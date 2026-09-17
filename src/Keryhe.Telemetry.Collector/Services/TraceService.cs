using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Collector.Services.Helpers;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// gRPC service implementation for OpenTelemetry traces collection.
/// Handles OTLP (OpenTelemetry Protocol) trace export requests and stores them using TraceRepository.
/// </summary>
public class TraceService : OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceBase
{
    private readonly ITraceWriteRepository _traceRepository;
    private readonly ILogger<TraceService> _logger;
    private readonly ITenantResolver _tenantResolver;

    public TraceService(ITraceWriteRepository traceRepository, ILogger<TraceService> logger, ITenantResolver tenantResolver)
    {
        _traceRepository = traceRepository ?? throw new ArgumentNullException(nameof(traceRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    }

    /// <summary>
    /// Handles the Export gRPC call for trace data.
    /// Converts the incoming OTLP trace data to Models and stores them using the TraceRepository.
    ///
    /// <c>PartialSuccess.RejectedSpans</c> is honest only at ENQUEUE time, not at durable-storage
    /// time: it is 0 once <see cref="ITraceWriteRepository.StoreTracesBatchAsync"/> returns (the
    /// whole batch was accepted onto <c>TelemetryIngestionChannel</c>), or the full span count if
    /// enqueueing itself threw. Storage is asynchronous past that point --
    /// <c>TelemetryIngestionWorker</c> flushes the channel on a delay, with its own bounded retry,
    /// and a batch that still fails after retries are exhausted is dropped with no way to signal
    /// this caller, who has long since received its (successful) response. That drop is observable
    /// via <c>Keryhe.Telemetry.Core.Data.IngestionMetrics</c>'s <c>records_dropped</c> counter and
    /// the worker's "batch dropped" log line, never via this response. This was previously
    /// disguised as a real check (`storedTraceIds.Contains(...)`) that in fact always evaluated to
    /// "everything succeeded," an O(n²) computation for a foregone conclusion; reporting the
    /// honest, cheaper number in its place is deliberate, not a regression.
    /// </summary>
    /// <param name="request">The ExportTraceServiceRequest containing trace data</param>
    /// <param name="context">The gRPC server call context</param>
    /// <returns>ExportTraceServiceResponse indicating success or failure</returns>
    public override async Task<ExportTraceServiceResponse> Export(
        ExportTraceServiceRequest request,
        ServerCallContext context)
    {
        if (request == null)
        {
            _logger.LogError("Received null ExportTraceServiceRequest");
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, "Request cannot be null"));
        }

        var errorMessage = string.Empty;
        var traces = new List<TraceModel>();
        var totalSpanCount = 0;
        try
        {
            string? keyHash = ApiKeyHelper.GetKeyHash(context);
            var tenantId = await _tenantResolver.ResolveTenantIdAsync(keyHash, context.CancellationToken);
            if (tenantId <= 0)
                throw new RpcException(new Grpc.Core.Status(StatusCode.Unauthenticated, "Invalid API key."));

            _logger.LogDebug("Received traces export request with {ResourceSpansCount} resource spans",
                request.ResourceSpans?.Count ?? 0);

            // Convert protobuf message to Models
            traces = ConvertToTraceModels(request, tenantId);

            if (!traces.Any())
            {
                _logger.LogDebug("No traces found in the request");
                return new ExportTraceServiceResponse
                {
                    PartialSuccess = new ExportTracePartialSuccess
                    {
                        RejectedSpans = 0,
                        ErrorMessage = string.Empty
                    }
                };
            }

            // Computed before the store call (not after) so that if enqueueing itself throws,
            // totalSpanCount is still populated and the catch-path response below reports the
            // full span count as rejected instead of silently defaulting to 0.
            totalSpanCount = traces.Sum(t => t.Spans.Count);

            // Store traces using the repository. A successful return means the whole batch was
            // accepted onto the ingestion channel -- see the honesty note on this method's doc
            // comment for what that does and does not guarantee.
            await _traceRepository.StoreTracesBatchAsync(traces, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Trace export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            errorMessage = "Invalid argument in trace export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing trace export request";
            _logger.LogError(ex, errorMessage);
        }

        return new ExportTraceServiceResponse
        {
            PartialSuccess = new ExportTracePartialSuccess
            {
                RejectedSpans = string.IsNullOrEmpty(errorMessage) ? 0 : totalSpanCount,
                ErrorMessage = errorMessage
            }
        };
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
            SchemaUrl = string.IsNullOrEmpty(resourceSpan?.SchemaUrl) ? null : resourceSpan.SchemaUrl,
            // Mirrors NormalizeResource's synthetic resource, but carrying the real tenant.
            Attributes = resourceSpan?.Resource == null
                ? new Dictionary<string, object> { { "service.name", "unknown" } }
                : ConvertAttributes(resourceSpan.Resource.Attributes)
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
            Name = scope.Name ?? "unknown",
            Version = string.IsNullOrEmpty(scope.Version) ? null : scope.Version,
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : schemaUrl,
            Attributes = ConvertAttributes(scope.Attributes)
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

        var Model = new SpanModel
        {
            TraceIdHex = traceIdHex,
            SpanIdHex = spanIdHex,
            ParentSpanIdHex = parentSpanIdHex,
            Name = span.Name ?? "unknown",
            Kind = ConvertSpanKind(span.Kind),
            StartTimeUnixNano = (long)span.StartTimeUnixNano,
            EndTimeUnixNano = (long)span.EndTimeUnixNano,
            DroppedAttributesCount = (int)span.DroppedAttributesCount,
            DroppedEventsCount = (int)span.DroppedEventsCount,
            DroppedLinksCount = (int)span.DroppedLinksCount,
            TraceState = string.IsNullOrEmpty(span.TraceState) ? null : span.TraceState,
            Flags = (int)span.Flags,
            StatusCode = ConvertSpanStatusCode(span.Status?.Code ?? OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Unset),
            StatusMessage = string.IsNullOrEmpty(span.Status?.Message) ? null : span.Status.Message,
            Attributes = ConvertAttributes(span.Attributes),
            Events = ConvertSpanEvents(span.Events),
            Links = ConvertSpanLinks(span.Links),
            Resource = resource,
            InstrumentationScope = scope
        };

        return Model;
    }

    /// <summary>
    /// Converts OTLP span events to SpanEventModel list
    /// </summary>
    private List<SpanEventModel> ConvertSpanEvents(Google.Protobuf.Collections.RepeatedField<Span.Types.Event>? events)
    {
        if (events == null || !events.Any())
            return new List<SpanEventModel>();

        return events.Select(e => new SpanEventModel
        {
            Name = e.Name ?? "unknown",
            TimeUnixNano = (long)e.TimeUnixNano,
            DroppedAttributesCount = (int)e.DroppedAttributesCount,
            Attributes = ConvertAttributes(e.Attributes)
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP span links to SpanLinkModel list
    /// </summary>
    private List<SpanLinkModel> ConvertSpanLinks(Google.Protobuf.Collections.RepeatedField<Span.Types.Link>? links)
    {
        if (links == null || !links.Any())
            return new List<SpanLinkModel>();

        var linkModels = new List<SpanLinkModel>();

        foreach (var link in links)
        {
            var linkedTraceIdHex = ConvertTraceId(link.TraceId);
            var linkedSpanIdHex = ConvertSpanId(link.SpanId);

            if (!string.IsNullOrEmpty(linkedTraceIdHex) && !string.IsNullOrEmpty(linkedSpanIdHex))
            {
                linkModels.Add(new SpanLinkModel
                {
                    LinkedTraceIdHex = linkedTraceIdHex,
                    LinkedSpanIdHex = linkedSpanIdHex,
                    TraceState = string.IsNullOrEmpty(link.TraceState) ? null : link.TraceState,
                    Flags = (int)link.Flags,
                    DroppedAttributesCount = (int)link.DroppedAttributesCount,
                    Attributes = ConvertAttributes(link.Attributes)
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
    /// Converts OTLP attributes to a dictionary
    /// </summary>
    private Dictionary<string, object> ConvertAttributes(Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes)
    {
        var result = new Dictionary<string, object>();

        if (attributes == null)
            return result;

        foreach (var attr in attributes)
        {
            if (string.IsNullOrEmpty(attr.Key) || attr.Value == null)
                continue;

            var value = ConvertAnyValue(attr.Value);
            if (value != null)
            {
                result[attr.Key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Converts OTLP AnyValue to .NET object
    /// </summary>
    private object? ConvertAnyValue(AnyValue anyValue)
    {
        return anyValue.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => anyValue.StringValue,
            AnyValue.ValueOneofCase.BoolValue => anyValue.BoolValue,
            AnyValue.ValueOneofCase.IntValue => anyValue.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => anyValue.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => anyValue.BytesValue.ToByteArray(),
            AnyValue.ValueOneofCase.ArrayValue => ConvertArrayValue(anyValue.ArrayValue),
            AnyValue.ValueOneofCase.KvlistValue => ConvertKeyValueList(anyValue.KvlistValue),
            _ => anyValue.ToString()
        };
    }

    /// <summary>
    /// Converts OTLP ArrayValue to .NET array
    /// </summary>
    private object[] ConvertArrayValue(ArrayValue arrayValue)
    {
        return arrayValue.Values
            .Select(ConvertAnyValue)
            .Where(v => v != null)
            .ToArray()!;
    }

    /// <summary>
    /// Converts OTLP KeyValueList to .NET dictionary
    /// </summary>
    private Dictionary<string, object> ConvertKeyValueList(KeyValueList kvList)
    {
        var result = new Dictionary<string, object>();

        foreach (var kv in kvList.Values)
        {
            if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
            {
                var value = ConvertAnyValue(kv.Value);
                if (value != null)
                {
                    result[kv.Key] = value;
                }
            }
        }

        return result;
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