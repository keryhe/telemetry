using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

public class MetricWriteRepository : IMetricWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly TelemetryIngestionChannel _channel;
    private readonly ILogger<MetricWriteRepository> _logger;

    public MetricWriteRepository(
        TelemetryWriteDbContext context,
        TelemetryIngestionChannel channel,
        ILogger<MetricWriteRepository> logger)
    {
        _context = context;
        _channel = channel;
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

    public async Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default)
    {
        var metric = await _context.Metrics.FindAsync([id], cancellationToken);
        if (metric == null) return false;
        _context.Metrics.Remove(metric);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        var metrics = await _context.Metrics
            .Where(m => m.Name == name)
            .ToListAsync(cancellationToken);

        _context.Metrics.RemoveRange(metrics);
        await _context.SaveChangesAsync(cancellationToken);
        return metrics.Count;
    }

    public async Task<int> DeleteMetricsByTimeRangeAsync(
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var metrics = await _context.Metrics
            .Where(m => m.CreatedAt >= startTime && m.CreatedAt <= endTime)
            .ToListAsync(cancellationToken);

        _context.Metrics.RemoveRange(metrics);
        await _context.SaveChangesAsync(cancellationToken);
        return metrics.Count;
    }

    public async Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var metrics = await _context.Metrics
            .Where(m => m.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        _context.Metrics.RemoveRange(metrics);
        await _context.SaveChangesAsync(cancellationToken);
        return metrics.Count;
    }
}
