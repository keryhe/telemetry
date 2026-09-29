using System.Data.Common;
using System.Globalization;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// Shared plumbing for the per-provider observers. Every provider's connection is a
/// <see cref="DbConnection"/>, so queries run through one generic ADO helper that returns plain
/// rows; each subclass supplies only its connection and its provider-specific SQL. Every call
/// opens a pooled connection of its own, so a sample never queues behind a long-running one.
/// </summary>
public abstract partial class DatabaseObserverBase : ILockObserver, IStatementStatsCollector, ITableStatsReader
{
    /// <summary>Longest a query text is kept in a sample (SQL is truncated server-side too, this bounds the JSON).</summary>
    protected const int MaxQueryChars = 400;

    private readonly Func<DateTime, CancellationToken, Task<string>>? _readLogs;

    protected DatabaseObserverBase(Func<DateTime, CancellationToken, Task<string>>? readLogs) => _readLogs = readLogs;

    public abstract string Provider { get; }

    /// <summary>When <see cref="BeginAsync"/> ran, in the local clock the container log timestamps are compared against.</summary>
    protected DateTime BeganUtc { get; private set; }

    protected abstract DbConnection CreateConnection();

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        BeganUtc = DateTime.UtcNow;
        await BeginCoreAsync(cancellationToken);
    }

    protected abstract Task BeginCoreAsync(CancellationToken cancellationToken);
    public abstract Task<LockSample> SampleAsync(CancellationToken cancellationToken);
    public abstract Task<LockSummary> EndAsync(CancellationToken cancellationToken);
    public abstract Task ResetAsync(CancellationToken cancellationToken);
    public abstract Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The server's effective configuration (memory, durability, isolation), read once when the observers start.</summary>
    public abstract Task<IReadOnlyList<ServerSetting>> ReadSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Provider diagnostics for the measured window (counter baselines are taken in <see cref="ResetAsync"/>). Never throws per section.</summary>
    public abstract Task<IReadOnlyList<DiagnosticSection>> ReadDiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>Runs one diagnostic query as a text table; a failure becomes the section's <see cref="DiagnosticSection.Error"/>.</summary>
    protected async Task<DiagnosticSection> SectionAsync(string name, string? note, string[] columns, string sql, CancellationToken cancellationToken, int commandTimeoutSeconds = 60)
    {
        try
        {
            var rows = await QueryAsync(sql, cancellationToken, commandTimeoutSeconds);
            return new DiagnosticSection(name, note, columns, rows.Select(r => (IReadOnlyList<string?>)r.Select(Cell).ToList()).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticSection(name, note, columns, [], ex.Message);
        }
    }

    /// <summary>Builds a section from values computed in code (counter deltas and the like), guarding the computation the same way.</summary>
    protected static async Task<DiagnosticSection> ComputedSectionAsync(string name, string? note, string[] columns, Func<Task<IEnumerable<object?[]>>> rows)
    {
        try
        {
            return new DiagnosticSection(name, note, columns, (await rows()).Select(r => (IReadOnlyList<string?>)r.Select(Cell).ToList()).ToList());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DiagnosticSection(name, note, columns, [], ex.Message);
        }
    }

    /// <summary>Reads name/value rows as settings; a failure is recorded as a single <c>(error)</c> setting rather than thrown.</summary>
    protected async Task<IReadOnlyList<ServerSetting>> SettingsAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            return (await QueryAsync(sql, cancellationToken)).Select(r => new ServerSetting(Str(r[0]) ?? "", Str(r[1]))).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new ServerSetting("(error)", ex.Message)];
        }
    }

    private static string? Cell(object? value) => value switch
    {
        null => null,
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
        DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan t => t.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms",
        _ => Str(value)
    };

    /// <summary>Container stdout+stderr since the run began; empty when no log reader was supplied.</summary>
    protected async Task<string> ReadLogSinceBeginAsync(CancellationToken cancellationToken) =>
        _readLogs is null ? "" : await _readLogs(BeganUtc.AddSeconds(-1), cancellationToken);

    protected async Task<List<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken, int commandTimeoutSeconds = 30)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);
        return await QueryAsync(conn, sql, cancellationToken, commandTimeoutSeconds);
    }

    protected static async Task<List<object?[]>> QueryAsync(DbConnection conn, string sql, CancellationToken cancellationToken, int commandTimeoutSeconds = 30)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = commandTimeoutSeconds;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            for (var i = 0; i < row.Length; i++)
                if (row[i] is DBNull) row[i] = null;
            rows.Add(row);
        }
        return rows;
    }

    protected async Task ExecuteAsync(string sql, CancellationToken cancellationToken, int commandTimeoutSeconds = 60)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = commandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected async Task<double> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(sql, cancellationToken);
        return rows.Count == 0 ? 0 : Num(rows[0][0]);
    }

    protected static string? Str(object? value) => value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    protected static double Num(object? value) =>
        value is null ? 0 : value is IConvertible c and not string ? c.ToDouble(CultureInfo.InvariantCulture)
        : double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    protected static long Long(object? value) => (long)Num(value);

    protected static string? Trim(object? value)
    {
        var s = Str(value);
        return s is null ? null : s.Length <= MaxQueryChars ? s : s[..MaxQueryChars];
    }

    /// <summary>Builds a sample from whatever the provider's sampler produced; a thrown error becomes a failed sample.</summary>
    protected static async Task<LockSample> GuardedSampleAsync(Func<Task<(List<LockWait> Waits, List<Gauge> Gauges, List<LongQuery> Long)>> take)
    {
        var at = DateTimeOffset.UtcNow;
        try
        {
            var (waits, gauges, longQueries) = await take();
            return new LockSample(at, waits, gauges, longQueries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LockSample(at, [], [], [], ex.Message);
        }
    }

    protected static string EscapeLiteral(string value) => value.Replace("'", "''");
}
