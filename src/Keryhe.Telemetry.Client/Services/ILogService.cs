using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public interface ILogService
{
    Task<List<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken = default);
    Task<List<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default);
}

public class LogService : ILogService
{
    private readonly ILogger<LogService> _logger;
    private readonly ILogRepository _logRepository;
    
    public LogService(ILogRepository logRepository, ILogger<LogService> logger)
    {
        _logger = logger;
        _logRepository = logRepository;
    }


    public async Task<List<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken = default)
    {
        if (startTime == null || endTime == null)
        {
            throw new ArgumentNullException(nameof(startTime));
        }
        var results = await _logRepository.GetLogRecordsByTimeRangeAsync(startTime.Value, endTime.Value, cancellationToken);
        return results.ToList();
    }

    public async Task<List<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceIdHex))
        {
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        }
        
        _logger.LogInformation("Retrieving logs for trace ID: {TraceId}", traceIdHex);
        var results = await _logRepository.GetLogRecordsByTraceIdAsync(traceIdHex, cancellationToken);
        return results.ToList();
    }
}