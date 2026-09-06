using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

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
        await _channel.Logs.Writer.WriteAsync([logRecord], cancellationToken);
        return -1;
    }

    public async Task<IEnumerable<long>> StoreLogRecordsBatchAsync(
        IEnumerable<LogRecordModel> logRecords,
        CancellationToken cancellationToken = default)
    {
        var list = (logRecords ?? throw new ArgumentNullException(nameof(logRecords))).ToList();
        if (list.Count == 0) return [];
        await _channel.Logs.Writer.WriteAsync(list, cancellationToken);
        _logger.LogDebug("Enqueued {Count} log records for async write", list.Count);
        return Enumerable.Empty<long>();
    }

    public Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => _store.DeleteOldLogRecordsAsync(retentionPeriod, cancellationToken);
}
