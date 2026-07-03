using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Keryhe.Telemetry.Data;
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
        var storedSpanCount = 0;
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

            // Store traces using the repository
            var storedTraceIds = await _traceRepository.StoreTracesBatchAsync(traces, context.CancellationToken);
            var storedTraceCount = storedTraceIds.Count();
            totalSpanCount = traces.Sum(t => t.Spans.Count);
            storedSpanCount = traces.Where(t => storedTraceIds.Contains(t.Spans.FirstOrDefault()?.TraceIdHex ?? "")) .Sum(t => t.Spans.Count);

            _logger.LogInformation("Successfully stored {StoredTraceCount} traces with {TotalSpanCount} spans", 
                storedTraceCount, totalSpanCount);
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
                RejectedSpans = Math.Max(0, totalSpanCount - storedSpanCount),
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

                    var spanModel = ConvertSpan(span, resourceModel, instrumentationScopeModel);
                    trace.Spans.Add(spanModel);
                }
            }
        }

        return traceGroups.Values.ToList();
    }

    /// <summary>
    /// Converts OTLP Resource to ResourceModel
    /// </summary>
    private ResourceModel? ConvertResource(OpenTelemetry.Proto.Trace.V1.ResourceSpans? resourceSpan, long tenantId)
    {
        if (resourceSpan?.Resource == null)
            return null;

        return new ResourceModel
        {
            TenantId = tenantId,
            SchemaUrl = string.IsNullOrEmpty(resourceSpan.SchemaUrl) ? null : resourceSpan.SchemaUrl,
            Attributes = ConvertAttributes(resourceSpan.Resource.Attributes)
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
    /// Converts OTLP Span to SpanModel
    /// </summary>
    private SpanModel ConvertSpan(Span span, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var traceIdHex = ConvertTraceId(span.TraceId);
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