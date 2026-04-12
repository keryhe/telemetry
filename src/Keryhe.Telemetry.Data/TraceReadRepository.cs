using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

// =============================================================================
// TRACE READ REPOSITORY IMPLEMENTATION
// =============================================================================

public class TraceReadRepository : ITraceReadRepository
{
    private readonly TelemetryReadDbContext _context;
    private readonly ILogger<TraceReadRepository> _logger;

    public TraceReadRepository(TelemetryReadDbContext context, ILogger<TraceReadRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            var rawData = await _context.Spans
                .Include(s => s.Resource)
                .Where(s => s.StartTimeUnixNano >= startTimeNano && s.StartTimeUnixNano <= endTimeNano)
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    s.Name,
                    s.ParentSpanId,
                    SpanAttributes = s.Attributes,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            var rawTraces = rawData
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    HasErrors = g.Any(s => s.StatusCode == SpanStatusCode.ERROR),
                    RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            return rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = t.ServiceName,
                RootOperationName = t.RootSpan.Name,
                RootSpanAttributes = t.RootSpan.SpanAttributes,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano)
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();
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

            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    s.Name,
                    s.ParentSpanId,
                    SpanAttributes = s.Attributes,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            var filteredSpans = rawData
                .Where(s => s.ResourceAttributes != null &&
                           s.ResourceAttributes.ContainsKey("service.name") &&
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .ToList();

            var rawTraces = filteredSpans
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    HasErrors = g.Any(s => s.StatusCode == SpanStatusCode.ERROR),
                    RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First()
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            return rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = serviceName,
                RootOperationName = t.RootSpan.Name,
                RootSpanAttributes = t.RootSpan.SpanAttributes,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano)
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();
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

            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.Name,
                    s.ParentSpanId,
                    SpanAttributes = s.Attributes,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            var rawTraces = rawData
                .GroupBy(s => s.TraceId)
                .Select(g => new
                {
                    TraceIdHex = g.Key,
                    SpanCount = g.Count(),
                    MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                    RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .OrderByDescending(t => t.MinStartTimeNano)
                .Take(limit)
                .ToList();

            return rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = true,
                ServiceName = t.ServiceName,
                RootOperationName = t.RootSpan.Name,
                RootSpanAttributes = t.RootSpan.SpanAttributes,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano)
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();
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

            var rawData = await query
                .Select(s => new
                {
                    s.TraceId,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    s.StatusCode,
                    s.Name,
                    s.ParentSpanId,
                    SpanAttributes = s.Attributes,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

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
                    RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                    ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano)
                                                      .FirstOrDefault()?.ResourceAttributes)
                })
                .Where(t => t.DurationNano >= minDurationNano)
                .OrderByDescending(t => t.DurationNano)
                .Take(limit)
                .ToList();

            return rawTraces.Select(t => new TraceInfo
            {
                TraceIdHex = t.TraceIdHex,
                SpanCount = t.SpanCount,
                TraceStartTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano),
                TraceEndTime = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano),
                HasErrors = t.HasErrors,
                ServiceName = t.ServiceName,
                RootOperationName = t.RootSpan.Name,
                RootSpanAttributes = t.RootSpan.SpanAttributes,
                TraceDuration = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MaxEndTimeNano)
                              - OpenTelemetryDbContextExtensions.UnixNanoToDateTime(t.MinStartTimeNano)
            }).ToList();
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
            var query = from child in _context.Spans
                        join parent in _context.Spans
                            on new { child.ParentSpanId, child.TraceId }
                            equals new { ParentSpanId = parent.SpanId, parent.TraceId }
                        join parentRes in _context.Resources on parent.ResourceId equals parentRes.Id
                        join childRes in _context.Resources on child.ResourceId equals childRes.Id
                        select new { child, parent, parentRes, childRes };

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
                           x.ParentService != x.ChildService)
                .ToList();

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
                query = query.Where(s => s.EndTimeUnixNano <= endTimeNano);
            }

            var rawData = await query
                .Select(s => new
                {
                    s.Name,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            return rawData
                .Where(s => s.ResourceAttributes != null &&
                           s.ResourceAttributes.ContainsKey("service.name") &&
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(s => s.Name)
                .ToDictionary(g => g.Key, g => g.Count());
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
                query = query.Where(s => s.EndTimeUnixNano <= endTimeNano);
            }

            var rawData = await query
                .Select(s => new
                {
                    s.Name,
                    s.StartTimeUnixNano,
                    s.EndTimeUnixNano,
                    ResourceAttributes = s.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            return rawData
                .Where(s => s.ResourceAttributes != null &&
                           s.ResourceAttributes.ContainsKey("service.name") &&
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(s => s.Name)
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(s => s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0
                );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving average latencies for service {ServiceName}", serviceName);
            throw;
        }
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private SpanModel? ConvertToSpanModel(Span? span)
    {
        if (span == null)
            return null;

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

        if (span.Resource != null)
        {
            model.Resource = new ResourceModel
            {
                SchemaUrl = span.Resource.SchemaUrl,
                Attributes = span.Resource.Attributes ?? new Dictionary<string, object>()
            };
        }

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
                result.Add(model);
        }
        return result;
    }

    private static string? ExtractServiceName(Dictionary<string, object>? attributes)
    {
        if (attributes == null || !attributes.ContainsKey("service.name"))
            return null;

        return attributes["service.name"]?.ToString();
    }
}
