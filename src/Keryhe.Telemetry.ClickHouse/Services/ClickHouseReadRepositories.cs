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
/// ClickHouse retention: <c>ALTER TABLE ... DROP PARTITION</c> for every fully expired
/// day (the points, spans and logs tables), plus a lightweight <c>DELETE</c> of stale metric catalog and series rows. Spans, log records and the data-point tables are
/// partitioned by day, so retention granularity is the day: a partition is dropped once the whole
/// day is older than the cutoff, and rows in the cutoff's own day survive until it ends. The count
/// returned is the rows in the dropped partitions, read from <c>system.parts</c> just before the drop.
/// </summary>
public class ClickHouseRetentionSweeper(IConfiguration configuration)
    : RetentionSweeperBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // trace_index and the rollup tables are partitioned by the same days as the table they derive from,
    // so their expired partitions go with it (not counted in the returned rows).
    public override Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropExpiredPartitionsAsync(["spans"], ["trace_index", "request_rollup_minute"], retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropExpiredPartitionsAsync(["log_records"], ["log_rollup_minute"], retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DeleteOldMetricsAsync(retentionPeriod, cancellationToken);

    // The five points tables are dropped by day and counted. metric_series and metric_catalog are not partitioned
    // (one row per series / metric), so a lightweight DELETE removes the rows nothing has written to within the
    // window; their counts are not part of the total (row model, "Metric catalog").
    private static readonly string[] PointsTables =
        ["gauge_points", "sum_points", "histogram_points", "exp_histogram_points", "summary_points"];

    private async Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken ct)
    {
        var removed = await DropExpiredPartitionsAsync(PointsTables, [], retentionPeriod, ct);
        var cutoff = TimeConversion.UnixNanoToDateTime(CutoffNano(retentionPeriod));
        await using var conn = await OpenConnectionAsync(ct);
        foreach (var table in new[] { "metric_series", "metric_catalog" })
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE last_seen < toDateTime64(@cutoff, 9, 'UTC') SETTINGS lightweight_deletes_sync = 1",
                new { cutoff }, cancellationToken: ct));
        return removed;
    }

    private async Task<int> DropExpiredPartitionsAsync(
        IReadOnlyList<string> countedTables, IReadOnlyList<string> uncountedTables, TimeSpan retentionPeriod, CancellationToken ct)
    {
        // CutoffNano guards a negative period (a cutoff in the future would drop everything).
        var cutoff = TimeConversion.UnixNanoToDateTime(CutoffNano(retentionPeriod));
        // A day partition (yyyymmdd) is fully expired once its whole day is before the cutoff's day.
        var cutoffDay = uint.Parse(cutoff.ToString("yyyyMMdd"));

        await using var conn = await OpenConnectionAsync(ct);

        long removed = 0;
        foreach (var table in countedTables.Concat(uncountedTables))
        {
            var partitions = (await conn.QueryAsync<PartitionRow>(new CommandDefinition(
                """
                SELECT partition_id AS Partition, sum(rows) AS Rows
                FROM system.parts
                WHERE database = currentDatabase() AND table = @table AND active AND toUInt32OrZero(partition_id) < @cutoffDay
                GROUP BY partition_id
                """,
                new { table, cutoffDay }, cancellationToken: ct))).ToList();

            foreach (var partition in partitions)
            {
                // The partition id comes from system.parts (digits only), not from a caller.
                await conn.ExecuteAsync(new CommandDefinition(
                    $"ALTER TABLE {table} DROP PARTITION ID '{partition.Partition}'", cancellationToken: ct));
                if (countedTables.Contains(table)) removed += partition.Rows;
            }
        }
        return (int)Math.Min(removed, int.MaxValue);
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
