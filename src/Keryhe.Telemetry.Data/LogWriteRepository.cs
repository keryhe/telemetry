using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

// =============================================================================
// LOG WRITE REPOSITORY IMPLEMENTATION
// =============================================================================

public class LogWriteRepository : ILogWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly ILogger<LogWriteRepository> _logger;

    public LogWriteRepository(TelemetryWriteDbContext context, ILogger<LogWriteRepository> logger)
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

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resource = await GetOrCreateResourceAsync(logRecord.Resource, cancellationToken);
            var scope = await GetOrCreateInstrumentationScopeAsync(logRecord.InstrumentationScope, cancellationToken);

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

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            var logRecordEntities = new List<LogRecord>();

            foreach (var logRecord in logRecordsList)
            {
                var resourceKey = GenerateResourceKey(logRecord.Resource);
                if (!resourceCache.TryGetValue(resourceKey, out var resource))
                {
                    resource = await GetOrCreateResourceAsync(logRecord.Resource, cancellationToken);
                    resourceCache[resourceKey] = resource;
                }

                var scopeKey = GenerateScopeKey(logRecord.InstrumentationScope);
                if (!scopeCache.TryGetValue(scopeKey, out var scope))
                {
                    scope = await GetOrCreateInstrumentationScopeAsync(logRecord.InstrumentationScope, cancellationToken);
                    scopeCache[scopeKey] = scope;
                }

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

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private async Task<Resource> GetOrCreateResourceAsync(ResourceModel? resourceModel, CancellationToken cancellationToken)
    {
        resourceModel = NormalizeResourceModel(resourceModel);

        var resourceHash = GenerateResourceHash(resourceModel);

                var resourceIdRows = await _context.Database.SqlQueryRaw<long>(
            @"INSERT INTO resources (""AttributesJson"", ""CreatedAt"", ""ResourceHash"", ""SchemaUrl"")
              VALUES (CAST({0} AS jsonb), {1}, {2}, {3})
              ON CONFLICT (""ResourceHash"")
              DO UPDATE SET ""ResourceHash"" = EXCLUDED.""ResourceHash""
              RETURNING ""Id"";",
            SerializeAttributesDeterministically(resourceModel.Attributes),
            DateTime.UtcNow,
            resourceHash,
            (object?)resourceModel.SchemaUrl ?? DBNull.Value)
                        .ToListAsync(cancellationToken);

                var resourceId = resourceIdRows.Single();

        return new Resource { Id = resourceId };
    }

    private async Task<InstrumentationScope> GetOrCreateInstrumentationScopeAsync(InstrumentationScopeModel? scopeModel, CancellationToken cancellationToken)
    {
        if (scopeModel == null)
        {
            scopeModel = new InstrumentationScopeModel { Name = "unknown" };
        }

        var scopeHash = GenerateScopeHash(scopeModel);

                var scopeIdRows = await _context.Database.SqlQueryRaw<long>(
            @"INSERT INTO instrumentation_scopes (""Name"", ""Version"", ""SchemaUrl"", ""ScopeHash"", ""CreatedAt"", ""AttributesJson"")
              VALUES ({0}, {1}, {2}, {3}, {4}, CAST({5} AS jsonb))
              ON CONFLICT (""ScopeHash"")
              DO UPDATE SET ""ScopeHash"" = EXCLUDED.""ScopeHash""
              RETURNING ""Id"";",
            scopeModel.Name,
            (object?)scopeModel.Version ?? DBNull.Value,
            (object?)scopeModel.SchemaUrl ?? DBNull.Value,
            scopeHash,
            DateTime.UtcNow,
            SerializeAttributesDeterministically(scopeModel.Attributes))
                        .ToListAsync(cancellationToken);

                var scopeId = scopeIdRows.Single();

        return new InstrumentationScope { Id = scopeId };
    }

    private static string GenerateResourceHash(ResourceModel resourceModel)
    {
        var content = $"{resourceModel.SchemaUrl ?? ""}__{SerializeAttributesDeterministically(resourceModel.Attributes)}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateScopeHash(InstrumentationScopeModel scopeModel)
    {
        var content = $"{scopeModel.Name}__{scopeModel.Version ?? ""}__{scopeModel.SchemaUrl ?? ""}__{SerializeAttributesDeterministically(scopeModel.Attributes)}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateResourceKey(ResourceModel? resourceModel)
        => GenerateResourceHash(NormalizeResourceModel(resourceModel));

    private static string GenerateScopeKey(InstrumentationScopeModel? scopeModel)
        => GenerateScopeHash(scopeModel ?? new InstrumentationScopeModel { Name = "unknown" });

    private static ResourceModel NormalizeResourceModel(ResourceModel? resourceModel)
    {
        return resourceModel ?? new ResourceModel
        {
            Attributes = new Dictionary<string, object> { { "service.name", "unknown" } }
        };
    }

    private static string SerializeAttributesDeterministically(Dictionary<string, object>? attributes)
    {
        var ordered = (attributes ?? new Dictionary<string, object>())
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return JsonSerializer.Serialize(ordered);
    }
}
