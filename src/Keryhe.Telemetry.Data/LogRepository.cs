using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

// =============================================================================
// LOG REPOSITORY IMPLEMENTATION
// =============================================================================

public class LogRepository : ILogRepository
{
    private readonly OpenTelemetryDbContext _context;
    private readonly ILogger<LogRepository> _logger;

    public LogRepository(OpenTelemetryDbContext context, ILogger<LogRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Stores a single log record
    /// </summary>
    public async Task<long> StoreLogRecordAsync(LogRecordModel logRecord, CancellationToken cancellationToken = default)
    {
        if (logRecord == null)
            throw new ArgumentNullException(nameof(logRecord));

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // Get or create resource
            var resource = await GetOrCreateResourceAsync(logRecord.Resource, cancellationToken);

            // Get or create instrumentation scope
            var scope = await GetOrCreateInstrumentationScopeAsync(logRecord.InstrumentationScope, cancellationToken);

            // Create log record entity
            var logRecordEntity = new LogRecord
            {
                ResourceId = resource.Id,
                ScopeId = scope.Id,
                TimeUnixNano = logRecord.TimeUnixNano,
                ObservedTimeUnixNano = logRecord.ObservedTimeUnixNano,
                SeverityNumber = logRecord.SeverityNumber,
                SeverityText = logRecord.SeverityText,
                BodyType = logRecord.BodyType,
                BodyValue = logRecord.BodyValue,
                DroppedAttributesCount = logRecord.DroppedAttributesCount,
                Flags = logRecord.Flags,
                TraceId = logRecord.TraceIdHex,
                SpanId = logRecord.SpanIdHex,
                Attributes = logRecord.Attributes
            };

            _context.LogRecords.Add(logRecordEntity);
            await _context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored log record with ID {LogRecordId}", logRecordEntity.Id);
            return logRecordEntity.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing log record");
            throw;
        }
        finally
        {
            Threading.LockSignal.Release();
        }
    }

    /// <summary>
    /// Stores multiple log records in a batch for better performance
    /// </summary>
    public async Task<IEnumerable<long>> StoreLogRecordsBatchAsync(IEnumerable<LogRecordModel> logRecords, CancellationToken cancellationToken = default)
    {
        if (logRecords == null)
            throw new ArgumentNullException(nameof(logRecords));

        var logRecordsList = logRecords.ToList();
        if (!logRecordsList.Any())
            return Enumerable.Empty<long>();

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // Group by resource and scope for efficiency
            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            var logRecordEntities = new List<LogRecord>();

            foreach (var logRecord in logRecordsList)
            {
                // Get or create resource
                var resourceKey = GenerateResourceKey(logRecord.Resource);
                if (!resourceCache.TryGetValue(resourceKey, out var resource))
                {
                    resource = await GetOrCreateResourceAsync(logRecord.Resource, cancellationToken);
                    resourceCache[resourceKey] = resource;
                }

                // Get or create instrumentation scope
                var scopeKey = GenerateScopeKey(logRecord.InstrumentationScope);
                if (!scopeCache.TryGetValue(scopeKey, out var scope))
                {
                    scope = await GetOrCreateInstrumentationScopeAsync(logRecord.InstrumentationScope,
                        cancellationToken);
                    scopeCache[scopeKey] = scope;
                }

                // Create log record entity
                var logRecordEntity = new LogRecord
                {
                    ResourceId = resource.Id,
                    ScopeId = scope.Id,
                    TimeUnixNano = logRecord.TimeUnixNano,
                    ObservedTimeUnixNano = logRecord.ObservedTimeUnixNano,
                    SeverityNumber = logRecord.SeverityNumber,
                    SeverityText = logRecord.SeverityText,
                    BodyType = logRecord.BodyType,
                    BodyValue = logRecord.BodyValue,
                    DroppedAttributesCount = logRecord.DroppedAttributesCount,
                    Flags = logRecord.Flags,
                    TraceId = logRecord.TraceIdHex,
                    SpanId = logRecord.SpanIdHex,
                    Attributes = logRecord.Attributes
                };

                logRecordEntities.Add(logRecordEntity);
            }

            // Bulk insert log records
            _context.LogRecords.AddRange(logRecordEntities);
            await _context.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var resultIds = logRecordEntities.Select(x => x.Id).ToList();
            
            _logger.LogDebug("Successfully stored batch of {Count} log records", logRecordsList.Count);
            return resultIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing log records batch");
            throw;
        }
        finally
        {
            Threading.LockSignal.Release();
        }
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
                    //.ThenInclude(s => s.Attributes)
                //.Include(lr => lr.Attributes)
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
                    //.ThenInclude(s => s.Attributes)
                //.Include(lr => lr.Attributes)
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
                    //.ThenInclude(s => s.Attributes)
                //.Include(lr => lr.Attributes)
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
                    //.ThenInclude(s => s.Attributes)
                //.Include(lr => lr.Attributes)
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

    /// <summary>
    /// Deletes a log record by ID
    /// </summary>
    public async Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var logRecord = await _context.LogRecords.FindAsync(new object[] { id }, cancellationToken);
            if (logRecord == null)
                return false;

            _context.LogRecords.Remove(logRecord);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted log record with ID {LogRecordId}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting log record with ID {LogRecordId}", id);
            throw;
        }
    }

    /// <summary>
    /// Deletes log records within a time range
    /// </summary>
    public async Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        try
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

            var recordsToDelete = await _context.LogRecords
                .Where(lr => lr.TimeUnixNano >= startTimeNano && lr.TimeUnixNano <= endTimeNano)
                .ToListAsync(cancellationToken);

            var deleteCount = recordsToDelete.Count;
            _context.LogRecords.RemoveRange(recordsToDelete);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted {Count} log records for time range {StartTime} to {EndTime}", 
                deleteCount, startTime, endTime);
            
            return deleteCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting log records for time range {StartTime} to {EndTime}", startTime, endTime);
            throw;
        }
    }

    private LogRecordModel? ConvertToLogRecordModel(LogRecord? logRecord)
    {
        if (logRecord == null)
        {
            return null;
        }
        
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

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private async Task<Resource> GetOrCreateResourceAsync(ResourceModel? resourceModel, CancellationToken cancellationToken)
    {
        if (resourceModel == null)
        {
            resourceModel = new ResourceModel
            {
                Attributes = new Dictionary<string, object> { { "service.name", "unknown" } }
            };
        }

        var resourceHash = GenerateResourceHash(resourceModel);

        var existingResource = await _context.Resources
            .FirstOrDefaultAsync(r => r.ResourceHash == resourceHash, cancellationToken);

        if (existingResource != null)
            return existingResource;

        var resource = new Resource
        {
            ResourceHash = resourceHash,
            SchemaUrl = resourceModel.SchemaUrl,
            Attributes = resourceModel.Attributes
        };

        _context.Resources.Add(resource);
        await _context.SaveChangesAsync(cancellationToken);

        return resource;
    }

    private async Task<InstrumentationScope> GetOrCreateInstrumentationScopeAsync(InstrumentationScopeModel? scopeModel, CancellationToken cancellationToken)
    {
        if (scopeModel == null)
        {
            scopeModel = new InstrumentationScopeModel { Name = "unknown" };
        }

        var scopeHash = GenerateScopeHash(scopeModel);

        var existingScope = await _context.InstrumentationScopes
            .FirstOrDefaultAsync(s => s.ScopeHash == scopeHash, cancellationToken);

        if (existingScope != null)
            return existingScope;

        var scope = new InstrumentationScope
        {
            Name = scopeModel.Name,
            Version = scopeModel.Version,
            SchemaUrl = scopeModel.SchemaUrl,
            ScopeHash = scopeHash,
            Attributes = scopeModel.Attributes
        };

        _context.InstrumentationScopes.Add(scope);
        await _context.SaveChangesAsync(cancellationToken);

        return scope;
    }

    private static string GenerateResourceHash(ResourceModel resourceModel)
    {
        var content = $"{resourceModel.SchemaUrl ?? ""}__{JsonSerializer.Serialize(resourceModel.Attributes ?? new Dictionary<string, object>())}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateScopeHash(InstrumentationScopeModel scopeModel)
    {
        var content = $"{scopeModel.Name}__{scopeModel.Version ?? ""}__{scopeModel.SchemaUrl ?? ""}__{JsonSerializer.Serialize(scopeModel.Attributes ?? new Dictionary<string, object>())}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateResourceKey(ResourceModel? resourceModel)
    {
        return GenerateResourceHash(resourceModel ?? new ResourceModel());
    }

    private static string GenerateScopeKey(InstrumentationScopeModel? scopeModel)
    {
        return GenerateScopeHash(scopeModel ?? new InstrumentationScopeModel { Name = "unknown" });
    }

    private static string ConvertAttributeValue(object value)
    {
        return value switch
        {
            string str => str,
            bool b => b.ToString().ToLower(),
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString("G17"), // Full precision
            float f => f.ToString("G9"),   // Full precision
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => JsonSerializer.Serialize(value)
        };
    }

    private static AttributeType DetermineAttributeType(object value)
    {
        return value switch
        {
            string => AttributeType.STRING,
            bool => AttributeType.BOOL,
            int or long => AttributeType.INT,
            double or float => AttributeType.DOUBLE,
            byte[] => AttributeType.BYTES,
            Array or IEnumerable<object> => AttributeType.ARRAY,
            _ => AttributeType.KVLIST
        };
    }
}
