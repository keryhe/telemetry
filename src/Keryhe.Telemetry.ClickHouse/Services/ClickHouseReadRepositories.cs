using System.Data.Common;
using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.ClickHouse.Services;

// =============================================================================
// ClickHouse read-side plumbing. The trace, log, metric and resource repositories each live in their own file and
// implement the Core interfaces directly (plans/clickhouse-redesign README R4); what is left here is the connection factory,
// the retention sweeper and the rollup reader, which keeps the shared rollup SQL. Connections come from ConnectionStrings:Api.
// =============================================================================

internal static class ClickHouseConnectionFactory
{
    public static async Task<DbConnection> OpenReadAsync(string connectionString, CancellationToken ct)
    {
        var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// What <see cref="ClickHouseRetentionSweeper"/> has already deleted, so the hourly sweep does not repeat a
/// lightweight delete over the same expired days: <c>(table, window, tenants) -> the day it deleted up to</c>.
/// A singleton (the sweeper is scoped); a restart empties it, which only repeats each delete once.
/// </summary>
public sealed class ClickHouseRetentionMemory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _deletedThrough = new();

    internal bool Covers(string key, DateTime through) => _deletedThrough.TryGetValue(key, out var done) && done >= through;

    internal void Remember(string key, DateTime through) => _deletedThrough[key] = through;

    /// <summary>Forgets everything (for a test that empties the tables under the sweeper).</summary>
    public void Clear() => _deletedThrough.Clear();
}

/// <summary>
/// ClickHouse retention (per tenant, day granularity). Per signal:
/// <list type="number">
/// <item><c>ALTER TABLE ... DROP PARTITION</c> for every day wholly before the cutoff of the LONGEST window in
/// effect (<see cref="RetentionPolicy.LongestWindow"/>, the built-in one included for orphans); the raw rows are
/// counted from <c>system.parts</c> just before the drop.</item>
/// <item>For each shorter window, one lightweight <c>DELETE</c> of the tenants on that window over the days between
/// the longest cutoff and their own (and a second one for tenants with no control-plane row when the window is the
/// built-in one). The time bound prunes partitions and <c>tenant_id</c> leads every sort key; the logs table also
/// carries a <c>toStartOfFiveMinutes(timestamp)</c> bound and the points tables a <c>toStartOfHour(time)</c> bound,
/// or the sort key would not prune. A delete already done for the same days is skipped
/// (<see cref="ClickHouseRetentionMemory"/>).</item>
/// <item>The unpartitioned <c>metric_series</c>/<c>metric_catalog</c> rows nothing wrote to within a tenant's window
/// are removed per window.</item>
/// </list>
/// A day is removed once the whole day is older than the cutoff: rows in the cutoff's own day survive until it ends.
/// The returned counts are the raw rows removed (an approximate count for the deletes, which is acceptable for a log line).
/// </summary>
public class ClickHouseRetentionSweeper(IConfiguration configuration, ClickHouseRetentionMemory? memory = null)
    : IRetentionSweeper
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;
    private readonly ClickHouseRetentionMemory _memory = memory ?? new ClickHouseRetentionMemory();

    private enum TimeKind { DateTime, Day, Nano }

    /// <param name="Prune">An extra sort-key bound the table needs for the time range to prune: "five" or "hour".</param>
    private sealed record TableSpec(string Name, TimeKind Kind, string Column, bool Counted, string? Prune = null);

    // trace_index and the rollup tables are partitioned by the same days as the table they derive from, so they go
    // with it (not counted in the returned rows).
    private static readonly TableSpec[] TraceTables =
    [
        new("spans", TimeKind.DateTime, "start_time", true),
        new("trace_index", TimeKind.Day, "day", false),
        new("request_rollup_minute", TimeKind.Nano, "bucket_start_unix_nano", false)
    ];

    private static readonly TableSpec[] LogTables =
    [
        new("log_records", TimeKind.DateTime, "timestamp", true, "five"),
        new("log_rollup_minute", TimeKind.Nano, "bucket_start_unix_nano", false)
    ];

    // The five points tables. metric_series and metric_catalog are not partitioned (one row per series / metric), so a
    // lightweight DELETE removes the rows nothing has written to within the window; their counts are not part of the
    // total (row model, "Metric catalog").
    private static readonly TableSpec[] PointsTables =
    [
        new("gauge_points", TimeKind.DateTime, "time", true, "hour"),
        new("sum_points", TimeKind.DateTime, "time", true, "hour"),
        new("histogram_points", TimeKind.DateTime, "time", true, "hour"),
        new("exp_histogram_points", TimeKind.DateTime, "time", true, "hour"),
        new("summary_points", TimeKind.DateTime, "time", true, "hour")
    ];

    public Task<int> DeleteOldTracesAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
        => SweepAsync(RetentionSignal.Traces, TraceTables, policy, cancellationToken);

    public Task<int> DeleteOldLogRecordsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
        => SweepAsync(RetentionSignal.Logs, LogTables, policy, cancellationToken);

    public async Task<int> DeleteOldMetricDataPointsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
    {
        var removed = await SweepAsync(RetentionSignal.Metrics, PointsTables, policy, cancellationToken);
        await DeleteStaleSeriesAsync(policy, cancellationToken);
        return removed;
    }

    private async Task<int> SweepAsync(RetentionSignal signal, TableSpec[] tables, RetentionPolicy policy, CancellationToken ct)
    {
        // A day partition (yyyymmdd) is fully expired once its whole day is before the cutoff's day.
        var longest = policy.LongestWindow(signal);
        var dropBoundary = (policy.Now - longest).Date;
        var cutoffDay = uint.Parse(dropBoundary.ToString("yyyyMMdd"));

        await using var conn = await ClickHouseConnectionFactory.OpenReadAsync(_connectionString, ct);

        long removed = 0;
        foreach (var table in tables)
        {
            var partitions = (await conn.QueryAsync<PartitionRow>(new CommandDefinition(
                """
                SELECT partition_id AS Partition, sum(rows) AS Rows
                FROM system.parts
                WHERE database = currentDatabase() AND table = @table AND active AND toUInt32OrZero(partition_id) < @cutoffDay
                GROUP BY partition_id
                """,
                new { table = table.Name, cutoffDay }, cancellationToken: ct))).ToList();

            foreach (var partition in partitions)
            {
                // The partition id comes from system.parts (digits only), not from a caller.
                await conn.ExecuteAsync(new CommandDefinition(
                    $"ALTER TABLE {table.Name} DROP PARTITION ID '{partition.Partition}'", cancellationToken: ct));
                if (table.Counted) removed += partition.Rows;
            }
        }

        // Shorter windows: the days between the longest cutoff (everything before it is gone) and their own.
        var known = policy.KnownTenants.OrderBy(id => id).ToList();
        foreach (var window in policy.DistinctWindows(signal))
        {
            if (window >= longest) continue;
            var through = (policy.Now - window).Date;
            if (through <= dropBoundary) continue;

            var groups = new List<(string Key, string TenantPredicate)>();
            var ids = policy.TenantsWithWindow(signal, window);
            if (ids.Count > 0)
                groups.Add(($"{window.Ticks}:in:{string.Join(',', ids)}", $"tenant_id IN ({string.Join(',', ids)})"));
            if (window == policy.BuiltInWindow(signal))
                groups.Add(($"{window.Ticks}:orphans:{string.Join(',', known)}",
                    known.Count == 0 ? "1" : $"tenant_id NOT IN ({string.Join(',', known)})"));

            foreach (var (key, tenantPredicate) in groups)
            foreach (var table in tables)
            {
                var memoryKey = $"{table.Name}:{key}";
                if (_memory.Covers(memoryKey, through)) continue;

                var where = $"{tenantPredicate} AND {TimePredicate(table, dropBoundary, through)}";
                if (table.Counted)
                    removed += await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT toInt64(count()) FROM {table.Name} WHERE {where}", cancellationToken: ct));
                await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {table.Name} WHERE {where} SETTINGS lightweight_deletes_sync = 1", cancellationToken: ct));
                _memory.Remember(memoryKey, through);
            }
        }

        return (int)Math.Min(removed, int.MaxValue);
    }

    /// <summary>The expired-day range <c>[from, to)</c> for a table, with the extra sort-key bound it needs to prune.</summary>
    private static string TimePredicate(TableSpec table, DateTime from, DateTime to)
    {
        switch (table.Kind)
        {
            case TimeKind.Day:
                return $"{table.Column} >= toDate('{from:yyyy-MM-dd}') AND {table.Column} < toDate('{to:yyyy-MM-dd}')";
            case TimeKind.Nano:
                return $"{table.Column} >= {TimeConversion.DateTimeToUnixNano(from)} AND {table.Column} < {TimeConversion.DateTimeToUnixNano(to)}";
            default:
                var f = DateTimeLiteral(from);
                var t = DateTimeLiteral(to);
                var predicate = $"{table.Column} >= {f} AND {table.Column} < {t}";
                if (table.Prune is { } fn)
                {
                    var bucket = fn == "five" ? $"toStartOfFiveMinutes({table.Column})" : $"toStartOfHour({table.Column})";
                    predicate += $" AND {bucket} >= {f} AND {bucket} < {t}";
                }
                return predicate;
        }
    }

    private static string DateTimeLiteral(DateTime utc) => $"toDateTime64('{utc:yyyy-MM-dd HH:mm:ss}', 9, 'UTC')";

    /// <summary>
    /// <c>metric_series</c>/<c>metric_catalog</c> rows nothing has written to within the tenant's metrics window, one
    /// statement per distinct window (tenants grouped), plus the orphan form for tenants with no control-plane row at the
    /// built-in window.
    /// </summary>
    private async Task DeleteStaleSeriesAsync(RetentionPolicy policy, CancellationToken ct)
    {
        var known = policy.KnownTenants.OrderBy(id => id).ToList();
        await using var conn = await ClickHouseConnectionFactory.OpenReadAsync(_connectionString, ct);
        foreach (var window in policy.DistinctWindows(RetentionSignal.Metrics))
        {
            var cutoff = DateTimeLiteral(policy.Now - window);
            var predicates = new List<string>();
            var ids = policy.TenantsWithWindow(RetentionSignal.Metrics, window);
            if (ids.Count > 0) predicates.Add($"tenant_id IN ({string.Join(',', ids)})");
            if (window == policy.BuiltInWindow(RetentionSignal.Metrics))
                predicates.Add(known.Count == 0 ? "1" : $"tenant_id NOT IN ({string.Join(',', known)})");

            foreach (var predicate in predicates)
            foreach (var table in new[] { "metric_series", "metric_catalog" })
                await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {table} WHERE {predicate} AND last_seen < {cutoff} SETTINGS lightweight_deletes_sync = 1",
                    cancellationToken: ct));
        }
    }

    private sealed class PartitionRow
    {
        public string Partition { get; set; } = null!;
        public long Rows { get; set; }
    }
}

public class ClickHouseRollupReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : RollupReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";
    protected override string BigintExpr(string expression) => $"toInt64({expression})";
}
