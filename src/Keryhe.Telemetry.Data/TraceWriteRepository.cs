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
// TRACE WRITE REPOSITORY IMPLEMENTATION
// =============================================================================

public class TraceWriteRepository : ITraceWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly ILogger<TraceWriteRepository> _logger;

    public TraceWriteRepository(TelemetryWriteDbContext context, ILogger<TraceWriteRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Stores a complete trace with all its spans
    /// </summary>
    public async Task<string> StoreTraceAsync(TraceModel trace, CancellationToken cancellationToken = default)
    {
        if (trace == null)
            throw new ArgumentNullException(nameof(trace));

        if (!trace.Spans.Any())
            throw new ArgumentException("Trace must contain at least one span");

        var traceId = trace.Spans.First().TraceIdHex;

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var defaultResource = await GetOrCreateResourceAsync(trace.Resource, cancellationToken);
            var defaultScope = await GetOrCreateInstrumentationScopeAsync(trace.InstrumentationScope, cancellationToken);

            foreach (var spanModel in trace.Spans)
            {
                var resource = spanModel.Resource != null
                    ? await GetOrCreateResourceAsync(spanModel.Resource, cancellationToken)
                    : defaultResource;

                var scope = spanModel.InstrumentationScope != null
                    ? await GetOrCreateInstrumentationScopeAsync(spanModel.InstrumentationScope, cancellationToken)
                    : defaultScope;

                await StoreSpanInternalAsync(spanModel, resource, scope, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored trace {TraceId} with {SpanCount} spans",
                traceId, trace.Spans.Count);

            return traceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing trace {TraceId}", traceId);
            throw;
        }
    }

    /// <summary>
    /// Stores a single span
    /// </summary>
    public async Task<long> StoreSpanAsync(SpanModel span, CancellationToken cancellationToken = default)
    {
        if (span == null)
            throw new ArgumentNullException(nameof(span));

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resource = await GetOrCreateResourceAsync(span.Resource, cancellationToken);
            var scope = await GetOrCreateInstrumentationScopeAsync(span.InstrumentationScope, cancellationToken);

            var spanId = await StoreSpanInternalAsync(span, resource, scope, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored span {SpanId} for trace {TraceId}",
                span.SpanIdHex, span.TraceIdHex);

            return spanId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing span {SpanId} for trace {TraceId}",
                span.SpanIdHex, span.TraceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Stores multiple traces in a batch
    /// </summary>
    public async Task<IEnumerable<string>> StoreTracesBatchAsync(IEnumerable<TraceModel> traces, CancellationToken cancellationToken = default)
    {
        if (traces == null)
            throw new ArgumentNullException(nameof(traces));

        var tracesList = traces.ToList();
        if (!tracesList.Any())
            return Enumerable.Empty<string>();

        var traceIds = new List<string>();

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            foreach (var trace in tracesList)
            {
                if (!trace.Spans.Any())
                    continue;

                var traceId = trace.Spans.First().TraceIdHex;
                traceIds.Add(traceId);

                var defaultResourceKey = GenerateResourceKey(trace.Resource);
                if (!resourceCache.TryGetValue(defaultResourceKey, out var defaultResource))
                {
                    defaultResource = await GetOrCreateResourceAsync(trace.Resource, cancellationToken);
                    resourceCache[defaultResourceKey] = defaultResource;
                }

                var defaultScopeKey = GenerateScopeKey(trace.InstrumentationScope);
                if (!scopeCache.TryGetValue(defaultScopeKey, out var defaultScope))
                {
                    defaultScope = await GetOrCreateInstrumentationScopeAsync(trace.InstrumentationScope, cancellationToken);
                    scopeCache[defaultScopeKey] = defaultScope;
                }

                foreach (var spanModel in trace.Spans)
                {
                    Resource resource;
                    InstrumentationScope scope;

                    if (spanModel.Resource != null)
                    {
                        var spanResourceKey = GenerateResourceKey(spanModel.Resource);
                        if (!resourceCache.TryGetValue(spanResourceKey, out resource))
                        {
                            resource = await GetOrCreateResourceAsync(spanModel.Resource, cancellationToken);
                            resourceCache[spanResourceKey] = resource;
                        }
                    }
                    else
                    {
                        resource = defaultResource;
                    }

                    if (spanModel.InstrumentationScope != null)
                    {
                        var spanScopeKey = GenerateScopeKey(spanModel.InstrumentationScope);
                        if (!scopeCache.TryGetValue(spanScopeKey, out scope))
                        {
                            scope = await GetOrCreateInstrumentationScopeAsync(spanModel.InstrumentationScope, cancellationToken);
                            scopeCache[spanScopeKey] = scope;
                        }
                    }
                    else
                    {
                        scope = defaultScope;
                    }

                    await StoreSpanInternalAsync(spanModel, resource, scope, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored batch of {Count} traces", tracesList.Count);
            return traceIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing traces batch");
            throw;
        }
    }

    /// <summary>
    /// Stores multiple spans in a batch
    /// </summary>
    public async Task<IEnumerable<long>> StoreSpansBatchAsync(IEnumerable<SpanModel> spans, CancellationToken cancellationToken = default)
    {
        if (spans == null)
            throw new ArgumentNullException(nameof(spans));

        var spansList = spans.ToList();
        if (!spansList.Any())
            return Enumerable.Empty<long>();

        var spanIds = new List<long>();

        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            foreach (var spanModel in spansList)
            {
                var resourceKey = GenerateResourceKey(spanModel.Resource);
                if (!resourceCache.TryGetValue(resourceKey, out var resource))
                {
                    resource = await GetOrCreateResourceAsync(spanModel.Resource, cancellationToken);
                    resourceCache[resourceKey] = resource;
                }

                var scopeKey = GenerateScopeKey(spanModel.InstrumentationScope);
                if (!scopeCache.TryGetValue(scopeKey, out var scope))
                {
                    scope = await GetOrCreateInstrumentationScopeAsync(spanModel.InstrumentationScope, cancellationToken);
                    scopeCache[scopeKey] = scope;
                }

                var spanId = await StoreSpanInternalAsync(spanModel, resource, scope, cancellationToken);
                spanIds.Add(spanId);
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored batch of {Count} spans", spansList.Count);
            return spanIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing spans batch");
            throw;
        }
    }

    /// <summary>
    /// Deletes a complete trace
    /// </summary>
    public async Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        try
        {
            var traceId = traceIdHex;

            var spans = await _context.Spans
                .Where(s => s.TraceId == traceId)
                .ToListAsync(cancellationToken);

            if (!spans.Any())
                return false;

            _context.Spans.RemoveRange(spans);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted trace {TraceId} with {SpanCount} spans",
                traceIdHex, spans.Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting trace {TraceId}", traceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Deletes traces within a time range
    /// </summary>
    public async Task<int> DeleteTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        try
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

            var spans = await _context.Spans
                .Where(s => s.StartTimeUnixNano >= startTimeNano && s.StartTimeUnixNano <= endTimeNano)
                .ToListAsync(cancellationToken);

            var traceCount = spans.GroupBy(s => s.TraceId).Count();
            _context.Spans.RemoveRange(spans);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted {TraceCount} traces ({SpanCount} spans) for time range {StartTime} to {EndTime}",
                traceCount, spans.Count, startTime, endTime);

            return traceCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting traces for time range {StartTime} to {EndTime}", startTime, endTime);
            throw;
        }
    }

    /// <summary>
    /// Deletes a specific span
    /// </summary>
    public async Task<bool> DeleteSpanAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex))
            throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));

        try
        {
            var traceId = traceIdHex;
            var spanId = spanIdHex;

            var span = await _context.Spans
                .FirstOrDefaultAsync(s => s.TraceId == traceId && s.SpanId == spanId, cancellationToken);

            if (span == null)
                return false;

            _context.Spans.Remove(span);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted span {SpanId} from trace {TraceId}", spanIdHex, traceIdHex);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting span {SpanId} from trace {TraceId}", spanIdHex, traceIdHex);
            throw;
        }
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private async Task<long> StoreSpanInternalAsync(SpanModel spanModel, Resource resource, InstrumentationScope scope, CancellationToken cancellationToken)
    {
        var spanEntity = new Span
        {
            TraceId = spanModel.TraceIdHex,
            SpanId = spanModel.SpanIdHex,
            ParentSpanId = spanModel.ParentSpanIdHex,
            ResourceId = resource.Id,
            ScopeId = scope.Id,
            Name = spanModel.Name,
            Kind = spanModel.Kind,
            StartTimeUnixNano = spanModel.StartTimeUnixNano,
            EndTimeUnixNano = spanModel.EndTimeUnixNano,
            DroppedAttributesCount = spanModel.DroppedAttributesCount,
            DroppedEventsCount = spanModel.DroppedEventsCount,
            DroppedLinksCount = spanModel.DroppedLinksCount,
            TraceState = spanModel.TraceState,
            StatusCode = spanModel.StatusCode,
            StatusMessage = spanModel.StatusMessage,
            Attributes = spanModel.Attributes,
        };

        _context.Spans.Add(spanEntity);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex, "uk_trace_span"))
        {
            _context.Entry(spanEntity).State = EntityState.Detached;

            var existingSpan = await _context.Spans
                .FirstAsync(s => s.TraceId == spanModel.TraceIdHex && s.SpanId == spanModel.SpanIdHex, cancellationToken);

            return existingSpan.Id;
        }

        if (spanModel.Events?.Count > 0)
        {
            var events = spanModel.Events.Select(eventModel => new SpanEvent
            {
                SpanId = spanEntity.Id,
                Name = eventModel.Name,
                TimeUnixNano = eventModel.TimeUnixNano,
                DroppedAttributesCount = eventModel.DroppedAttributesCount,
                Attributes = eventModel.Attributes,
            }).ToList();

            _context.SpanEvents.AddRange(events);
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (spanModel.Links?.Count > 0)
        {
            var links = spanModel.Links.Select(linkModel => new SpanLink
            {
                SpanId = spanEntity.Id,
                LinkedTraceId = linkModel.LinkedTraceIdHex,
                LinkedSpanId = linkModel.LinkedSpanIdHex,
                TraceState = linkModel.TraceState ?? "unknown",
                DroppedAttributesCount = linkModel.DroppedAttributesCount,
                Attributes = linkModel.Attributes,
            }).ToList();

            _context.SpanLinks.AddRange(links);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        return spanEntity.Id;
    }

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

    private static bool IsUniqueConstraintViolation(DbUpdateException ex, string constraintName)
    {
        var inner = ex.InnerException;
        if (inner == null)
            return false;

        var sqlState = inner.GetType().GetProperty("SqlState")?.GetValue(inner) as string;
        if (string.Equals(sqlState, "23505", StringComparison.Ordinal))
        {
            var dbConstraintName = inner.GetType().GetProperty("ConstraintName")?.GetValue(inner) as string;
            return dbConstraintName == null || string.Equals(dbConstraintName, constraintName, StringComparison.Ordinal);
        }

        return inner.Message.Contains(constraintName, StringComparison.OrdinalIgnoreCase);
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
