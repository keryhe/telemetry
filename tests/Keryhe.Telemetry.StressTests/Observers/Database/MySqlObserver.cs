using System.Data.Common;
using MySqlConnector;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// MySQL observer. Lock waits come from <c>sys.innodb_lock_waits</c> (the view over
/// <c>performance_schema.data_lock_waits</c>, with both queries and the blocker chain; wait age is second-precision);
/// <c>Innodb_row_lock_current_waits</c> is sampled, and <c>Innodb_row_lock_waits</c>/<c>_time</c> and deadlocks
/// are deltas. Deadlock dumps come from the container error log (<c>innodb_print_all_deadlocks</c>); slowest SQL
/// is <c>performance_schema.events_statements_summary_by_digest</c>. Needs a user that can read
/// <c>performance_schema</c>, which is why the diagnostics container runs as root.
/// </summary>
public sealed class MySqlObserver : DatabaseObserverBase
{
    private readonly string _connectionString;
    private double _rowLockWaitsBefore, _rowLockTimeBefore, _deadlocksBefore;
    private Dictionary<string, double> _innodbBefore = [];
    private double _historyLengthMax;

    public MySqlObserver(string connectionString, Func<DateTime, CancellationToken, Task<string>>? readLogs = null) : base(readLogs) =>
        _connectionString = connectionString;

    public override string Provider => "MySql";

    protected override DbConnection CreateConnection() => new MySqlConnection(_connectionString);

    private const string StatusSql = "SELECT VARIABLE_NAME, VARIABLE_VALUE FROM performance_schema.global_status WHERE VARIABLE_NAME IN ('Innodb_row_lock_current_waits', 'Innodb_row_lock_waits', 'Innodb_row_lock_time')";
    private const string DeadlocksSql = "SELECT `COUNT` FROM information_schema.INNODB_METRICS WHERE NAME = 'lock_deadlocks'";

    private async Task<Dictionary<string, double>> ReadStatusAsync(CancellationToken cancellationToken) =>
        (await QueryAsync(StatusSql, cancellationToken)).ToDictionary(r => Str(r[0])!, r => Num(r[1]));

    protected override async Task BeginCoreAsync(CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(cancellationToken);
        _rowLockWaitsBefore = status.GetValueOrDefault("Innodb_row_lock_waits");
        _rowLockTimeBefore = status.GetValueOrDefault("Innodb_row_lock_time");
        _deadlocksBefore = await ScalarAsync(DeadlocksSql, cancellationToken);
    }

    public override Task<LockSample> SampleAsync(CancellationToken cancellationToken) => GuardedSampleAsync(async () =>
    {
        var waits = (await QueryAsync($"""
            SELECT waiting_pid, blocking_pid, LEFT(waiting_query, {MaxQueryChars}), LEFT(blocking_query, {MaxQueryChars}),
                   CONCAT(locked_table, IFNULL(CONCAT('.', locked_index), '')), locked_type, TIMESTAMPDIFF(MICROSECOND, wait_started, NOW()) / 1000
            FROM sys.innodb_lock_waits
            """, cancellationToken)).Select(r =>
            new LockWait(Str(r[0]), Str(r[1]), Trim(r[2]), Trim(r[3]), Str(r[4]), Str(r[5]), Num(r[6]))).ToList();

        var status = await ReadStatusAsync(cancellationToken);
        // Purge lag: a growing history list means undo is piling up behind long transactions (large retention deletes, for one).
        var history = await ScalarAsync(HistoryLengthSql, cancellationToken);
        _historyLengthMax = Math.Max(_historyLengthMax, history);
        var gauges = new List<Gauge>
        {
            new("history_list_length", null, history),
            new("lock_waits", null, waits.Count),
            new("row_lock_current_waits", null, status.GetValueOrDefault("Innodb_row_lock_current_waits")),
            new("row_lock_waits_total", null, status.GetValueOrDefault("Innodb_row_lock_waits")),
            new("row_lock_time_ms_total", null, status.GetValueOrDefault("Innodb_row_lock_time")),
        };
        return (waits, gauges, new List<LongQuery>());
    });

    public override async Task<LockSummary> EndAsync(CancellationToken cancellationToken)
    {
        var status = await ReadStatusAsync(cancellationToken);
        var deadlocks = await ScalarAsync(DeadlocksSql, cancellationToken) - _deadlocksBefore;
        return new LockSummary(
            (long)deadlocks,
            new Dictionary<string, double>
            {
                ["deadlocks"] = deadlocks,
                ["row_lock_waits"] = status.GetValueOrDefault("Innodb_row_lock_waits") - _rowLockWaitsBefore,
                ["row_lock_time_ms"] = status.GetValueOrDefault("Innodb_row_lock_time") - _rowLockTimeBefore,
            },
            ServerLogParsers.MySqlDeadlocks(await ReadLogSinceBeginAsync(cancellationToken)), [], new Dictionary<string, string>(), []);
    }

    private const string HistoryLengthSql = "SELECT `COUNT` FROM information_schema.INNODB_METRICS WHERE NAME = 'trx_rseg_history_len'";

    private static readonly string[] InnodbCounters =
    [
        "Innodb_buffer_pool_read_requests", "Innodb_buffer_pool_reads", "Innodb_buffer_pool_wait_free", "Innodb_buffer_pool_pages_flushed",
        "Innodb_log_waits", "Innodb_os_log_written", "Innodb_data_fsyncs", "Innodb_rows_inserted", "Innodb_rows_deleted",
    ];

    private async Task<Dictionary<string, double>> ReadInnodbCountersAsync(CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT VARIABLE_NAME, VARIABLE_VALUE FROM performance_schema.global_status WHERE VARIABLE_NAME IN (" +
                          string.Join(", ", InnodbCounters.Select(c => $"'{c}'")) + ")", cancellationToken))
        .ToDictionary(r => Str(r[0])!, r => Num(r[1]), StringComparer.OrdinalIgnoreCase);

    public override async Task ResetAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("TRUNCATE TABLE performance_schema.events_statements_summary_by_digest", cancellationToken);
        try { _innodbBefore = await ReadInnodbCountersAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _innodbBefore = []; }
        _historyLengthMax = 0;
    }

    public override Task<IReadOnlyList<ServerSetting>> ReadSettingsAsync(CancellationToken cancellationToken) => SettingsAsync(
        "SELECT VARIABLE_NAME, VARIABLE_VALUE FROM performance_schema.global_variables WHERE VARIABLE_NAME IN (" +
        "'version', 'innodb_buffer_pool_size', 'innodb_buffer_pool_instances', 'log_bin', 'sync_binlog', 'binlog_format', " +
        "'innodb_flush_log_at_trx_commit', 'innodb_redo_log_capacity', 'innodb_log_file_size', 'innodb_log_files_in_group', 'innodb_doublewrite', " +
        "'innodb_flush_method', 'innodb_io_capacity', 'transaction_isolation', 'innodb_lock_wait_timeout', 'innodb_deadlock_detect', 'max_connections') " +
        "ORDER BY VARIABLE_NAME", cancellationToken);

    public override async Task<IReadOnlyList<DiagnosticSection>> ReadDiagnosticsAsync(CancellationToken cancellationToken) =>
    [
        await ComputedSectionAsync("InnoDB buffer pool, redo and purge", "Deltas over the measured window; the hit ratio is 1 - disk reads / read requests.",
            ["Counter", "Value"], async () =>
            {
                var now = await ReadInnodbCountersAsync(cancellationToken);
                double Delta(string name) => now.GetValueOrDefault(name) - _innodbBefore.GetValueOrDefault(name);
                var rows = InnodbCounters.Select(c => new object?[] { c, Delta(c) }).ToList();
                var requests = Delta("Innodb_buffer_pool_read_requests");
                rows.Add(["buffer_pool_hit_ratio", requests > 0 ? Math.Round(1 - Delta("Innodb_buffer_pool_reads") / requests, 5) : null]);
                rows.Add(["history_list_length_end", await ScalarAsync(HistoryLengthSql, cancellationToken)]);
                rows.Add(["history_list_length_max_sampled", _historyLengthMax]);
                return rows;
            }),

        await SectionAsync("spans index I/O", "performance_schema.table_io_waits_summary_by_index_usage, cumulative since the container started (warm-up included). NULL index = full scans / row access without an index.",
            ["Index", "Fetches", "Inserts", "Updates", "Deletes", "Wait ms"], """
            SELECT INDEX_NAME, COUNT_FETCH, COUNT_INSERT, COUNT_UPDATE, COUNT_DELETE, ROUND(SUM_TIMER_WAIT / 1000000000, 1)
            FROM performance_schema.table_io_waits_summary_by_index_usage
            WHERE OBJECT_SCHEMA = DATABASE() AND OBJECT_NAME = 'spans' ORDER BY SUM_TIMER_WAIT DESC
            """, cancellationToken),

        await SectionAsync("Unused indexes", "sys.schema_unused_indexes: indexes with no reads since the container started.",
            ["Table", "Index"], "SELECT object_name, index_name FROM sys.schema_unused_indexes WHERE object_schema = DATABASE() ORDER BY object_name, index_name", cancellationToken),
    ];

    public override async Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken)
    {
        // Timer columns are picoseconds. The observer's own catalog queries are excluded.
        string Sql(string order, string extra) => $"""
            SELECT LEFT(DIGEST_TEXT, 500), COUNT_STAR, SUM_TIMER_WAIT / 1000000000, AVG_TIMER_WAIT / 1000000000,
                   MAX_TIMER_WAIT / 1000000000, SUM_ROWS_AFFECTED + SUM_ROWS_SENT
            FROM performance_schema.events_statements_summary_by_digest
            WHERE SCHEMA_NAME = DATABASE() AND DIGEST_TEXT IS NOT NULL
              AND DIGEST_TEXT NOT LIKE '%performance_schema%' AND DIGEST_TEXT NOT LIKE '%information_schema%' AND DIGEST_TEXT NOT LIKE '%innodb_lock_waits%' {extra}
            ORDER BY {order} DESC LIMIT {top}
            """;
        var byTotal = await QueryAsync(Sql("SUM_TIMER_WAIT", ""), cancellationToken);
        var byMean = await QueryAsync(Sql("AVG_TIMER_WAIT", "AND COUNT_STAR >= 5"), cancellationToken);
        return new StatementStatsSnapshot("performance_schema.events_statements_summary_by_digest",
            byTotal.Select(ToStat).ToList(), byMean.Select(ToStat).ToList());
    }

    private static StatementStat ToStat(object?[] r) => new(Str(r[0]) ?? "", Long(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Long(r[5]));

    public override async Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken)
    {
        // TABLE_ROWS is InnoDB's estimate; an exact COUNT(*) over a large table is a long full scan, so the report flags these as approximate.
        var rows = await QueryAsync(
            "SELECT TABLE_NAME, TABLE_ROWS, DATA_LENGTH + INDEX_LENGTH FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", cancellationToken);
        return rows.Select(r => new TableStat(Str(r[0])!, Long(r[1]), true, Long(r[2]))).ToList();
    }
}
