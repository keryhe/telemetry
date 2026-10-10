using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Collector.Authentication;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// Converts OTLP trace exports to models and enqueues them (see <see cref="IngestAsync"/>). The gRPC service and the HTTP endpoint are thin
/// adapters over it, so both transports share conversion, limits, queue behaviour and counters.
/// </summary>
public sealed class OtlpTraceIngestor
{
    private readonly ITraceWriteRepository _traceRepository;
    private readonly ILogger<OtlpTraceIngestor> _logger;
    private readonly TelemetryIngestionChannel _channel;
    private readonly OtlpAttributeConverter _converter;
    private readonly IngestionMetrics _metrics;

    public OtlpTraceIngestor(ITraceWriteRepository traceRepository, ILogger<OtlpTraceIngestor> logger, TelemetryIngestionChannel channel, IngestionMetrics metrics, OtlpAttributeConverter converter)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _traceRepository = traceRepository ?? throw new ArgumentNullException(nameof(traceRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Converts an OTLP export to models and enqueues them for the signal's write path, for whichever transport received it. The
    /// caller has already authenticated the export and supplies its tenant.
    ///
    /// <c>Rejected</c> in the result is honest only at ENQUEUE time, not at durable-storage time: 0 once the whole export is on the
    /// ingestion channel, or the full record count when conversion or enqueueing itself failed for a reason that is the request's
    /// (<c>ErrorMessage</c> says which). Storage is asynchronous past that point; a batch that still fails after the worker's retries is
    /// dropped with no way to signal this caller, and shows on <c>records_dropped</c> and the worker's log line instead.
    /// A queue that cannot take the export, or a collector that is shutting down, throws <see cref="IngestionRejectedException"/> with
    /// nothing enqueued: the transport answers with its retryable status.
    /// </summary>
    public async Task<IngestResult> IngestAsync(ExportTraceServiceRequest request, long tenantId, string protocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errorMessage = string.Empty;
        var requestCount = 0;
        var traces = new List<TraceModel>();
        var totalSpanCount = 0;
        try
        {
            // A full queue refuses the export now, before it is converted (the repository's bounded wait applies after).
            requestCount = request.ResourceSpans.Sum(r => r.ScopeSpans.Sum(s => s.Spans.Count));
            _channel.ThrowIfSaturated("traces", _channel.TraceGate);

            _logger.LogDebug("Received traces export request with {ResourceSpansCount} resource spans", request.ResourceSpans?.Count ?? 0);

            traces = ConvertToTraceModels(request, tenantId);
            if (traces.Count == 0)
            {
                _logger.LogDebug("No traces found in the request");
                return IngestResult.Ok;
            }

            // Computed before the store call (not after) so that if enqueueing itself throws, the count is still known
            // and the result reports the full span count as rejected instead of defaulting to 0.
            totalSpanCount = traces.Sum(t => t.Spans.Count);

            await _traceRepository.StoreTracesBatchAsync(traces, cancellationToken, request.CalculateSize());
            _metrics.RecordAccepted("traces", tenantId, totalSpanCount, protocol);
        }
        catch (IngestionRejectedException rejection)
        {
            _metrics.RecordRefused("traces", tenantId, rejection.Reason, requestCount, protocol);
            _logger.LogDebug("Refused traces export for tenant {TenantId}: {Reason}", tenantId, rejection.Reason);
            throw;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // The ingestion worker is draining for host shutdown and has closed the channel. A retryable refusal, not a
            // partial-success rejection (which OTLP clients never retry), so the client resends -- behind a load balancer, to another instance.
            _metrics.RecordRefused("traces", tenantId, RefusalReasons.ShuttingDown, requestCount, protocol);
            _logger.LogWarning("Rejected traces export: collector is shutting down");
            throw new IngestionRejectedException("traces", RefusalReasons.ShuttingDown, TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Trace export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            _metrics.RecordRefused("traces", tenantId, RefusalReasons.Invalid, requestCount, protocol);
            errorMessage = "Invalid argument in trace export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing trace export request";
            _logger.LogError(ex, errorMessage);
        }

        // When conversion itself failed the whole export is unusable, and the span count was never reached: report the request's.
        return new IngestResult(string.IsNullOrEmpty(errorMessage) ? 0 : totalSpanCount > 0 ? totalSpanCount : requestCount, errorMessage);
    }

    /// <summary>
    /// Converts OTLP ExportTraceServiceRequest to a list of TraceModel objects
    /// </summary>
    private List<TraceModel> ConvertToTraceModels(ExportTraceServiceRequest request, long tenantId)
    {
        var traceGroups = new Dictionary<string, TraceModel>();

        foreach (var resourceSpans in request.ResourceSpans)
        {
            // Convert resource information
            var resourceModel = ConvertResource(resourceSpans, tenantId);

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                // Convert instrumentation scope information
                var instrumentationScopeModel = ConvertInstrumentationScope(scopeSpans.SchemaUrl, scopeSpans.Scope);

                foreach (var span in scopeSpans.Spans)
                {
                    var traceIdHex = ConvertTraceId(span.TraceId);
                    if (string.IsNullOrEmpty(traceIdHex))
                    {
                        _logger.LogWarning("Skipping span with invalid trace ID");
                        continue;
                    }

                    // Group spans by trace ID
                    if (!traceGroups.TryGetValue(traceIdHex, out var trace))
                    {
                        trace = new TraceModel
                        {
                            Resource = resourceModel,
                            InstrumentationScope = instrumentationScopeModel,
                            Spans = new List<SpanModel>()
                        };
                        traceGroups[traceIdHex] = trace;
                    }

                    var spanModel = ConvertSpan(span, traceIdHex, resourceModel, instrumentationScopeModel);
                    trace.Spans.Add(spanModel);
                }
            }
        }

        return traceGroups.Values.ToList();
    }

    /// <summary>
    /// Converts OTLP Resource to ResourceModel.
    ///
    /// Never returns null, even when the export carries no Resource block. OTLP permits omitting it,
    /// and the fallback has to be built HERE because this is the last point where the authenticated
    /// tenant is still known: NormalizeResource's own null fallback runs inside the bulk writer, which
    /// has no tenant and can only default to tenant 1 -- filing a resource-less export from any tenant
    /// under tenant 1's telemetry.
    /// </summary>
    private ResourceModel ConvertResource(OpenTelemetry.Proto.Trace.V1.ResourceSpans? resourceSpan, long tenantId)
    {
        return new ResourceModel
        {
            TenantId = tenantId,
            SchemaUrl = string.IsNullOrEmpty(resourceSpan?.SchemaUrl) ? null : _converter.ClipColumn(resourceSpan.SchemaUrl, ColumnLimits.SchemaUrl, "traces", "schema_url"),
            // Mirrors NormalizeResource's synthetic resource, but carrying the real tenant. Truncated before the resource is hashed.
            Attributes = resourceSpan?.Resource == null
                ? new Dictionary<string, object> { { "service.name", "unknown" } }
                : _converter.ClipServiceName(_converter.ConvertAttributes(resourceSpan.Resource.Attributes, "traces"), "traces")
        };
    }

    /// <summary>
    /// Converts OTLP InstrumentationScope to InstrumentationScopeModel
    /// </summary>
    private InstrumentationScopeModel? ConvertInstrumentationScope(string schemaUrl, InstrumentationScope? scope)
    {
        if (scope == null)
            return null;

        return new InstrumentationScopeModel
        {
            Name = _converter.ClipColumn(scope.Name, ColumnLimits.ScopeName, "traces", "scope_name") ?? "unknown",
            Version = string.IsNullOrEmpty(scope.Version) ? null : _converter.ClipColumn(scope.Version, ColumnLimits.ScopeVersion, "traces", "scope_version"),
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : _converter.ClipColumn(schemaUrl, ColumnLimits.SchemaUrl, "traces", "schema_url"),
            Attributes = _converter.ConvertAttributes(scope.Attributes, "traces")
        };
    }

    /// <summary>
    /// Converts OTLP Span to SpanModel. <paramref name="traceIdHex"/> is passed in rather than
    /// re-derived from <c>span.TraceId</c>: the caller (<see cref="ConvertToTraceModels"/>) already
    /// converted it once to group spans by trace, so re-converting here would hex-encode the same
    /// 16 bytes a second time for every span.
    /// </summary>
    private SpanModel ConvertSpan(Span span, string traceIdHex, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var spanIdHex = ConvertSpanId(span.SpanId);
        var parentSpanIdHex = ConvertSpanId(span.ParentSpanId);

        if (string.IsNullOrEmpty(traceIdHex) || string.IsNullOrEmpty(spanIdHex))
        {
            throw new ArgumentException($"Invalid span with trace ID '{traceIdHex}' and span ID '{spanIdHex}'");
        }

        var attributes = _converter.ConvertAttributes(span.Attributes, "traces", out var droppedAttributes);
        var eventCount = _converter.Allow(span.Events.Count, _converter.Limits.MaxEventsPerSpan, "traces", "events", out var droppedEvents);
        var linkCount = _converter.Allow(span.Links.Count, _converter.Limits.MaxLinksPerSpan, "traces", "links", out var droppedLinks);

        var Model = new SpanModel
        {
            TraceIdHex = traceIdHex,
            SpanIdHex = spanIdHex,
            ParentSpanIdHex = parentSpanIdHex,
            Name = _converter.ClipColumn(span.Name, ColumnLimits.SpanName, "traces", "span_name") ?? "unknown",
            Kind = ConvertSpanKind(span.Kind),
            StartTimeUnixNano = (long)span.StartTimeUnixNano,
            EndTimeUnixNano = (long)span.EndTimeUnixNano,
            DroppedAttributesCount = (int)span.DroppedAttributesCount + droppedAttributes,
            DroppedEventsCount = (int)span.DroppedEventsCount + droppedEvents,
            DroppedLinksCount = (int)span.DroppedLinksCount + droppedLinks,
            TraceState = string.IsNullOrEmpty(span.TraceState) ? null : span.TraceState,
            Flags = (int)span.Flags,
            StatusCode = ConvertSpanStatusCode(span.Status?.Code ?? OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Unset),
            StatusMessage = string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status.Message,
            Attributes = attributes,
            Events = ConvertSpanEvents(span.Events, eventCount),
            Links = ConvertSpanLinks(span.Links, linkCount),
            Resource = resource,
            InstrumentationScope = scope
        };

        return Model;
    }

    /// <summary>
    /// Converts OTLP span events to SpanEventModel list
    /// </summary>
    private List<SpanEventModel> ConvertSpanEvents(Google.Protobuf.Collections.RepeatedField<Span.Types.Event>? events, int keep)
    {
        if (events == null || !events.Any())
            return new List<SpanEventModel>();

        return events.Take(keep).Select(e =>
        {
            var attrs = _converter.ConvertAttributes(e.Attributes, "traces", out var droppedAttrs);
            return new SpanEventModel
            {
                Name = e.Name ?? "unknown",
                TimeUnixNano = (long)e.TimeUnixNano,
                DroppedAttributesCount = (int)e.DroppedAttributesCount + droppedAttrs,
                Attributes = attrs
            };
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP span links to SpanLinkModel list
    /// </summary>
    private List<SpanLinkModel> ConvertSpanLinks(Google.Protobuf.Collections.RepeatedField<Span.Types.Link>? links, int keep)
    {
        if (links == null || !links.Any())
            return new List<SpanLinkModel>();

        var linkModels = new List<SpanLinkModel>();

        foreach (var link in links.Take(keep))
        {
            var linkedTraceIdHex = ConvertTraceId(link.TraceId);
            var linkedSpanIdHex = ConvertSpanId(link.SpanId);

            if (!string.IsNullOrEmpty(linkedTraceIdHex) && !string.IsNullOrEmpty(linkedSpanIdHex))
            {
                var linkAttributes = _converter.ConvertAttributes(link.Attributes, "traces", out var droppedLinkAttributes);
                linkModels.Add(new SpanLinkModel
                {
                    LinkedTraceIdHex = linkedTraceIdHex,
                    LinkedSpanIdHex = linkedSpanIdHex,
                    TraceState = string.IsNullOrEmpty(link.TraceState) ? null : link.TraceState,
                    Flags = (int)link.Flags,
                    DroppedAttributesCount = (int)link.DroppedAttributesCount + droppedLinkAttributes,
                    Attributes = linkAttributes
                });
            }
            else
            {
                _logger.LogWarning("Skipping span link with invalid trace ID '{LinkedTraceId}' or span ID '{LinkedSpanId}'", 
                    linkedTraceIdHex, linkedSpanIdHex);
            }
        }

        return linkModels;
    }

    /// <summary>
    /// Converts OTLP SpanKind to local SpanKind enum
    /// </summary>
    private SpanKind ConvertSpanKind(Span.Types.SpanKind spanKind)
    {
        return spanKind switch
        {
            Span.Types.SpanKind.Internal => SpanKind.INTERNAL,
            Span.Types.SpanKind.Server => SpanKind.SERVER,
            Span.Types.SpanKind.Client => SpanKind.CLIENT,
            Span.Types.SpanKind.Producer => SpanKind.PRODUCER,
            Span.Types.SpanKind.Consumer => SpanKind.CONSUMER,
            _ => SpanKind.UNSPECIFIED
        };
    }

    /// <summary>
    /// Converts OTLP Status.StatusCode to local SpanStatusCode enum
    /// </summary>
    private SpanStatusCode ConvertSpanStatusCode(OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode statusCode)
    {
        return statusCode switch
        {
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Ok => SpanStatusCode.OK,
            OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error => SpanStatusCode.ERROR,
            _ => SpanStatusCode.UNSET
        };
    }

    /// <summary>
    /// Converts trace ID bytes to hex string representation
    /// </summary>
    private string? ConvertTraceId(Google.Protobuf.ByteString? traceId)
    {
        if (traceId == null || traceId.IsEmpty)
            return null;

        var bytes = traceId.ToByteArray();
        if (bytes.Length != 16)
        {
            _logger.LogWarning("Invalid trace ID length: {Length}, expected 16 bytes", bytes.Length);
            return null;
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Converts span ID bytes to hex string representation
    /// </summary>
    private string? ConvertSpanId(Google.Protobuf.ByteString? spanId)
    {
        if (spanId == null || spanId.IsEmpty)
            return null;

        var bytes = spanId.ToByteArray();
        if (bytes.Length != 8)
        {
            _logger.LogWarning("Invalid span ID length: {Length}, expected 8 bytes", bytes.Length);
            return null;
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}