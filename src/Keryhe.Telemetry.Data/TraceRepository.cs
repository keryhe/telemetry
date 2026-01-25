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
// TRACE REPOSITORY IMPLEMENTATION
// =============================================================================

public class TraceRepository : ITraceRepository
{
    private readonly OpenTelemetryDbContext _context;
    private readonly ILogger<TraceRepository> _logger;

    public TraceRepository(OpenTelemetryDbContext context, ILogger<TraceRepository> logger)
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

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // Get or create default resource and scope
            var defaultResource = await GetOrCreateResourceAsync(trace.Resource, cancellationToken);
            var defaultScope = await GetOrCreateInstrumentationScopeAsync(trace.InstrumentationScope, cancellationToken);

            foreach (var spanModel in trace.Spans)
            {
                // Use span-level resource/scope if provided, otherwise use trace-level defaults
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
        finally
        {
            Threading.LockSignal.Release();
        }
    }

    /// <summary>
    /// Stores a single span
    /// </summary>
    public async Task<long> StoreSpanAsync(SpanModel span, CancellationToken cancellationToken = default)
    {
        if (span == null)
            throw new ArgumentNullException(nameof(span));

        Threading.LockSignal.Wait();
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
        finally
        {
            Threading.LockSignal.Release();
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

        Threading.LockSignal.Wait();
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

                // Get or create default resource and scope for this trace
                var defaultResourceKey = GenerateResourceKey(trace.Resource);
                if (!resourceCache.TryGetValue(defaultResourceKey, out var defaultResource))
                {
                    defaultResource = await GetOrCreateResourceAsync(trace.Resource, cancellationToken);
                    resourceCache[defaultResourceKey] = defaultResource;
                }

                var defaultScopeKey = GenerateScopeKey(trace.InstrumentationScope);
                if (!scopeCache.TryGetValue(defaultScopeKey, out var defaultScope))
                {
                    defaultScope =
                        await GetOrCreateInstrumentationScopeAsync(trace.InstrumentationScope, cancellationToken);
                    scopeCache[defaultScopeKey] = defaultScope;
                }

                foreach (var spanModel in trace.Spans)
                {
                    // Determine resource and scope for this span
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
                            scope = await GetOrCreateInstrumentationScopeAsync(spanModel.InstrumentationScope,
                                cancellationToken);
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
        finally
        {
            Threading.LockSignal.Release();
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

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            foreach (var spanModel in spansList)
            {
                // Get or create resource
                var resourceKey = GenerateResourceKey(spanModel.Resource);
                if (!resourceCache.TryGetValue(resourceKey, out var resource))
                {
                    resource = await GetOrCreateResourceAsync(spanModel.Resource, cancellationToken);
                    resourceCache[resourceKey] = resource;
                }

                // Get or create scope
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
        finally
        {
            Threading.LockSignal.Release();
        }
    }

    /// <summary>
    /// Gets all spans for a trace
    /// </summary>
    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        try
        {
            var traceId = traceIdHex;

            var spans = await _context.Spans
                .Include(s => s.Resource)
                .Include(s => s.Scope)
                .Include(s => s.Events)
                .Include(s => s.Links)
                .Where(s => s.TraceId == traceId)
                .OrderBy(s => s.StartTimeUnixNano)
                .ToListAsync(cancellationToken);

            return ConvertToSpanModels(spans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving spans from trace {TraceId}", traceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Gets a specific span
    /// </summary>
    public async Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
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
                .Include(s => s.Resource)
                .Include(s => s.Scope)
                .Include(s => s.Events)
                .Include(s => s.Links)
                .FirstOrDefaultAsync(s => s.TraceId == traceId && s.SpanId == spanId, cancellationToken);
            
            return ConvertToSpanModel(span);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving span {SpanId} from trace {TraceId}", spanIdHex, traceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Gets child spans of a parent span
    /// </summary>
    public async Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(parentSpanIdHex))
            throw new ArgumentException("Parent Span ID cannot be null or empty", nameof(parentSpanIdHex));

        try
        {
            var traceId = traceIdHex;
            var parentSpanId = parentSpanIdHex;

            var spans = await _context.Spans
                .Include(s => s.Resource)
                .Include(s => s.Scope)
                .Include(s => s.Events)
                .Include(s => s.Links)
                .Where(s => s.TraceId == traceId && s.ParentSpanId == parentSpanId)
                .OrderBy(s => s.StartTimeUnixNano)
                .ToListAsync(cancellationToken);
            
            return ConvertToSpanModels(spans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving child spans for parent {ParentSpanId} in trace {TraceId}", 
                parentSpanIdHex, traceIdHex);
            throw;
        }
    }

    /// <summary>
    /// Gets traces within a time range
    /// </summary>
    public async Task<List<TraceInfo>> GetTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        try
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime);
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime);

            // First, get raw data from database including Resource.Attributes
            var rawData = await _context.Spans
                .Include(s => s.Resource)
                .Where(s => s.StartTimeUnixNano >= startTimeNano && s.StartTimeUnixNano <= endTimeNano)
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Group and extract service name from the earliest span (root span)
            var rawTraces = rawData
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    HasErrors = g.Any(s => s.StatusCode == SpanStatusCode.ERROR),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            // Then convert in memory
            var traces = rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = t.ServiceName,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano) 
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();

            return traces;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving traces for time range {StartTime} to {EndTime}", startTime, endTime);
            throw;
        }
    }

    /// <summary>
    /// Gets traces for a specific service
    /// </summary>
    public async Task<List<TraceInfo>> GetTracesByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            // Start with base query and include Resource
            var query = _context.Spans
                .Include(s => s.Resource)
                .AsQueryable();

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(s => s.StartTimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(s => s.StartTimeUnixNano <= endTimeNano);
            }

            // First, get raw data from database with Resource.Attributes
            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then filter by service name in memory
            var filteredSpans = rawData
                .Where(s => s.ResourceAttributes != null && 
                           s.ResourceAttributes.ContainsKey("service.name") && 
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .ToList();

            // Group by TraceId
            var rawTraces = filteredSpans
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    HasErrors = g.Any(s => s.StatusCode == SpanStatusCode.ERROR)
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            // Then convert in memory
            var traces = rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = serviceName,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano) 
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();

            return traces;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving traces for service {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Gets traces that contain errors
    /// </summary>
    public async Task<List<TraceInfo>> GetErrorTracesAsync(DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.Spans
                .Include(s => s.Resource)
                .Where(s => s.StatusCode == SpanStatusCode.ERROR);

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(s => s.StartTimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(s => s.StartTimeUnixNano <= endTimeNano);
            }

            // First, get raw data from database
            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Group and extract service name from the earliest span (root span)
            var rawTraces = rawData
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            // Then convert in memory
            var traces = rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = true,
                ServiceName = t.ServiceName,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano) 
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();

            return traces;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving error traces");
            throw;
        }
    }

    /// <summary>
    /// Gets traces that exceed a minimum duration
    /// </summary>
    public async Task<List<TraceInfo>> GetSlowTracesAsync(TimeSpan minDuration, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            var minDurationNano = (long)(minDuration.TotalMilliseconds * 1_000_000);
            
            var query = _context.Spans
                .Include(s => s.Resource)
                .AsQueryable();

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(s => s.StartTimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(s => s.StartTimeUnixNano <= endTimeNano);
            }

            // First, get raw data from database
            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Group and extract service name from the earliest span (root span)
            var rawTraces = rawData
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    HasErrors = g.Any(s => s.StatusCode == SpanStatusCode.ERROR),
                    DurationNano = g.Max(s => s.EndTimeUnixNano) - g.Min(s => s.StartTimeUnixNano),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .Where(t => t.DurationNano >= minDurationNano)
                .OrderByDescending(t => t.DurationNano)
                .Take(limit)
                .ToList();

            // Then convert in memory
            var traces = rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = t.ServiceName,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano) 
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();

            return traces;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving slow traces with minimum duration {MinDuration}", minDuration);
            throw;
        }
    }
    
    /// <summary>
/// Gets service dependencies based on span relationships
/// </summary>
public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
{
    try
    {
        // Build query for service dependencies using the spans table
        // This mimics the service_map_detailed view logic
        var query = from child in _context.Spans
                    join parent in _context.Spans 
                        on new { child.ParentSpanId, child.TraceId } 
                        equals new { ParentSpanId = parent.SpanId, parent.TraceId }
                    join parentRes in _context.Resources on parent.ResourceId equals parentRes.Id
                    join childRes in _context.Resources on child.ResourceId equals childRes.Id
                    select new { child, parent, parentRes, childRes };

        // Apply time filters if provided
        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(x => x.child.StartTimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(x => x.child.StartTimeUnixNano <= endTimeNano);
        }

        // First, get raw data from database (without accessing Attributes dictionary)
        var rawDependencies = await query
            .Select(x => new
            {
                ParentResourceAttributes = x.parentRes.Attributes,
                ChildResourceAttributes = x.childRes.Attributes,
                x.child.Kind,
                x.child.StatusCode,
                DurationNano = x.child.EndTimeUnixNano - x.child.StartTimeUnixNano
            })
            .ToListAsync(cancellationToken);

        // Then extract service names and filter in memory
        var dependencies = rawDependencies
            .Select(x => new
            {
                ParentService = x.ParentResourceAttributes != null && x.ParentResourceAttributes.ContainsKey("service.name")
                    ? x.ParentResourceAttributes["service.name"]?.ToString()
                    : null,
                ChildService = x.ChildResourceAttributes != null && x.ChildResourceAttributes.ContainsKey("service.name")
                    ? x.ChildResourceAttributes["service.name"]?.ToString()
                    : null,
                x.Kind,
                x.StatusCode,
                x.DurationNano
            })
            .Where(x => x.ParentService != null && 
                       x.ChildService != null && 
                       x.ParentService != x.ChildService) // Only cross-service calls
            .ToList();

        // Group and aggregate the results
        var serviceDependencies = dependencies
            .GroupBy(x => new { x.ParentService, x.ChildService, x.Kind })
            .Select(g => new ServiceDependency
            {
                ParentService = g.Key.ParentService!,
                ChildService = g.Key.ChildService!,
                SpanKind = g.Key.Kind,
                CallCount = g.Count(),
                AvgDurationMs = g.Average(x => x.DurationNano) / 1_000_000.0,
                MinDurationMs = g.Min(x => x.DurationNano) / 1_000_000.0,
                MaxDurationMs = g.Max(x => x.DurationNano) / 1_000_000.0,
                ErrorCount = g.Count(x => x.StatusCode == SpanStatusCode.ERROR),
                ErrorRate = (g.Count(x => x.StatusCode == SpanStatusCode.ERROR) / (double)g.Count()) * 100
            })
            .OrderByDescending(x => x.CallCount)
            .ToList();

        _logger.LogDebug("Retrieved {Count} service dependencies", serviceDependencies.Count);
        
        return serviceDependencies;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error retrieving service dependencies");
        throw;
    }
}

    /// <summary>
    /// Gets operation counts for a specific service
    /// </summary>
    public async Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            var query = _context.Spans
                .Include(s => s.Resource)
                .AsQueryable();

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(s => s.StartTimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(s => s.StartTimeUnixNano <= endTimeNano);
            }

            // First, get raw data from database
            var rawData = await query
                .Select(s => new
                {
                    s.Name,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then filter by service name in memory and count operations
            var counts = rawData
                .Where(s => s.ResourceAttributes != null && 
                           s.ResourceAttributes.ContainsKey("service.name") && 
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(s => s.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving operation counts for service {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Gets average latencies for operations in a specific service
    /// </summary>
    public async Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            var query = _context.Spans
                .Include(s => s.Resource)
                .AsQueryable();

            if (startTime.HasValue)
            {
                var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
                query = query.Where(s => s.StartTimeUnixNano >= startTimeNano);
            }

            if (endTime.HasValue)
            {
                var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
                query = query.Where(s => s.StartTimeUnixNano <= endTimeNano);
            }

            // First, get raw data from database
            var rawData = await query
                .Select(s => new
                {
                    s.Name,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then filter by service name in memory and calculate average latencies
            var latencies = rawData
                .Where(s => s.ResourceAttributes != null && 
                           s.ResourceAttributes.ContainsKey("service.name") && 
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(s => s.Name)
                .ToDictionary(
                    g => g.Key, 
                    g => g.Average(s => s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0
                );

            return latencies;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving average latencies for service {ServiceName}", serviceName);
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
    
    private SpanModel? ConvertToSpanModel(Span? span)
    {
        if (span == null)
        {
            return null;
        }
        
        var model = new SpanModel
        {
            TraceIdHex = span.TraceId,
            SpanIdHex = span.SpanId,
            ParentSpanIdHex = span.ParentSpanId,
            Name = span.Name,
            Kind = span.Kind,
            StartTimeUnixNano = span.StartTimeUnixNano,
            EndTimeUnixNano = span.EndTimeUnixNano,
            DroppedAttributesCount = span.DroppedAttributesCount,
            DroppedEventsCount = span.DroppedEventsCount,
            DroppedLinksCount = span.DroppedLinksCount,
            TraceState = span.TraceState,
            StatusCode = span.StatusCode,
            StatusMessage = span.StatusMessage,
            Attributes = span.Attributes,
            Events = span.Events?.Select(e => new SpanEventModel
            {
                Name = e.Name,
                TimeUnixNano = e.TimeUnixNano,
                DroppedAttributesCount = e.DroppedAttributesCount,
                Attributes = e.Attributes
            }).ToList() ?? new List<SpanEventModel>(),
            Links = span.Links?.Select(l => new SpanLinkModel
            {
                LinkedTraceIdHex = l.LinkedTraceId,
                LinkedSpanIdHex = l.LinkedSpanId,
                TraceState = l.TraceState,
                DroppedAttributesCount = l.DroppedAttributesCount,
                Attributes = l.Attributes
            }).ToList() ?? new List<SpanLinkModel>()
        };

        // Map resource if available
        if (span.Resource != null)
        {
            model.Resource = new ResourceModel
            {
                SchemaUrl = span.Resource.SchemaUrl,
                Attributes = span.Resource.Attributes ?? new Dictionary<string, object>()
            };
        }

        // Map instrumentation scope if available
        if (span.Scope != null)
        {
            model.InstrumentationScope = new InstrumentationScopeModel
            {
                Name = span.Scope.Name,
                Version = span.Scope.Version,
                SchemaUrl = span.Scope.SchemaUrl,
                Attributes = span.Scope.Attributes ?? new Dictionary<string, object>()
            };
        }
        
        return model;
    }
    
    private List<SpanModel> ConvertToSpanModels(IEnumerable<Span> spans)
    {
        var result = new List<SpanModel>();
        
        foreach (var span in spans)
        {
            var model = ConvertToSpanModel(span);
            if (model != null)
            {
                result.Add(model);
            }
        }
        
        return result;
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private async Task<long> StoreSpanInternalAsync(SpanModel spanModel, Resource resource, InstrumentationScope scope, CancellationToken cancellationToken)
    {
        // Create span entity
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
        await _context.SaveChangesAsync(cancellationToken);

        // Add span events
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

        // Add span links
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

        // Save all changes
        await _context.SaveChangesAsync(cancellationToken);

        return spanEntity.Id;
    }

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

    /// <summary>
    /// Extracts the service name from Resource attributes
    /// </summary>
    private static string? ExtractServiceName(Dictionary<string, object>? attributes)
    {
        if (attributes == null || !attributes.ContainsKey("service.name"))
            return null;
        
        return attributes["service.name"]?.ToString();
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
