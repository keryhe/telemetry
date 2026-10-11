using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Retention by day (README U7), per tenant: a tenant's day folder is deleted once the whole day is before the cutoff's day of that tenant's own window, so rows in
/// the cutoff's own day survive until it ends, as on ClickHouse. Every table following the signal's window goes (derived
/// tables with their raw one); the returned count is the raw tables' rows, read from the manifests before the delete. The
/// day folders carry every table's hours of that day, compacted or not, so this needs no knowledge of compaction. Phase 6
/// adds the logs and metrics tables' tests and the compactor's interplay.
/// </summary>
public sealed class ParquetRetentionSweeper(IParquetFileStore store) : IRetentionSweeper
{
    public Task<int> DeleteOldTracesAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Traces, RetentionSignal.Traces, policy, cancellationToken);

    public Task<int> DeleteOldLogRecordsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Logs, RetentionSignal.Logs, policy, cancellationToken);

    public Task<int> DeleteOldMetricDataPointsAsync(RetentionPolicy policy, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Metrics, RetentionSignal.Metrics, policy, cancellationToken);

    private async Task<int> SweepAsync(ParquetRetentionWindow window, RetentionSignal signal, RetentionPolicy policy, CancellationToken ct)
    {
        // A folder can be expired for someone up to the cutoff day of the SHORTEST window in effect (the latest cutoff); each
        // (tenant, day) is then checked against its own tenant's cutoff (a tenant with no control-plane row takes the
        // built-in window, which DistinctWindows always includes). The policy refuses a negative window, so a cutoff in the
        // future cannot drop everything.
        var latestCutoffDay = DateOnly.FromDateTime(policy.Now - policy.DistinctWindows(signal)[0]);

        long removed = 0;
        foreach (var table in ParquetTables.All.Where(t => t.Retention == window))
        {
            var days = store.EnumeratePartitions(table.Name, null, DateOnly.MinValue, latestCutoffDay.AddDays(-1))
                .GroupBy(p => (p.TenantId, p.Date)).ToList();
            foreach (var day in days)
            {
                ct.ThrowIfCancellationRequested();
                // Expired once the whole day is before the cutoff's day.
                if (day.Key.Date >= DateOnly.FromDateTime(policy.CutoffFor(signal, day.Key.TenantId))) continue;
                if (!table.Derived)
                    removed += (await ParquetManifests.ResolveAsync(store, [.. day], ct)).Sum(f => f.File.Rows);
                await store.DeleteDayAsync(table.Name, day.Key.TenantId, day.Key.Date, ct);
            }
        }
        return (int)Math.Min(removed, int.MaxValue);
    }
}
