using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

public class LogWriteRepository : ILogWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly TelemetryIngestionChannel _channel;
    private readonly ILogger<LogWriteRepository> _logger;

    public LogWriteRepository(
        TelemetryWriteDbContext context,
        TelemetryIngestionChannel channel,
        ILogger<LogWriteRepository> logger)
    {
        _context = context;
        _channel = channel;
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

    public async Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default)
    {
        var record = await _context.LogRecords.FindAsync([id], cancellationToken);
        if (record == null) return false;
        _context.LogRecords.Remove(record);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteLogRecordsByTimeRangeAsync(
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
        var endNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

        var records = await _context.LogRecords
            .Where(lr => lr.TimeUnixNano >= startNano && lr.TimeUnixNano <= endNano)
            .ToListAsync(cancellationToken);

        _context.LogRecords.RemoveRange(records);
        await _context.SaveChangesAsync(cancellationToken);
        return records.Count;
    }
}
