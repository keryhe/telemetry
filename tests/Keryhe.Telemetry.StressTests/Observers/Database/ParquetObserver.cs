using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Keryhe.Telemetry.Parquet;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// Parquet observer (plans/parquet-duckdb phase 6 decision 4). There is no database server: the "database" is a directory, so
/// there are no locks and no container to sample. The "lock" sample is file pressure instead (data files per table, files
/// waiting for the compactor, leftover temp files). Row counts are DuckDB over the files the manifests name, exactly what a
/// reader sees. Slowest SQL is parsed from the API host's console log, where the read engine writes one <c>Debug</c> line per
/// statement (<c>parquet-sql ms= files= sql=</c>; the launcher turns that category to Debug); <see cref="AttachHostLog"/> gives
/// the observer the path once the hosts are up. Resource use is the hosts' own (the harness's process metrics), not a container's.
/// </summary>
public sealed partial class ParquetObserver : DatabaseObserverBase
{
    private readonly string _root;
    private readonly LocalParquetFileStore _store;
    private string? _apiLogPath;
    private DateTimeOffset _since = DateTimeOffset.MinValue;
    private DateTimeOffset _filesSampledAt;
    private List<Gauge> _fileGauges = [];

    public ParquetObserver(string rootPath, Func<DateTime, CancellationToken, Task<string>>? readLogs = null) : base(readLogs)
    {
        _root = rootPath;
        _store = new LocalParquetFileStore(rootPath);
    }

    public override string Provider => "Parquet";

    public override void AttachHostLog(string apiHostLogPath) => _apiLogPath = apiHostLogPath;

    protected override DbConnection CreateConnection()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        return connection;
    }

    protected override Task BeginCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<ParquetPartition> Partitions(ParquetTable table)
        => _store.EnumeratePartitions(table.Name, null, DateOnly.MinValue, DateOnly.MaxValue);

    private async Task<List<ParquetResolvedFile>> FilesOfAsync(ParquetTable table, CancellationToken ct)
        => [.. await ParquetManifests.ResolveAsync(_store, [.. Partitions(table)], ct)];

    private static string FileList(IEnumerable<string> paths) => string.Join(", ", paths.Select(p => "'" + p.Replace("'", "''") + "'"));

    public override Task<LockSample> SampleAsync(CancellationToken cancellationToken) => GuardedSampleAsync(() =>
    {
        // Walking the tree is the cost; every five seconds is plenty for a file count.
        if (DateTimeOffset.UtcNow - _filesSampledAt > TimeSpan.FromSeconds(5))
        {
            var gauges = new List<Gauge>();
            long temp = 0;
            foreach (var table in ParquetTables.All)
            {
                var path = Path.Combine(_root, table.Name);
                if (!Directory.Exists(path)) continue;
                var files = Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories).Count();
                gauges.Add(new Gauge("data_files", table.Name, files));
                temp += Directory.EnumerateDirectories(path, ".tmp", SearchOption.AllDirectories).Sum(d => Directory.EnumerateFiles(d).Count());
            }
            gauges.Add(new Gauge("temp_files", null, temp));
            _fileGauges = gauges;
            _filesSampledAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult((new List<LockWait>(), new List<Gauge>(_fileGauges), new List<LongQuery>()));
    });

    public override Task<LockSummary> EndAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LockSummary(0, new Dictionary<string, double>(), [], [], new Dictionary<string, string>(), []));

    public override Task ResetAsync(CancellationToken cancellationToken)
    {
        _since = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"^(?<ts>\d{4}-\d\d-\d\dT[\d:.]+Z)\s.*?parquet-sql ms=(?<ms>[\d.]+) files=(?<files>\d+) sql=(?<sql>.*)$")]
    private static partial Regex StatementLine();

    private List<(string Sql, double Ms, long Files)> ReadStatements()
    {
        var list = new List<(string, double, long)>();
        if (_apiLogPath is null || !File.Exists(_apiLogPath)) return list;
        using var stream = new FileStream(_apiLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("parquet-sql ", StringComparison.Ordinal)) continue;
            var m = StatementLine().Match(line);
            if (!m.Success) continue;
            if (DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) && at < _since)
                continue;
            list.Add((m.Groups["sql"].Value, double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture), long.Parse(m.Groups["files"].Value, CultureInfo.InvariantCulture)));
        }
        return list;
    }

    public override Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken)
    {
        var stats = ReadStatements().GroupBy(s => s.Sql)
            .Select(g => new StatementStat(g.Key.Length <= MaxQueryChars ? g.Key : g.Key[..MaxQueryChars], g.Count(), g.Sum(s => s.Ms), g.Average(s => s.Ms), g.Max(s => s.Ms), g.Sum(s => s.Files)))
            .ToList();
        return Task.FromResult(new StatementStatsSnapshot(
            _apiLogPath is null ? "none (API host log not attached)" : "API host log (Debug 'parquet-sql' lines; Rows = files read)",
            [.. stats.OrderByDescending(s => s.TotalMs).Take(top)],
            [.. stats.Where(s => s.Calls >= 5).OrderByDescending(s => s.MeanMs).Take(top)]));
    }

    public override async Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken)
    {
        var stats = new List<TableStat>();
        foreach (var table in ParquetTables.All)
        {
            var files = await FilesOfAsync(table, cancellationToken);
            stats.Add(new TableStat(table.Name, files.Sum(f => f.File.Rows), false, files.Sum(f => f.File.Bytes)));
        }
        return stats;
    }

    public override async Task<IReadOnlyList<ServerSetting>> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = new List<ServerSetting> { new("root", _root), new("topology", "files on one machine; DuckDB in the API host (no database server)") };
        try
        {
            var version = await QueryAsync("SELECT version()", cancellationToken);
            settings.Insert(0, new ServerSetting("duckdb version", Str(version[0][0])));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { settings.Add(new ServerSetting("(error)", ex.Message)); }
        return settings;
    }

    public override async Task<IReadOnlyList<DiagnosticSection>> ReadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        return
        [
            await ComputedSectionAsync("Files per table",
                "Files the manifests name (what a reader opens) against all .parquet files on disk (the difference is superseded files waiting out the grace period, or orphans).",
                ["Table", "Read files", "Read MB", "Files on disk", "Disk MB", "Rows"], async () =>
                {
                    var rows = new List<object?[]>();
                    foreach (var table in ParquetTables.All)
                    {
                        var read = await FilesOfAsync(table, cancellationToken);
                        var path = Path.Combine(_root, table.Name);
                        var onDisk = Directory.Exists(path)
                            ? Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList() : [];
                        rows.Add([table.Name, read.Count, Math.Round(read.Sum(f => f.File.Bytes) / 1_048_576.0, 1), onDisk.Count,
                            Math.Round(onDisk.Sum(f => f.Length) / 1_048_576.0, 1), read.Sum(f => f.File.Rows)]);
                    }
                    return rows;
                }),
            await ComputedSectionAsync("Slowest statements by files read",
                "The API host's read statements over the measured window, ranked by files opened per call (the cost compaction removes).",
                ["Query", "Calls", "Files / call", "Mean ms", "Max ms"], () =>
                    Task.FromResult(ReadStatements().GroupBy(s => s.Sql)
                        .OrderByDescending(g => g.Average(s => s.Files)).Take(20)
                        .Select(g => new object?[] { g.Key.Length <= 200 ? g.Key : g.Key[..200], g.Count(), Math.Round(g.Average(s => s.Files), 1), Math.Round(g.Average(s => s.Ms), 1), g.Max(s => s.Ms) })
                        .ToList().AsEnumerable())),
        ];
    }

    /// <summary>
    /// Counts what a reader would see: each table's resolved files read through DuckDB, grouped by tenant and age. Every Parquet table
    /// carries its own <c>tenant_id</c> and partition time column, so the rollups are counted by the same code.
    /// </summary>
    public override async Task<RowCounts> CountRowsAsync(long cutoffNanos, CancellationToken cancellationToken)
    {
        var cells = new List<RowCountCell>();
        foreach (var t in CountedTable.All)
            cells.AddRange(await CountTableAsync(PhysicalName(t.Table), t.Table, null, cutoffNanos, cancellationToken));
        cells.AddRange((await CountRollupRowsAsync(cutoffNanos, cancellationToken)).Cells);
        return new RowCounts(cells, null);
    }

    public override async Task<RowCounts> CountRollupRowsAsync(long cutoffNanos, CancellationToken cancellationToken)
    {
        var cells = new List<RowCountCell>();
        cells.AddRange(await CountTableAsync(Load.RollupTables.Request, Load.RollupTables.Request, "request_count", cutoffNanos, cancellationToken));
        cells.AddRange(await CountTableAsync(Load.RollupTables.Log, Load.RollupTables.Log, "record_count", cutoffNanos, cancellationToken));
        return new RowCounts(cells, null);
    }

    /// <summary>The ledger's logical table names are the relational ones; Parquet names its point tables like ClickHouse.</summary>
    private static string PhysicalName(string logical) => logical switch
    {
        "gauge_data_points" => ParquetTables.GaugePoints,
        "sum_data_points" => ParquetTables.SumPoints,
        "histogram_data_points" => ParquetTables.HistogramPoints,
        "exponential_histogram_data_points" => ParquetTables.ExpHistogramPoints,
        "summary_data_points" => ParquetTables.SummaryPoints,
        _ => logical
    };

    private async Task<List<RowCountCell>> CountTableAsync(string physical, string logical, string? sumColumn, long cutoffNanos, CancellationToken ct)
    {
        var table = ParquetTables.ByName(physical);
        var files = await FilesOfAsync(table, ct);
        if (files.Count == 0) return [];
        var age = $"CASE WHEN {table.PartitionTimeColumn} >= {cutoffNanos} THEN 0 ELSE 1 END";
        var count = sumColumn is null ? "COUNT(*)" : $"SUM({sumColumn})";
        var rows = await QueryAsync(
            $"SELECT tenant_id, {age}, {count} FROM read_parquet([{FileList(files.Select(f => f.PathIn(_store)))}]) GROUP BY tenant_id, {age}",
            ct, commandTimeoutSeconds: 1800);
        return [.. rows.Select(r => new RowCountCell(Long(r[0]), logical, Long(r[1]) == 1, Long(r[2])))];
    }
}
