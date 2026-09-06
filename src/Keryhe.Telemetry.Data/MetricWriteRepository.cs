using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

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
        await _channel.Metrics.Writer.WriteAsync([metric], cancellationToken);
        return -1;
    }

    public async Task<IEnumerable<long>> StoreMetricsBatchAsync(
        IEnumerable<MetricModel> metrics,
        CancellationToken cancellationToken = default)
    {
        var list = (metrics ?? throw new ArgumentNullException(nameof(metrics))).ToList();
        if (list.Count == 0) return [];
        await _channel.Metrics.Writer.WriteAsync(list, cancellationToken);
        _logger.LogDebug("Enqueued {Count} metrics for async write", list.Count);
        return Enumerable.Empty<long>();
    }

    public Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => _store.DeleteOldMetricDataPointsAsync(retentionPeriod, cancellationToken);
}
