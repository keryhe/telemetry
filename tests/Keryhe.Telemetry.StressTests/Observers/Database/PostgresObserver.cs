using System.Data.Common;
using Npgsql;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// PostgreSQL observer. Lock waits come from <c>pg_stat_activity</c> with
/// <c>pg_blocking_pids()</c> (the blocker chain and both query texts) and <c>pg_locks</c> counts;
/// deadlocks are the delta of <c>pg_stat_database.deadlocks</c>, with the reports themselves taken
/// from the container log (<c>log_lock_waits</c> is on); slowest SQL is <c>pg_stat_statements</c>.
/// </summary>
public sealed class PostgresObserver : DatabaseObserverBase
{
    private readonly string _connectionString;
    private double _deadlocksBefore;
    private Dictionary<string, double> _checkpointsBefore = [];
    private DateTime _diagnosticsSince = DateTime.UtcNow;

    public PostgresObserver(string connectionString, Func<DateTime, CancellationToken, Task<string>>? readLogs = null)
        : base(readLogs)
    {
        _connectionString = connectionString;
    }

    public override string Provider => "PostgreSQL";

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

    // Lock counts by mode and relation.
    private const string LockCountsSql = $"""
        SELECT c.relname AS relation,
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

    public override async Task ResetAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("SELECT pg_stat_statements_reset()", cancellationToken);
        _diagnosticsSince = DateTime.UtcNow;
        try { _checkpointsBefore = await ReadCheckpointCountersAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _checkpointsBefore = []; }
    }

    private static readonly string[] SettingNames =
    [
        "shared_buffers", "effective_cache_size", "work_mem", "maintenance_work_mem", "max_connections",
        "max_wal_size", "min_wal_size", "checkpoint_timeout", "checkpoint_completion_target", "wal_compression", "wal_buffers",
        "synchronous_commit", "fsync", "full_page_writes", "default_transaction_isolation",
        "autovacuum", "autovacuum_max_workers", "autovacuum_naptime", "autovacuum_vacuum_cost_delay", "autovacuum_vacuum_cost_limit",
        "autovacuum_vacuum_scale_factor", "autovacuum_vacuum_insert_scale_factor", "gin_pending_list_limit",
        "max_worker_processes", "shared_preload_libraries", "pg_stat_statements.track",
    ];

    public override Task<IReadOnlyList<ServerSetting>> ReadSettingsAsync(CancellationToken cancellationToken) => SettingsAsync(
        "SELECT 'server_version', version() UNION ALL " +
        $"SELECT name, current_setting(name) FROM pg_settings WHERE name IN ({string.Join(", ", SettingNames.Select(n => $"'{n}'"))})",
        cancellationToken);

    /// <summary>
    /// Cumulative checkpoint and WAL counters. PostgreSQL 17 moved the checkpoint counters from <c>pg_stat_bgwriter</c> to
    /// <c>pg_stat_checkpointer</c>; both shapes are read into the same names.
    /// </summary>
    private async Task<Dictionary<string, double>> ReadCheckpointCountersAsync(CancellationToken cancellationToken)
    {
        var hasCheckpointer = await ScalarAsync("SELECT count(*) FROM pg_catalog.pg_class WHERE relname = 'pg_stat_checkpointer'", cancellationToken) > 0;
        var sql = hasCheckpointer
            ? "SELECT num_timed, num_requested, write_time, sync_time, buffers_written FROM pg_stat_checkpointer"
            : "SELECT checkpoints_timed, checkpoints_req, checkpoint_write_time, checkpoint_sync_time, buffers_checkpoint FROM pg_stat_bgwriter";
        var c = (await QueryAsync(sql, cancellationToken))[0];
        var w = (await QueryAsync("SELECT wal_records, wal_bytes, wal_buffers_full FROM pg_stat_wal", cancellationToken))[0];
        return new Dictionary<string, double>
        {
            ["checkpoints_timed"] = Num(c[0]), ["checkpoints_requested"] = Num(c[1]), ["checkpoint_write_ms"] = Num(c[2]),
            ["checkpoint_sync_ms"] = Num(c[3]), ["checkpoint_buffers_written"] = Num(c[4]),
            ["wal_records"] = Num(w[0]), ["wal_bytes"] = Num(w[1]), ["wal_buffers_full"] = Num(w[2]),
        };
    }

    public override async Task<IReadOnlyList<DiagnosticSection>> ReadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var sections = new List<DiagnosticSection>
        {
            await SectionAsync("Foreign-key checks (FOR KEY SHARE)",
                "pg_stat_statements since the measured window began (track = all, so checks fired inside COPY are included).",
                ["Statement", "Calls", "Total ms", "Mean ms"],
                $"SELECT left(query, 200), calls, round(total_exec_time::numeric, 1), round(mean_exec_time::numeric, 4) FROM pg_stat_statements " +
                $"WHERE dbid = {DatabaseOid} AND query LIKE '%FOR KEY SHARE%' ORDER BY calls DESC LIMIT 20", cancellationToken),

            await ComputedSectionAsync("Checkpoints and WAL", "Deltas over the measured window, except the last row: the server's own 'checkpoints are occurring too frequently' warnings in the container log since the observers started (warm-up included).",
                ["Counter", "Value"], async () =>
                {
                    var now = await ReadCheckpointCountersAsync(cancellationToken);
                    var rows = now.Select(kv => new object?[] { kv.Key, kv.Value - _checkpointsBefore.GetValueOrDefault(kv.Key) }).ToList();
                    var seconds = (DateTime.UtcNow - _diagnosticsSince).TotalSeconds;
                    var count = now["checkpoints_timed"] + now["checkpoints_requested"] - _checkpointsBefore.GetValueOrDefault("checkpoints_timed") - _checkpointsBefore.GetValueOrDefault("checkpoints_requested");
                    rows.Add(["window_seconds", Math.Round(seconds)]);
                    rows.Add(["mean_checkpoint_interval_seconds", count > 0 ? Math.Round(seconds / count, 1) : null]);
                    rows.Add(["wal_mb_per_second", Math.Round((now["wal_bytes"] - _checkpointsBefore.GetValueOrDefault("wal_bytes")) / 1048576.0 / Math.Max(1, seconds), 2)]);
                    rows.Add(["checkpoints_too_frequent_warnings", ServerLogParsers.CountOccurrences(await ReadLogSinceBeginAsync(cancellationToken), "checkpoints are occurring too frequently")]);
                    return rows;
                }),

            await SectionAsync("Vacuum and dead tuples", "Per table; cumulative since the container started.",
                ["Table", "Live tuples", "Dead tuples", "Autovacuums", "Autoanalyzes", "Last autovacuum"],
                "SELECT s.relname, sum(s.n_live_tup), sum(s.n_dead_tup), sum(s.autovacuum_count), sum(s.autoanalyze_count), max(s.last_autovacuum) FROM pg_stat_user_tables s " +
                "GROUP BY 1 ORDER BY 3 DESC LIMIT 20", cancellationToken),
        };

        return sections;
    }

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
        var tables = (await QueryAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename", cancellationToken)).Select(r => Str(r[0])!).ToList();

        var stats = new List<TableStat>();
        foreach (var table in tables)
        {
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            var size = $"pg_total_relation_size('{EscapeLiteral(quoted)}')";
            var rows = await QueryAsync($"SELECT (SELECT count(*) FROM {quoted}), {size}", cancellationToken, commandTimeoutSeconds: 300);
            stats.Add(new TableStat(table, Long(rows[0][0]), false, Long(rows[0][1])));
        }
        return stats;
    }
}
