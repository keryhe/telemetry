using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IRetentionSweeper"/>. The three <c>Delete*</c> sweeps are one
/// shared shape for the three relational providers whose tables are not partitioned -- PostgreSQL,
/// SQL Server and MySQL: spans and log records are deleted in bounded batches per tenant, through
/// the <c>(tenant_id, time)</c> access path every one of those tables leads with, and the metric
/// data-point tables in bounded batches through their time index. A provider supplies only its
/// batched-DELETE dialect (<see cref="BatchedDeleteSql"/>). ClickHouse (<c>DROP PARTITION</c>)
/// overrides the sweeps outright: it drops whole partitions, so its retention granularity is the
/// day, not the row.
///
/// The tenants to sweep are the ones with data (<c>SELECT DISTINCT tenant_id FROM resources</c>), not
/// the control plane's <c>tenants</c> table: the two may be different databases, and data whose tenant
/// has been removed from the control plane must still expire.
/// </summary>
public abstract class RetentionSweeperBase : IRetentionSweeper
{
    /// <summary>Opens a connection to the telemetry database.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>Rows removed per DELETE statement by the batched sweeps. Bounded so a large first sweep never runs as one long transaction or escalates its locks.</summary>
    protected virtual int DeleteBatchSize => 5_000;

    /// <summary>How long a sweep pauses after a full batch, so ingestion's own writes interleave with it.</summary>
    protected virtual TimeSpan PauseBetweenBatches => TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// One batched DELETE statement against <paramref name="table"/>, removing at most
    /// <see cref="DeleteBatchSize"/> rows matching <paramref name="predicate"/> (which references the
    /// <c>@cutoff</c> parameter, and <c>@tenantId</c> when the sweep is per tenant). Postgres has
    /// neither <c>DELETE TOP</c> nor <c>DELETE ... LIMIT</c>; SQL Server and MySQL do, each in its own form.
    /// </summary>
    protected virtual string BatchedDeleteSql(string table, string predicate)
        => throw new NotSupportedException($"{GetType().Name} does not implement batched deletes; it overrides the sweeps.");

    /// <summary>Session setup for a sweep connection (SQL Server: <c>DEADLOCK_PRIORITY LOW</c>, so a sweep is the deadlock victim rather than an ingest flush).</summary>
    protected virtual Task PrepareSweepConnectionAsync(DbConnection conn, CancellationToken ct) => Task.CompletedTask;

    public virtual async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await SweepPerTenantAsync("spans", "start_time_unix_nano", retentionPeriod, cancellationToken);
        // The request rollup follows the traces window (summary-rollups plan, decision 8); not counted.
        await SweepPerTenantAsync("request_rollup_minute", "bucket_start_unix_nano", retentionPeriod, cancellationToken);
        return removed;
    }

    public virtual async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await SweepPerTenantAsync("log_records", "time_unix_nano", retentionPeriod, cancellationToken);
        // The log rollup follows the logs window; not counted.
        await SweepPerTenantAsync("log_rollup_minute", "bucket_start_unix_nano", retentionPeriod, cancellationToken);
        return removed;
    }

    public virtual async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = CutoffNano(retentionPeriod);
        await using var conn = await OpenConnectionAsync(cancellationToken);
        await PrepareSweepConnectionAsync(conn, cancellationToken);

        var total = 0;
        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
            total += await BatchedDeleteAsync(conn, BatchedDeleteSql(table, "time_unix_nano < @cutoff"), new { cutoff }, cancellationToken);
        return total;
    }

    /// <summary>Deletes <paramref name="table"/>'s expired rows tenant by tenant, through its <c>(tenant_id, time)</c> access path.</summary>
    protected async Task<int> SweepPerTenantAsync(string table, string timeColumn, TimeSpan retentionPeriod, CancellationToken ct)
    {
        var cutoff = CutoffNano(retentionPeriod);
        await using var conn = await OpenConnectionAsync(ct);
        await PrepareSweepConnectionAsync(conn, ct);

        var tenantIds = (await conn.QueryAsync<long>(new CommandDefinition("SELECT DISTINCT tenant_id FROM resources", cancellationToken: ct))).ToList();
        var sql = BatchedDeleteSql(table, $"tenant_id = @tenantId AND {timeColumn} < @cutoff");

        var total = 0;
        foreach (var tenantId in tenantIds)
            total += await BatchedDeleteAsync(conn, sql, new { cutoff, tenantId }, ct);
        return total;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> until a batch comes back short. Each statement commits on its own --
    /// do NOT wrap the loop in a transaction, which would reproduce the long-running transaction the
    /// batching exists to avoid. It terminates because the cutoff is computed once, so rows arriving
    /// during the sweep are never older than it.
    /// </summary>
    private async Task<int> BatchedDeleteAsync(DbConnection conn, string sql, object parameters, CancellationToken ct)
    {
        var total = 0;
        int batch;
        do
        {
            batch = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
            total += batch;
            if (batch == DeleteBatchSize) await Task.Delay(PauseBetweenBatches, ct);
        } while (batch == DeleteBatchSize);
        return total;
    }

    /// <summary>
    /// The retention cutoff as unix nanoseconds.
    ///
    /// The negative guard is load-bearing, not defensive noise: a negative period puts the cutoff
    /// in the FUTURE, at which point every predicate here matches every row in the table. On a
    /// retention API that is the difference between a no-op and erasing the telemetry store, so it
    /// fails loudly rather than quietly succeeding.
    /// </summary>
    protected static long CutoffNano(TimeSpan retentionPeriod)
    {
        if (retentionPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod),
                "Retention period cannot be negative; that would delete all telemetry.");

        return TimeConversion.DateTimeToUnixNano(DateTime.UtcNow - retentionPeriod);
    }
}
