using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Retention by day (README U7): a tenant's day folder is deleted once the whole day is before the cutoff's day, so rows in
/// the cutoff's own day survive until it ends, as on ClickHouse. Every table following the signal's window goes (derived
/// tables with their raw one); the returned count is the raw tables' rows, read from the manifests before the delete. The
/// day folders carry every table's hours of that day, compacted or not, so this needs no knowledge of compaction. Phase 6
/// adds the logs and metrics tables' tests and the compactor's interplay.
/// </summary>
public sealed class ParquetRetentionSweeper(IParquetFileStore store) : IRetentionSweeper
{
    public Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Traces, retentionPeriod, cancellationToken);

    public Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Logs, retentionPeriod, cancellationToken);

    public Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(ParquetRetentionWindow.Metrics, retentionPeriod, cancellationToken);

    private async Task<int> SweepAsync(ParquetRetentionWindow window, TimeSpan retentionPeriod, CancellationToken ct)
    {
        // A negative period would put the cutoff in the future and drop everything: fail loudly, as the relational sweeps do.
        if (retentionPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod), "Retention period cannot be negative; that would delete all telemetry.");
        var cutoffDay = DateOnly.FromDateTime(DateTime.UtcNow - retentionPeriod);

        long removed = 0;
        foreach (var table in ParquetTables.All.Where(t => t.Retention == window))
        {
            var days = store.EnumeratePartitions(table.Name, null, DateOnly.MinValue, cutoffDay.AddDays(-1))
                .GroupBy(p => (p.TenantId, p.Date)).ToList();
            foreach (var day in days)
            {
                ct.ThrowIfCancellationRequested();
                if (!table.Derived)
                    removed += (await ParquetManifests.ResolveAsync(store, [.. day], ct)).Sum(f => f.File.Rows);
                await store.DeleteDayAsync(table.Name, day.Key.TenantId, day.Key.Date, ct);
            }
        }
        return (int)Math.Min(removed, int.MaxValue);
    }
}
