using System.Data.Common;
using Npgsql;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// PostgreSQL and TimescaleDB observers. Lock waits come from <c>pg_stat_activity</c> with
/// <c>pg_blocking_pids()</c> (the blocker chain and both query texts) and <c>pg_locks</c> counts;
/// deadlocks are the delta of <c>pg_stat_database.deadlocks</c>, with the reports themselves taken
/// from the container log (<c>log_lock_waits</c> is on); slowest SQL is <c>pg_stat_statements</c>.
/// Timescale adds chunk counts and background-job stats, plus the lock waits the chunk-creation
/// conflict CLAUDE.md describes show up as ordinary waits on <c>resources</c>/<c>instrumentation_scopes</c>.
/// </summary>
public sealed class PostgresObserver : DatabaseObserverBase
{
    private readonly string _connectionString;
    private readonly bool _timescale;
    private double _deadlocksBefore;

    public PostgresObserver(string connectionString, bool timescale, Func<DateTime, CancellationToken, Task<string>>? readLogs = null)
        : base(readLogs)
    {
        _connectionString = connectionString;
        _timescale = timescale;
    }

    public override string Provider => _timescale ? "Timescale" : "PostgreSQL";

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    private const string DatabaseOid = "(SELECT oid FROM pg_database WHERE datname = current_database())";

    private static readonly string WaitsSql = $"""
        SELECT a.pid, b.pid, left(a.query, {MaxQueryChars}), left(b.query, {MaxQueryChars}),
               COALESCE(l.relation::regclass::text, l.locktype), COALESCE(l.mode, a.wait_event),
               EXTRACT(EPOCH FROM (clock_timestamp() - a.state_change)) * 1000
        FROM pg_stat_activity a
        CROSS JOIN LATERAL unnest(pg_blocking_pids(a.pid)) AS bp(pid)
        JOIN pg_stat_activity b ON b.pid = bp.pid
        LEFT JOIN pg_locks l ON l.pid = a.pid AND NOT l.granted
        WHERE a.wait_event_type = 'Lock' AND a.datid = {DatabaseOid}
        """;

    // Lock counts by mode and relation. Timescale chunks are folded into one label: their names are
    // generated and would otherwise multiply the series by the chunk count.
    private const string LockCountsSql = $"""
        SELECT CASE WHEN c.relname LIKE '\_hyper\_%' THEN '(hypertable chunks)' ELSE c.relname END AS relation,
               l.mode, l.granted, count(*)
        FROM pg_locks l JOIN pg_class c ON c.oid = l.relation
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE l.locktype = 'relation' AND l.database = {DatabaseOid}
          AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND l.pid <> pg_backend_pid()
        GROUP BY 1, 2, 3
        """;

    protected override async Task BeginCoreAsync(CancellationToken cancellationToken) =>
        _deadlocksBefore = await ScalarAsync("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", cancellationToken);

    public override Task<LockSample> SampleAsync(CancellationToken cancellationToken) => GuardedSampleAsync(async () =>
    {
        var waits = (await QueryAsync(WaitsSql, cancellationToken)).Select(r =>
            new LockWait(Str(r[0]), Str(r[1]), Trim(r[2]), Trim(r[3]), Str(r[4]), Str(r[5]), Num(r[6]))).ToList();

        var gauges = new List<Gauge> { new("lock_waits", null, waits.Count) };
        foreach (var r in await QueryAsync(LockCountsSql, cancellationToken))
            gauges.Add(new Gauge(Convert.ToBoolean(r[2]) ? "locks_granted" : "locks_waiting", $"{Str(r[0])}/{Str(r[1])}", Num(r[3])));

        if (_timescale)
        {
            // Left join so a hypertable with no chunk yet reads as 0 rather than being absent.
            foreach (var r in await QueryAsync(
                "SELECT h.hypertable_name, count(c.chunk_name) FROM timescaledb_information.hypertables h " +
                "LEFT JOIN timescaledb_information.chunks c ON c.hypertable_schema = h.hypertable_schema AND c.hypertable_name = h.hypertable_name GROUP BY 1", cancellationToken))
                gauges.Add(new Gauge("chunks", Str(r[0]), Num(r[1])));

            // Background jobs (compression, continuous aggregate refresh): cumulative runs/failures and the last run's length.
            foreach (var r in await QueryAsync(
                "SELECT job_id, COALESCE(hypertable_name, ''), total_runs, total_failures, " +
                "COALESCE(EXTRACT(EPOCH FROM last_run_duration) * 1000, 0) FROM timescaledb_information.job_stats", cancellationToken))
            {
                var label = $"job {Str(r[0])} {Str(r[1])}".Trim();
                gauges.Add(new Gauge("job_total_runs", label, Num(r[2])));
                gauges.Add(new Gauge("job_total_failures", label, Num(r[3])));
                gauges.Add(new Gauge("job_last_run_ms", label, Num(r[4])));
            }
        }
        return (waits, gauges, new List<LongQuery>());
    });

    public override async Task<LockSummary> EndAsync(CancellationToken cancellationToken)
    {
        var deadlocks = await ScalarAsync("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()", cancellationToken) - _deadlocksBefore;
        var log = await ReadLogSinceBeginAsync(cancellationToken);
        var lockWaitLines = ServerLogParsers.PostgresLockWaits(log);
        return new LockSummary(
            (long)deadlocks,
            new Dictionary<string, double> { ["deadlocks"] = deadlocks, ["lock_waits_logged"] = lockWaitLines.Count(l => l.Contains("still waiting for")) },
            ServerLogParsers.PostgresDeadlocks(log), lockWaitLines, new Dictionary<string, string>(), []);
    }

    public override Task ResetAsync(CancellationToken cancellationToken) =>
        ExecuteAsync("SELECT pg_stat_statements_reset()", cancellationToken);

    public override async Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken)
    {
        const string columns = "left(query, 500), calls, total_exec_time, mean_exec_time, max_exec_time, rows";
        // The observer's own catalog queries are excluded so they never crowd out the workload.
        var where = $"WHERE dbid = {DatabaseOid} AND query NOT LIKE '%pg_stat_statements%' AND query NOT LIKE '%pg_stat_activity%' " +
                    "AND query NOT LIKE '%pg_locks%' AND query NOT LIKE '%pg_stat_database%'";
        var byTotal = await QueryAsync($"SELECT {columns} FROM pg_stat_statements {where} ORDER BY total_exec_time DESC LIMIT {top}", cancellationToken);
        var byMean = await QueryAsync($"SELECT {columns} FROM pg_stat_statements {where} AND calls >= 5 ORDER BY mean_exec_time DESC LIMIT {top}", cancellationToken);
        return new StatementStatsSnapshot("pg_stat_statements", byTotal.Select(ToStat).ToList(), byMean.Select(ToStat).ToList());
    }

    private static StatementStat ToStat(object?[] r) => new(Str(r[0]) ?? "", Long(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Long(r[5]));

    public override async Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken)
    {
        var hypertables = _timescale
            ? (await QueryAsync("SELECT hypertable_name FROM timescaledb_information.hypertables", cancellationToken)).Select(r => Str(r[0])!).ToHashSet()
            : [];
        var tables = (await QueryAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", cancellationToken)).Select(r => Str(r[0])!).ToList();

        var stats = new List<TableStat>();
        foreach (var table in tables)
        {
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            var size = hypertables.Contains(table)
                ? $"hypertable_size('{EscapeLiteral(quoted)}')"
                : $"pg_total_relation_size('{EscapeLiteral(quoted)}')";
            var rows = await QueryAsync($"SELECT (SELECT count(*) FROM {quoted}), {size}", cancellationToken, commandTimeoutSeconds: 300);
            stats.Add(new TableStat(table, Long(rows[0][0]), false, Long(rows[0][1])));
        }
        return stats;
    }
}
