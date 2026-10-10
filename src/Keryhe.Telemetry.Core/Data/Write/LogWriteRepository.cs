using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Write;

public class LogWriteRepository : ILogWriteRepository
{
    private readonly TelemetryIngestionChannel _channel;
    private readonly ILogger<LogWriteRepository> _logger;

    public LogWriteRepository(
        TelemetryIngestionChannel channel,
        ILogger<LogWriteRepository> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public async Task<long> StoreLogRecordAsync(
        LogRecordModel logRecord,
        CancellationToken cancellationToken = default)
    {
        if (logRecord == null) throw new ArgumentNullException(nameof(logRecord));
        await WriteLogRecordsAsync([logRecord], cancellationToken);
        return -1;
    }

    public async Task<IEnumerable<long>> StoreLogRecordsBatchAsync(
        IEnumerable<LogRecordModel> logRecords,
        CancellationToken cancellationToken = default,
        long requestBytes = 0)
    {
        var list = (logRecords ?? throw new ArgumentNullException(nameof(logRecords))).ToList();
        if (list.Count == 0) return [];
        await WriteLogRecordsAsync(list, cancellationToken, requestBytes);
        _logger.LogDebug("Enqueued {Count} log records for async write", list.Count);
        return Enumerable.Empty<long>();
    }

    /// <summary>
    /// Reserves <c>records.Count</c> on <see cref="TelemetryIngestionChannel.LogGate"/> before
    /// writing, releasing on a failed write so a cancelled or otherwise-failed enqueue cannot leak
    /// the reservation forever.
    /// </summary>
    private async Task WriteLogRecordsAsync(List<LogRecordModel> records, CancellationToken cancellationToken, long requestBytes = 0)
    {
        var tenantId = TenantOf.Of(records[0]);
        await _channel.AcquireOrRejectAsync("logs", _channel.LogGate, records.Count, cancellationToken, requestBytes, tenantId);
        try
        {
            _channel.MarkEnqueued(records, requestBytes);
            await _channel.Logs.Writer.WriteAsync(records, cancellationToken);
        }
        catch
        {
            _channel.Release(_channel.LogGate, records.Count, requestBytes, tenantId);
            throw;
        }
    }
}
