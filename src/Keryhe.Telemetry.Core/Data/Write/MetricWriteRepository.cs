using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Write;

public class MetricWriteRepository : IMetricWriteRepository
{
    private readonly TelemetryIngestionChannel _channel;
    private readonly ITelemetryWriteStore _store;
    private readonly ILogger<MetricWriteRepository> _logger;

    public MetricWriteRepository(
        TelemetryIngestionChannel channel,
        ITelemetryWriteStore store,
        ILogger<MetricWriteRepository> logger)
    {
        _channel = channel;
        _store = store;
        _logger = logger;
    }

    public async Task<long> StoreMetricAsync(
        MetricModel metric,
        CancellationToken cancellationToken = default)
    {
        if (metric == null) throw new ArgumentNullException(nameof(metric));
        await WriteMetricsAsync([metric], cancellationToken);
        return -1;
    }

    public async Task<IEnumerable<long>> StoreMetricsBatchAsync(
        IEnumerable<MetricModel> metrics,
        CancellationToken cancellationToken = default)
    {
        var list = (metrics ?? throw new ArgumentNullException(nameof(metrics))).ToList();
        if (list.Count == 0) return [];
        await WriteMetricsAsync(list, cancellationToken);
        _logger.LogDebug("Enqueued {Count} metrics for async write", list.Count);
        return Enumerable.Empty<long>();
    }

    public Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => _store.DeleteOldMetricDataPointsAsync(retentionPeriod, cancellationToken);

    /// <summary>
    /// Reserves <c>metrics.Count</c> on <see cref="TelemetryIngestionChannel.MetricGate"/> before
    /// writing, releasing on a failed write so a cancelled or otherwise-failed enqueue cannot leak
    /// the reservation forever.
    /// </summary>
    private async Task WriteMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken)
    {
        await _channel.MetricGate.AcquireAsync(metrics.Count, cancellationToken);
        try
        {
            await _channel.Metrics.Writer.WriteAsync(metrics, cancellationToken);
        }
        catch
        {
            _channel.MetricGate.Release(metrics.Count);
            throw;
        }
    }
}
