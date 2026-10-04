namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>Rows in one table for one tenant, split by age: <see cref="Backdated"/> rows are older than the cutoff the caller passed.</summary>
public sealed record RowCountCell(long TenantId, string Table, bool Backdated, long Rows);

/// <param name="RawSpanRows">ClickHouse only: spans rows before <c>ReplacingMergeTree</c> has merged duplicates away. The difference from the deduplicated span count is the "pending merge duplicates" number.</param>
public sealed record RowCounts(IReadOnlyList<RowCountCell> Cells, long? RawSpanRows);

/// <summary>A table the correctness check counts, and how it reaches its tenant (stress-test plan, Phase 7).</summary>
public sealed record CountedTable(string Table, string TimeColumn, bool ViaMetric)
{
    public string Signal => Table switch
    {
        "spans" or Load.RollupTables.Request => "traces",
        "log_records" or Load.RollupTables.Log => "logs",
        _ => "metrics"
    };

    /// <summary>The rollup tables (plans/summary-rollups.md): counted by summing their count column, tenant straight from the row.</summary>
    public static bool IsRollup(string table) => table is Load.RollupTables.Request or Load.RollupTables.Log;

    public static IReadOnlyList<CountedTable> All { get; } =
    [
        new("spans", "start_time_unix_nano", false),
        new("log_records", "time_unix_nano", false),
        new("gauge_data_points", "time_unix_nano", true),
        new("sum_data_points", "time_unix_nano", true),
        new("histogram_data_points", "time_unix_nano", true),
        new("exponential_histogram_data_points", "time_unix_nano", true),
        new("summary_data_points", "time_unix_nano", true),
    ];
}

public abstract partial class DatabaseObserverBase
{
    /// <summary>
    /// The rollup tables' row counts: <c>SUM(request_count)</c> / <c>SUM(record_count)</c> per tenant and age (a bucket is backdated when its minute
    /// is before <paramref name="cutoffNanos"/>). Cheap enough to poll, which the correctness check does while the rollup catches up with the
    /// last closed minutes. The same SQL runs on every provider (ClickHouse's rollup tables are summed, never read with <c>FINAL</c>).
    /// </summary>
    public async Task<RowCounts> CountRollupRowsAsync(long cutoffNanos, CancellationToken cancellationToken)
    {
        var cells = new List<RowCountCell>();
        foreach (var (table, column) in new[] { (Load.RollupTables.Request, "request_count"), (Load.RollupTables.Log, "record_count") })
        {
            var age = $"CASE WHEN bucket_start_unix_nano >= {cutoffNanos} THEN 0 ELSE 1 END";
            var rows = await QueryAsync($"SELECT tenant_id, {age}, SUM({column}) FROM {table} GROUP BY tenant_id, {age}",
                cancellationToken, commandTimeoutSeconds: 1800);
            cells.AddRange(rows.Select(r => new RowCountCell(Long(r[0]), table, Long(r[1]) == 1, Long(r[2]))));
        }
        return new RowCounts(cells, null);
    }

    /// <summary>
    /// Counts what the database holds per tenant, table and age, exactly as the read side would see it. Tenants are reached
    /// through <c>resources.tenant_id</c> (data points via <c>metrics.resource_id</c>); a row is backdated when its own timestamp is
    /// before <paramref name="cutoffNanos"/>. Runs while ingestion is quiet, so plain <c>COUNT(*)</c> is exact.
    /// </summary>
    public virtual async Task<RowCounts> CountRowsAsync(long cutoffNanos, CancellationToken cancellationToken)
    {
        var cells = new List<RowCountCell>();
        foreach (var t in CountedTable.All)
        {
            var age = $"CASE WHEN t.{t.TimeColumn} >= {cutoffNanos} THEN 0 ELSE 1 END";
            var joins = t.ViaMetric
                ? $"JOIN metrics m ON m.id = t.metric_id JOIN resources r ON r.id = m.resource_id"
                : "JOIN resources r ON r.id = t.resource_id";
            var rows = await QueryAsync($"SELECT r.tenant_id, {age}, COUNT(*) FROM {t.Table} t {joins} GROUP BY r.tenant_id, {age}",
                cancellationToken, commandTimeoutSeconds: 1800);
            cells.AddRange(rows.Select(r => new RowCountCell(Long(r[0]), t.Table, Long(r[1]) == 1, Long(r[2]))));
        }
        cells.AddRange((await CountRollupRowsAsync(cutoffNanos, cancellationToken)).Cells);
        return new RowCounts(cells, null);
    }
}
