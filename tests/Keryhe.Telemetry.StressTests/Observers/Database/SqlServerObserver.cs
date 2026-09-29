using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// SQL Server observer. Lock waits come from <c>sys.dm_os_waiting_tasks</c> (<c>LCK%</c> waits, with
/// resource description and both sessions' current SQL); lock escalation is the delta of
/// <c>index_lock_promotion_count</c>; deadlocks are counted from the Deadlocks performance counter and
/// their graphs read from the <c>system_health</c> ring buffer; slowest SQL is Query Store, falling back
/// to <c>sys.dm_exec_query_stats</c>. It also confirms the API's reads really run under SNAPSHOT (decision 35).
/// </summary>
public sealed class SqlServerObserver : DatabaseObserverBase
{
    /// <summary>The application name <c>SqlServerReadConnection</c> gives every API read connection.</summary>
    public const string ReadApplicationName = "Keryhe.Telemetry.Api.Read";

    private const int SnapshotIsolationLevel = 5;

    private readonly string _connectionString;
    private double _deadlocksBefore;
    private double _promotionsBefore;
    private int _readRequestsSeen;
    private int _nonSnapshotReadRequestsSeen;
    private string _statementSource = "sys.query_store_runtime_stats";
    private DateTime _statementsSince = DateTime.UtcNow;

    public SqlServerObserver(string connectionString, Func<DateTime, CancellationToken, Task<string>>? readLogs = null) : base(readLogs) =>
        _connectionString = connectionString;

    public override string Provider => "SqlServer";

    protected override DbConnection CreateConnection() =>
        // A distinct application name keeps the observer out of the API-read session check below.
        new SqlConnection(new SqlConnectionStringBuilder(_connectionString) { ApplicationName = "Keryhe.StressTests.Observer" }.ConnectionString);

    private static readonly string WaitsSql = $"""
        SELECT wt.session_id, wt.blocking_session_id, LEFT(bt.text, {MaxQueryChars}), LEFT(kt.text, {MaxQueryChars}),
               wt.resource_description, wt.wait_type, wt.wait_duration_ms
        FROM sys.dm_os_waiting_tasks wt
        JOIN sys.dm_exec_sessions s ON s.session_id = wt.session_id AND s.is_user_process = 1 AND s.database_id = DB_ID()
        LEFT JOIN sys.dm_exec_connections bc ON bc.session_id = wt.session_id
        OUTER APPLY sys.dm_exec_sql_text(bc.most_recent_sql_handle) bt
        LEFT JOIN sys.dm_exec_connections kc ON kc.session_id = wt.blocking_session_id
        OUTER APPLY sys.dm_exec_sql_text(kc.most_recent_sql_handle) kt
        WHERE wt.wait_type LIKE 'LCK%' AND wt.blocking_session_id IS NOT NULL
        """;

    private const string DeadlockCounterSql =
        "SELECT ISNULL(SUM(cntr_value), 0) FROM sys.dm_os_performance_counters WHERE counter_name = 'Number of Deadlocks/sec' AND instance_name = '_Total'";

    private const string PromotionsSql =
        "SELECT ISNULL(SUM(index_lock_promotion_count), 0) FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL)";

    protected override async Task BeginCoreAsync(CancellationToken cancellationToken)
    {
        _deadlocksBefore = await ScalarAsync(DeadlockCounterSql, cancellationToken);
        _promotionsBefore = await ScalarAsync(PromotionsSql, cancellationToken);
    }

    public override Task<LockSample> SampleAsync(CancellationToken cancellationToken) => GuardedSampleAsync(async () =>
    {
        var waits = (await QueryAsync(WaitsSql, cancellationToken)).Select(r =>
            new LockWait(Str(r[0]), Str(r[1]), Trim(r[2]), Trim(r[3]), Str(r[4]), Str(r[5]), Num(r[6]))).ToList();
        // Lock promotions are read only at the start and end: the DMV behind them is expensive enough to disturb a 1s cadence.
        var gauges = new List<Gauge> { new("lock_waits", null, waits.Count) };

        // Only running requests are checked: SQL Server resets an idle pooled connection's isolation level, so
        // an idle session reads as READ COMMITTED even though every API read sets SNAPSHOT before it queries.
        var read = await QueryAsync(
            $"SELECT COUNT(*), ISNULL(SUM(CASE WHEN r.transaction_isolation_level <> {SnapshotIsolationLevel} THEN 1 ELSE 0 END), 0) " +
            "FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id = r.session_id " +
            $"WHERE s.is_user_process = 1 AND s.program_name = '{ReadApplicationName}' AND r.session_id <> @@SPID", cancellationToken);
        var (total, nonSnapshot) = ((int)Num(read[0][0]), (int)Num(read[0][1]));
        gauges.Add(new Gauge("api_read_requests", null, total));
        gauges.Add(new Gauge("api_read_requests_not_snapshot", null, nonSnapshot));
        _readRequestsSeen += total;
        _nonSnapshotReadRequestsSeen += nonSnapshot;
        return (waits, gauges, new List<LongQuery>());
    });

    public override async Task<LockSummary> EndAsync(CancellationToken cancellationToken)
    {
        var deadlocks = await ScalarAsync(DeadlockCounterSql, cancellationToken) - _deadlocksBefore;
        var promotions = await ScalarAsync(PromotionsSql, cancellationToken) - _promotionsBefore;

        var artifacts = new Dictionary<string, string>();
        var graphs = await ReadDeadlockGraphsAsync(cancellationToken);
        for (var i = 0; i < graphs.Count; i++)
            artifacts[$"sqlserver-deadlock-{i + 1}.xml"] = graphs[i];

        var checks = new List<ObserverCheck>();
        // A reader blocked on ingestion would be a regression, so the report lists the isolation check explicitly.
        checks.Add(_readRequestsSeen == 0
            ? new ObserverCheck("API reads run under SNAPSHOT", CheckOutcome.NotChecked, "No running API read request was caught by a sample (a run with no browsers or other readers has none), so the isolation level was not checked.")
            : new ObserverCheck("API reads run under SNAPSHOT", _nonSnapshotReadRequestsSeen == 0 ? CheckOutcome.Passed : CheckOutcome.Failed,
                $"{_readRequestsSeen} running API read request(s) sampled; {_nonSnapshotReadRequestsSeen} not under SNAPSHOT."));

        return new LockSummary(
            (long)deadlocks,
            new Dictionary<string, double> { ["deadlocks"] = deadlocks, ["lock_promotions"] = promotions },
            graphs.Count == 0 ? [] : graphs.Select(g => g.Length > 2000 ? g[..2000] + " ..." : g).ToList(),
            [], artifacts, checks);
    }

    /// <summary>Deadlock graphs recorded by the <c>system_health</c> session since the run began (its ring buffer keeps only recent ones).</summary>
    private async Task<List<string>> ReadDeadlockGraphsAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "SELECT CAST(t.target_data AS nvarchar(max)) FROM sys.dm_xe_session_targets t " +
            "JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address " +
            "WHERE s.name = 'system_health' AND t.target_name = 'ring_buffer'", cancellationToken);
        var graphs = new List<string>();
        if (rows.Count == 0 || Str(rows[0][0]) is not { } xml) return graphs;

        var since = BeganUtc.AddSeconds(-1);
        foreach (var e in XDocument.Parse(xml).Descendants("event").Where(e => (string?)e.Attribute("name") == "xml_deadlock_report"))
        {
            if (!DateTime.TryParse((string?)e.Attribute("timestamp"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) || at < since)
                continue;
            var graph = e.Descendants("deadlock").FirstOrDefault();
            if (graph is not null) graphs.Add(graph.ToString());
        }
        return graphs;
    }

    public override async Task ResetAsync(CancellationToken cancellationToken)
    {
        _statementsSince = DateTime.UtcNow;
        var state = await QueryAsync("SELECT actual_state_desc FROM sys.database_query_store_options", cancellationToken);
        if (state.Count > 0 && Str(state[0][0]) == "READ_WRITE")
        {
            _statementSource = "sys.query_store_runtime_stats";
            await ExecuteAsync("ALTER DATABASE CURRENT SET QUERY_STORE CLEAR", cancellationToken);
        }
        else
        {
            // Not clearing the plan cache (that would disturb the workload), so this fallback filters by last execution time.
            _statementSource = "sys.dm_exec_query_stats";
        }
    }

    public override async Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken)
    {
        List<object?[]> byTotal, byMean;
        if (_statementSource == "sys.query_store_runtime_stats")
        {
            await ExecuteAsync("EXEC sp_query_store_flush_db", cancellationToken);
            // Durations in Query Store are microseconds. The mean is total over executions, not an average of averages.
            string Sql(string order, string having) => $"""
                SELECT TOP ({top}) LEFT(qt.query_sql_text, 500), SUM(rs.count_executions),
                       SUM(rs.avg_duration * rs.count_executions) / 1000.0,
                       SUM(rs.avg_duration * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0) / 1000.0,
                       MAX(rs.max_duration) / 1000.0, SUM(CAST(rs.avg_rowcount * rs.count_executions AS bigint))
                FROM sys.query_store_query q
                JOIN sys.query_store_query_text qt ON qt.query_text_id = q.query_text_id
                JOIN sys.query_store_plan p ON p.query_id = q.query_id
                JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
                WHERE qt.query_sql_text NOT LIKE '%query_store%' AND qt.query_sql_text NOT LIKE '%dm_os_%' AND qt.query_sql_text NOT LIKE '%dm_exec_%' AND qt.query_sql_text NOT LIKE '%dm_db_%'
                GROUP BY q.query_id, qt.query_sql_text {having}
                ORDER BY {order} DESC
                """;
            byTotal = await QueryAsync(Sql("SUM(rs.avg_duration * rs.count_executions)", ""), cancellationToken);
            byMean = await QueryAsync(Sql("SUM(rs.avg_duration * rs.count_executions) / NULLIF(SUM(rs.count_executions), 0)", "HAVING SUM(rs.count_executions) >= 5"), cancellationToken);
        }
        else
        {
            var since = _statementsSince.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string Sql(string order, string having) => $"""
                SELECT TOP ({top}) LEFT(SUBSTRING(t.text, s.statement_start_offset / 2 + 1,
                           (CASE WHEN s.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE s.statement_end_offset END - s.statement_start_offset) / 2 + 1), 500),
                       s.execution_count, s.total_elapsed_time / 1000.0, s.total_elapsed_time / NULLIF(s.execution_count, 0) / 1000.0,
                       s.max_elapsed_time / 1000.0, s.total_rows
                FROM sys.dm_exec_query_stats s CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) t
                WHERE s.last_execution_time >= '{since}' AND t.text NOT LIKE '%dm_exec_%' AND t.text NOT LIKE '%dm_os_%' AND t.text NOT LIKE '%dm_db_%' {having}
                ORDER BY {order} DESC
                """;
            byTotal = await QueryAsync(Sql("s.total_elapsed_time", ""), cancellationToken);
            byMean = await QueryAsync(Sql("s.total_elapsed_time / NULLIF(s.execution_count, 0)", "AND s.execution_count >= 5"), cancellationToken);
        }
        return new StatementStatsSnapshot(_statementSource, byTotal.Select(ToStat).ToList(), byMean.Select(ToStat).ToList());
    }

    private static StatementStat ToStat(object?[] r) => new(Str(r[0]) ?? "", Long(r[1]), Num(r[2]), Num(r[3]), Num(r[4]), Long(r[5]));

    public override async Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync("""
            SELECT t.name, SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END), SUM(ps.reserved_page_count) * 8192
            FROM sys.tables t JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id
            GROUP BY t.name ORDER BY t.name
            """, cancellationToken);
        return rows.Select(r => new TableStat(Str(r[0])!, Long(r[1]), false, Long(r[2]))).ToList();
    }
}
