using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

// =============================================================================
// LOG READ REPOSITORY IMPLEMENTATION
// =============================================================================

public class LogReadRepository : ILogReadRepository
{
    private readonly TelemetryReadDbContext _context;
    private readonly ILogger<LogReadRepository> _logger;

    public LogReadRepository(TelemetryReadDbContext context, ILogger<LogReadRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a log record by ID with all related data
    /// </summary>
    public async Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var logRecord = await _context.LogRecords
                .Include(lr => lr.Resource)
                .Include(lr => lr.Scope)
                .FirstOrDefaultAsync(lr => lr.Id == id, cancellationToken);

            return ConvertToLogRecordModel(logRecord);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log record with ID {LogRecordId}", id);
            throw;
        }
    }

    /// <summary>
    /// Gets all log records for a specific trace ID
    /// </summary>
    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        try
        {
            var traceId = traceIdHex;

            var logRecords = await _context.LogRecords
                .Include(lr => lr.Resource)
                .Include(lr => lr.Scope)
                .Where(lr => lr.TraceId == traceId)
                .OrderBy(lr => lr.TimeUnixNano)
                .ToListAsync(cancellationToken);

            return ConvertToLogRecordModels(logRecords);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log records for trace ID {TraceId}", traceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Gets log records within a time range
    /// </summary>
    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        try
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

            var logRecordModels = await _context.LogRecords
                .Include(lr => lr.Resource)
                .Include(lr => lr.Scope)
                .Where(lr => lr.TimeUnixNano >= startTimeNano && lr.TimeUnixNano <= endTimeNano)
                .OrderByDescending(lr => lr.TimeUnixNano)
                .ToListAsync(cancellationToken);

            return ConvertToLogRecordModels(logRecordModels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log records for time range {StartTime} to {EndTime}", startTime, endTime);
            throw;
        }
    }

    /// <summary>
    /// Gets log records by minimum severity level
    /// </summary>
    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsBySeverityAsync(int minSeverity, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.LogRecords
                .Include(lr => lr.Resource)
                .Include(lr => lr.Scope)
                .Where(lr => lr.SeverityNumber >= minSeverity);

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(lr => lr.TimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(lr => lr.TimeUnixNano <= endTimeNano);
            }

            var logRecordModels = await query
                .OrderBy(lr => lr.TimeUnixNano)
                .ToListAsync(cancellationToken);

            return ConvertToLogRecordModels(logRecordModels);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving log records by severity {MinSeverity}", minSeverity);
            throw;
        }
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private LogRecordModel? ConvertToLogRecordModel(LogRecord? logRecord)
    {
        if (logRecord == null)
            return null;

        var model = new LogRecordModel();
        model.Resource = new ResourceModel();
        model.Resource.SchemaUrl = logRecord.Resource.SchemaUrl;
        model.Resource.Attributes = logRecord.Resource.Attributes ?? new Dictionary<string, object>();
        model.InstrumentationScope = new InstrumentationScopeModel();
        model.InstrumentationScope.SchemaUrl = logRecord.Scope.SchemaUrl;
        model.InstrumentationScope.Attributes = logRecord.Scope.Attributes ?? new Dictionary<string, object>();
        model.InstrumentationScope.Name = logRecord.Scope.Name;
        model.InstrumentationScope.Version = logRecord.Scope.Version;
        model.SeverityText = logRecord.SeverityText;
        model.SeverityNumber = logRecord.SeverityNumber;
        model.Attributes = logRecord.Attributes ?? new Dictionary<string, object>();
        model.TraceIdHex = logRecord.TraceId;
        model.BodyType = logRecord.BodyType;
        model.BodyValue = logRecord.BodyValue;
        model.DroppedAttributesCount = logRecord.DroppedAttributesCount;
        model.Flags = logRecord.Flags;
        model.ObservedTimeUnixNano = logRecord.ObservedTimeUnixNano;
        model.SpanIdHex = logRecord.SpanId;
        model.TimeUnixNano = logRecord.TimeUnixNano;

        return model;
    }

    private IEnumerable<LogRecordModel> ConvertToLogRecordModels(IEnumerable<LogRecord> logRecords)
    {
        return logRecords.Select(ConvertToLogRecordModel).OfType<LogRecordModel>().ToList();
    }
}
