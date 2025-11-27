using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public interface ILogService
{
    Task<List<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken = default);
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
}