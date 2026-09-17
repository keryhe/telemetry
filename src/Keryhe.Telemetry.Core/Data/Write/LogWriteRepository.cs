using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Write;

public class LogWriteRepository : ILogWriteRepository
{
    private readonly TelemetryIngestionChannel _channel;
    private readonly ITelemetryWriteStore _store;
    private readonly ILogger<LogWriteRepository> _logger;

    public LogWriteRepository(
        TelemetryIngestionChannel channel,
        ITelemetryWriteStore store,
        ILogger<LogWriteRepository> logger)
    {
        _channel = channel;
        _store = store;
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
        CancellationToken cancellationToken = default)
    {
        var list = (logRecords ?? throw new ArgumentNullException(nameof(logRecords))).ToList();
        if (list.Count == 0) return [];
        await WriteLogRecordsAsync(list, cancellationToken);
        _logger.LogDebug("Enqueued {Count} log records for async write", list.Count);
        return Enumerable.Empty<long>();
    }

    public Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => _store.DeleteOldLogRecordsAsync(retentionPeriod, cancellationToken);

    /// <summary>
    /// Reserves <c>records.Count</c> on <see cref="TelemetryIngestionChannel.LogGate"/> before
    /// writing, releasing on a failed write so a cancelled or otherwise-failed enqueue cannot leak
    /// the reservation forever.
    /// </summary>
    private async Task WriteLogRecordsAsync(List<LogRecordModel> records, CancellationToken cancellationToken)
    {
        await _channel.LogGate.AcquireAsync(records.Count, cancellationToken);
        try
        {
            await _channel.Logs.Writer.WriteAsync(records, cancellationToken);
        }
        catch
        {
            _channel.LogGate.Release(records.Count);
            throw;
        }
    }
}
