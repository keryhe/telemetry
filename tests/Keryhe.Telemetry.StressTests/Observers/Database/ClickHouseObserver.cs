using System.Data.Common;
using System.Globalization;
using ClickHouse.Client.ADO;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// ClickHouse observer. There are no row locks, so the "lock" sample is pressure instead: long-running
/// queries, active parts per table and per partition, the merge backlog, and unfinished mutations
/// (lightweight <c>DELETE</c> and <c>ALTER ... UPDATE</c>). Where the relational providers count
/// deadlocks, this counts <c>DelayedInserts</c>/<c>RejectedInserts</c> deltas from <c>system.events</c> and
/// <c>TOO_MANY_PARTS</c> from <c>system.errors</c>. Slowest SQL is <c>system.query_log</c> grouped by
/// <c>normalized_query_hash</c>.
/// </summary>
public sealed class ClickHouseObserver : DatabaseObserverBase
{
    private static readonly string[] PressureEvents = ["DelayedInserts", "RejectedInserts", "DelayedInsertsMilliseconds", "DistributedDelayedInserts"];
    private const int LongRunningSeconds = 5;

    private readonly string _connectionString;
    private Dictionary<string, double> _eventsBefore = [];
    private double _tooManyPartsBefore;
    private string _since = "";

    public ClickHouseObserver(string connectionString, Func<DateTime, CancellationToken, Task<string>>? readLogs = null) : base(readLogs) =>
        _connectionString = connectionString;

    public override string Provider => "ClickHouse";

    protected override DbConnection CreateConnection() => new ClickHouseConnection(_connectionString);

    private async Task<Dictionary<string, double>> ReadEventsAsync(CancellationToken cancellationToken) =>
        (await QueryAsync(
            $"SELECT event, value FROM system.events WHERE event IN ({string.Join(", ", PressureEvents.Select(e => $"'{e}'"))})", cancellationToken))
        .ToDictionary(r => Str(r[0])!, r => Num(r[1]));

    private Task<double> TooManyPartsAsync(CancellationToken cancellationToken) =>
        ScalarAsync("SELECT sum(value) FROM system.errors WHERE name = 'TOO_MANY_PARTS'", cancellationToken);

    protected override async Task BeginCoreAsync(CancellationToken cancellationToken)
    {
        _eventsBefore = await ReadEventsAsync(cancellationToken);
        _tooManyPartsBefore = await TooManyPartsAsync(cancellationToken);
    }

    public override Task<LockSample> SampleAsync(CancellationToken cancellationToken) => GuardedSampleAsync(async () =>
    {
        var gauges = new List<Gauge>();

        var longRunning = (await QueryAsync(
            $"SELECT left(query, {MaxQueryChars}), elapsed * 1000 FROM system.processes " +
            $"WHERE elapsed > {LongRunningSeconds} AND query NOT LIKE '%system.processes%' ORDER BY elapsed DESC LIMIT 20", cancellationToken))
            .Select(r => new LongQuery(Str(r[0]) ?? "", Num(r[1]))).ToList();
        gauges.Add(new Gauge("long_running_queries", null, longRunning.Count));

        foreach (var r in await QueryAsync(
            "SELECT table, count(), max(part_count) FROM (SELECT table, partition, count() AS part_count FROM system.parts " +
            "WHERE database = currentDatabase() AND active GROUP BY table, partition) GROUP BY table", cancellationToken))
        {
            // Parts per partition is what trips TOO_MANY_PARTS, so the worst partition is the reading that matters.
            gauges.Add(new Gauge("partitions", Str(r[0]), Num(r[1])));
            gauges.Add(new Gauge("parts_max_per_partition", Str(r[0]), Num(r[2])));
        }
        foreach (var r in await QueryAsync(
            "SELECT table, count() FROM system.parts WHERE database = currentDatabase() AND active GROUP BY table", cancellationToken))
            gauges.Add(new Gauge("parts_active", Str(r[0]), Num(r[1])));

        gauges.Add(new Gauge("merges_running", null, await ScalarAsync("SELECT count() FROM system.merges", cancellationToken)));
        foreach (var r in await QueryAsync(
            "SELECT table, count() FROM system.mutations WHERE database = currentDatabase() AND NOT is_done GROUP BY table", cancellationToken))
            gauges.Add(new Gauge("mutations_pending", Str(r[0]), Num(r[1])));

        return (new List<LockWait>(), gauges, longRunning);
    });

    public override async Task<LockSummary> EndAsync(CancellationToken cancellationToken)
    {
        var events = await ReadEventsAsync(cancellationToken);
        var deltas = PressureEvents.ToDictionary(e => e, e => events.GetValueOrDefault(e) - _eventsBefore.GetValueOrDefault(e));
        var tooManyParts = await TooManyPartsAsync(cancellationToken) - _tooManyPartsBefore;
        var counters = new Dictionary<string, double>(deltas.ToDictionary(kv => kv.Key, kv => kv.Value)) { ["TOO_MANY_PARTS"] = tooManyParts };
        // No deadlocks exist here; the count is left at zero and the pressure counters carry the signal.
        return new LockSummary(0, counters, [], [], new Dictionary<string, string>(), []);
    }

    public override async Task ResetAsync(CancellationToken cancellationToken)
    {
        // query_log cannot be reset without dropping it, so the window is a start timestamp taken from the server's own clock.
        _since = Str((await QueryAsync("SELECT toString(now64(6))", cancellationToken))[0][0])!;
        await Task.CompletedTask;
    }

    public override async Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken)
    {
        await ExecuteAsync("SYSTEM FLUSH LOGS", cancellationToken);
        var since = string.IsNullOrEmpty(_since) ? "toDateTime64('1970-01-01 00:00:00', 6)" : $"toDateTime64('{EscapeLiteral(_since)}', 6)";
        string Sql(string order, string having) => $"""
            SELECT left(any(query), 500), count(), sum(query_duration_ms), avg(query_duration_ms), max(query_duration_ms), sum(read_rows + written_rows)
            FROM system.query_log
            WHERE type = 'QueryFinish' AND event_time_microseconds >= {since} AND current_database = currentDatabase()
              AND query NOT LIKE '%system.%' AND query NOT LIKE 'SYSTEM %'
            GROUP BY normalized_query_hash {having}
            ORDER BY {order} DESC LIMIT {top}
            """;
        var byTotal = await QueryAsync(Sql("sum(query_duration_ms)", ""), cancellationToken);
        var byMean = await QueryAsync(Sql("avg(query_duration_ms)", "HAVING count() >= 5"), cancellationToken);
        return new StatementStatsSnapshot("system.query_log", byTotal.Select(ToStat).ToList(), byMean.Select(ToStat).ToList());
    }

    private static StatementStat ToStat(object?[] r) => new(Str(r[0]) ?? "", Long(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Long(r[5]));

    public override Task<IReadOnlyList<ServerSetting>> ReadSettingsAsync(CancellationToken cancellationToken) => SettingsAsync(
        "SELECT 'version', version() " +
        "UNION ALL SELECT name, value FROM system.server_settings WHERE name IN ('max_server_memory_usage', 'max_server_memory_usage_to_ram_ratio', " +
        "'max_concurrent_queries', 'background_pool_size', 'background_merges_mutations_concurrency_ratio', 'mark_cache_size', 'uncompressed_cache_size') " +
        "UNION ALL SELECT name, value FROM system.settings WHERE name IN ('max_threads', 'max_memory_usage', 'max_insert_threads', 'async_insert', 'max_execution_time')",
        cancellationToken);

    public override async Task<IReadOnlyList<DiagnosticSection>> ReadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try { await ExecuteAsync("SYSTEM FLUSH LOGS", cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the sections below then read whatever was already flushed */ }
        var since = string.IsNullOrEmpty(_since) ? "toDateTime64('1970-01-01 00:00:00', 6)" : $"toDateTime64('{EscapeLiteral(_since)}', 6)";
        return
        [
            await SectionAsync("Rows and bytes read per query shape", "system.query_log over the measured window, top 20 by rows read.",
                ["Query", "Calls", "Rows read", "Bytes read", "Rows read / call", "Mean ms", "Max ms", "Rows written"], $"""
                SELECT left(any(query), 200), count(), sum(read_rows), sum(read_bytes), round(avg(read_rows)), round(avg(query_duration_ms), 1),
                       max(query_duration_ms), sum(written_rows)
                FROM system.query_log
                WHERE type = 'QueryFinish' AND event_time_microseconds >= {since} AND current_database = currentDatabase()
                  AND query NOT LIKE '%system.%' AND query NOT LIKE 'SYSTEM %'
                GROUP BY normalized_query_hash ORDER BY sum(read_rows) DESC LIMIT 20
                """, cancellationToken),

            await SectionAsync("Part events (merges, mutations, inserts)", "system.part_log over the measured window, per table and event type.",
                ["Table", "Event", "Count", "Rows", "Total s", "Max ms", "Errors"], $"""
                SELECT table, toString(event_type), count(), sum(rows), round(sum(duration_ms) / 1000, 1), max(duration_ms), countIf(error != 0)
                FROM system.part_log
                WHERE database = currentDatabase() AND event_time_microseconds >= {since}
                GROUP BY table, event_type ORDER BY table, event_type
                """, cancellationToken),

            await SectionAsync("Mutations", "Every mutation on this database (lightweight DELETE, ALTER ... UPDATE), grouped by table and command.",
                ["Table", "Command", "Count", "Done", "Max parts to do", "Last failure"], """
                SELECT table, left(command, 150), count(), countIf(is_done), max(parts_to_do), anyIf(latest_fail_reason, latest_fail_reason != '')
                FROM system.mutations WHERE database = currentDatabase()
                GROUP BY table, left(command, 150) ORDER BY count() DESC LIMIT 30
                """, cancellationToken),

            await SectionAsync("Merges still running at the end", null,
                ["Table", "Elapsed s", "Progress", "Parts", "Rows read", "Is mutation"], """
                SELECT table, round(elapsed, 1), round(progress, 2), num_parts, rows_read, is_mutation
                FROM system.merges WHERE database = currentDatabase() ORDER BY elapsed DESC
                """, cancellationToken),
        ];
    }

    /// <summary>
    /// The read side's view: <c>resources</c>/<c>metrics</c> are collapsed to one row per id before joining (a pending merge would otherwise multiply
    /// counts). On a 2.x schema spans are a <c>ReplacingMergeTree</c> and are read as <c>LIMIT 1 BY trace_id, span_id</c>, with the raw span count returned too so the
    /// report can show pending merge duplicates. On 3.0.0 (recognised by <c>trace_index</c>) spans are a plain <c>MergeTree</c> that stores a re-delivered span again, so
    /// they are counted as they are and there is no merge-pending number.
    /// </summary>
    public override async Task<RowCounts> CountRowsAsync(long cutoffNanos, CancellationToken cancellationToken)
    {
        var cells = new List<RowCountCell>();
        var replacingSpans = (long)await ScalarAsync(
            "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name = 'trace_index'", cancellationToken) == 0;
        foreach (var t in CountedTable.All)
        {
            var age = $"if(t.{t.TimeColumn} >= {cutoffNanos}, 0, 1)";
            var source = t.Table == "spans" && replacingSpans
                ? $"(SELECT resource_id, {t.TimeColumn} FROM spans LIMIT 1 BY trace_id, span_id)"
                : t.Table;
            var joins = t.ViaMetric
                ? "JOIN (SELECT id, any(resource_id) AS resource_id FROM metrics GROUP BY id) m ON m.id = t.metric_id " +
                  "JOIN (SELECT id, any(tenant_id) AS tenant_id FROM resources GROUP BY id) r ON r.id = m.resource_id"
                : "JOIN (SELECT id, any(tenant_id) AS tenant_id FROM resources GROUP BY id) r ON r.id = t.resource_id";
            var rows = await QueryAsync($"SELECT r.tenant_id, {age} AS backdated, count() FROM {source} t {joins} GROUP BY r.tenant_id, backdated",
                cancellationToken, commandTimeoutSeconds: 1800);
            cells.AddRange(rows.Select(r => new RowCountCell(Long(r[0]), t.Table, Long(r[1]) == 1, Long(r[2]))));
        }
        cells.AddRange((await CountRollupRowsAsync(cutoffNanos, cancellationToken)).Cells);
        if (!replacingSpans) return new RowCounts(cells, null);
        var raw = await ScalarAsync("SELECT count() FROM spans", cancellationToken);
        return new RowCounts(cells, (long)raw);
    }

    public override async Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken)
    {
        // Active parts before merges finish still hold ReplacingMergeTree duplicates, so the row counts can run high.
        var rows = await QueryAsync(
            "SELECT table, sum(rows), sum(bytes_on_disk) FROM system.parts WHERE database = currentDatabase() AND active GROUP BY table ORDER BY table",
            cancellationToken);
        return rows.Select(r => new TableStat(Str(r[0])!, Long(r[1]), true, Long(r[2]))).ToList();
    }
}
