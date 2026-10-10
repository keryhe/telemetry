using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Collector.Authentication;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// Converts OTLP log exports to models and enqueues them (see <see cref="IngestAsync"/>). The gRPC service and the HTTP endpoint are thin
/// adapters over it, so both transports share conversion, limits, queue behaviour and counters.
/// </summary>
public sealed class OtlpLogIngestor
{
    private readonly ILogWriteRepository _logRepository;
    private readonly ILogger<OtlpLogIngestor> _logger;
    private readonly TelemetryIngestionChannel _channel;
    private readonly OtlpAttributeConverter _converter;
    private readonly IngestionMetrics _metrics;

    public OtlpLogIngestor(ILogWriteRepository logRepository, ILogger<OtlpLogIngestor> logger, TelemetryIngestionChannel channel, IngestionMetrics metrics, OtlpAttributeConverter converter)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logRepository = logRepository ?? throw new ArgumentNullException(nameof(logRepository));
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
    public async Task<IngestResult> IngestAsync(ExportLogsServiceRequest request, long tenantId, string protocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errorMessage = string.Empty;
        var requestCount = 0;
        var logRecords = new List<LogRecordModel>();
        var storedLogCount = 0;
        try
        {
            // A full queue refuses the export now, before it is converted (the repository's bounded wait applies after).
            requestCount = request.ResourceLogs.Sum(r => r.ScopeLogs.Sum(s => s.LogRecords.Count));
            _channel.ThrowIfSaturated("logs", _channel.LogGate);

            _logger.LogDebug("Received logs export request with {ResourceLogsCount} resource logs", request.ResourceLogs?.Count ?? 0);

            logRecords = ConvertToLogRecordModels(request, tenantId);
            if (logRecords.Count == 0)
            {
                _logger.LogDebug("No log records found in the request");
                return IngestResult.Ok;
            }

            await _logRepository.StoreLogRecordsBatchAsync(logRecords, cancellationToken, request.CalculateSize());
            storedLogCount = logRecords.Count;
            _metrics.RecordAccepted("logs", tenantId, storedLogCount, protocol);
        }
        catch (IngestionRejectedException rejection)
        {
            _metrics.RecordRefused("logs", tenantId, rejection.Reason, requestCount, protocol);
            _logger.LogDebug("Refused logs export for tenant {TenantId}: {Reason}", tenantId, rejection.Reason);
            throw;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // The ingestion worker is draining for host shutdown and has closed the channel. A retryable refusal, not a
            // partial-success rejection (which OTLP clients never retry), so the client resends -- behind a load balancer, to another instance.
            _metrics.RecordRefused("logs", tenantId, RefusalReasons.ShuttingDown, requestCount, protocol);
            _logger.LogWarning("Rejected logs export: collector is shutting down");
            throw new IngestionRejectedException("logs", RefusalReasons.ShuttingDown, TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Log export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            _metrics.RecordRefused("logs", tenantId, RefusalReasons.Invalid, requestCount, protocol);
            errorMessage = "Invalid argument in log export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing log export request";
            _logger.LogError(ex, errorMessage);
        }

        var rejected = Math.Max(0, logRecords.Count - storedLogCount);
        if (rejected == 0 && errorMessage.Length > 0 && logRecords.Count == 0) rejected = requestCount;   // conversion itself failed: the whole export is unusable
        return new IngestResult(rejected, errorMessage);
    }

    /// <summary>
    /// Converts OTLP ExportLogsServiceRequest to a list of LogRecordModel objects
    /// </summary>
    private List<LogRecordModel> ConvertToLogRecordModels(ExportLogsServiceRequest request, long tenantId)
    {
        var logRecords = new List<LogRecordModel>();

        foreach (var resourceLogs in request.ResourceLogs)
        {
            // Convert resource information
            var resourceModel = ConvertResource(resourceLogs.SchemaUrl, resourceLogs.Resource, tenantId);

            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                // Convert instrumentation scope information
                var instrumentationScopeModel = ConvertInstrumentationScope(scopeLogs);

                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    var logRecordModel = ConvertLogRecord(logRecord, resourceModel, instrumentationScopeModel);
                    logRecords.Add(logRecordModel);
                }
            }
        }

        return logRecords;
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
    private ResourceModel ConvertResource(string schemaUrl, OpenTelemetry.Proto.Resource.V1.Resource? resource, long tenantId)
    {
        return new ResourceModel
        {
            TenantId = tenantId,
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : _converter.ClipColumn(schemaUrl, ColumnLimits.SchemaUrl, "logs", "schema_url"),
            // Mirrors NormalizeResource's synthetic resource, but carrying the real tenant. Truncated before the resource is hashed.
            Attributes = resource == null
                ? new Dictionary<string, object> { { "service.name", "unknown" } }
                : _converter.ClipServiceName(_converter.ConvertAttributes(resource.Attributes, "logs"), "logs")
        };
    }

    /// <summary>
    /// Converts OTLP InstrumentationScope to InstrumentationScopeModel
    /// </summary>
    private InstrumentationScopeModel? ConvertInstrumentationScope(ScopeLogs? scopeLogs)
    {
        if (scopeLogs?.Scope == null)
            return null;

        return new InstrumentationScopeModel
        {
            Name = _converter.ClipColumn(scopeLogs.Scope.Name, ColumnLimits.ScopeName, "logs", "scope_name") ?? "unknown",
            Version = string.IsNullOrEmpty(scopeLogs.Scope.Version) ? null : _converter.ClipColumn(scopeLogs.Scope.Version, ColumnLimits.ScopeVersion, "logs", "scope_version"),
            SchemaUrl = string.IsNullOrEmpty(scopeLogs.SchemaUrl) ? null : _converter.ClipColumn(scopeLogs.SchemaUrl, ColumnLimits.SchemaUrl, "logs", "schema_url"),
            Attributes = _converter.ConvertAttributes(scopeLogs.Scope.Attributes, "logs")
        };
    }

    /// <summary>
    /// Converts OTLP LogRecord to LogRecordModel
    /// </summary>
    private LogRecordModel ConvertLogRecord(LogRecord logRecord, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var attributes = _converter.ConvertAttributes(logRecord.Attributes, "logs", out var droppedAttributes);
        var model = new LogRecordModel
        {
            TimeUnixNano = logRecord.TimeUnixNano == 0 ? null : (long)logRecord.TimeUnixNano,
            ObservedTimeUnixNano = logRecord.ObservedTimeUnixNano == 0 ? null : (long)logRecord.ObservedTimeUnixNano,
            SeverityNumber = logRecord.SeverityNumber == 0 ? null : (int)logRecord.SeverityNumber,
            SeverityText = string.IsNullOrEmpty(logRecord.SeverityText) ? null : _converter.ClipColumn(logRecord.SeverityText, ColumnLimits.SeverityText, "logs", "severity_text"),
            EventName = string.IsNullOrEmpty(logRecord.EventName) ? null : _converter.ClipColumn(logRecord.EventName, ColumnLimits.EventName, "logs", "event_name"),
            DroppedAttributesCount = (int)logRecord.DroppedAttributesCount + droppedAttributes,
            Flags = (int)logRecord.Flags,
            TraceIdHex = ConvertTraceId(logRecord.TraceId),
            SpanIdHex = ConvertSpanId(logRecord.SpanId),
            Attributes = attributes,
            Resource = resource,
            InstrumentationScope = scope
        };

        // Convert log body
        if (logRecord.Body != null)
        {
            ConvertLogRecordBody(logRecord.Body, model);
        }

        return model;
    }

    /// <summary>
    /// Converts the log record body (AnyValue) to string representation
    /// </summary>
    private void ConvertLogRecordBody(AnyValue body, LogRecordModel model)
    {
        switch (body.ValueCase)
        {
            case AnyValue.ValueOneofCase.StringValue:
                model.BodyType = AttributeType.STRING;
                model.BodyValue = _converter.Clip(body.StringValue, _converter.Limits.MaxLogBodyLength, "logs", "body");
                break;

            case AnyValue.ValueOneofCase.BoolValue:
                model.BodyType = AttributeType.BOOL;
                model.BodyValue = body.BoolValue.ToString().ToLower();
                break;

            case AnyValue.ValueOneofCase.IntValue:
                model.BodyType = AttributeType.INT;
                model.BodyValue = body.IntValue.ToString();
                break;

            case AnyValue.ValueOneofCase.DoubleValue:
                model.BodyType = AttributeType.DOUBLE;
                model.BodyValue = body.DoubleValue.ToString("G17");
                break;

            case AnyValue.ValueOneofCase.BytesValue:
                model.BodyType = AttributeType.BYTES;
                model.BodyValue = Convert.ToBase64String(body.BytesValue.ToByteArray());
                ClipStructuredBody(model);
                break;

            case AnyValue.ValueOneofCase.ArrayValue:
                model.BodyType = AttributeType.ARRAY;
                model.BodyValue = ConvertArrayValueToJson(body.ArrayValue);
                ClipStructuredBody(model);
                break;

            case AnyValue.ValueOneofCase.KvlistValue:
                model.BodyType = AttributeType.KVLIST;
                model.BodyValue = ConvertKeyValueListToJson(body.KvlistValue);
                ClipStructuredBody(model);
                break;

            default:
                model.BodyType = AttributeType.STRING;
                model.BodyValue = _converter.Clip(body.ToString(), _converter.Limits.MaxLogBodyLength, "logs", "body");
                break;
        }
    }

    /// <summary>
    /// Converts ArrayValue to JSON string representation
    /// </summary>
    /// <summary>
    /// A bytes, array or map body that is still over <see cref="IngestionLimitsOptions.MaxLogBodyLength"/> once rendered is cut
    /// and becomes a STRING body: cut JSON is not JSON, and the type must not promise a structure the text no longer has.
    /// </summary>
    private void ClipStructuredBody(LogRecordModel model)
    {
        var clipped = _converter.Clip(model.BodyValue, _converter.Limits.MaxLogBodyLength, "logs", "body");
        if (!ReferenceEquals(clipped, model.BodyValue))
        {
            model.BodyValue = clipped;
            model.BodyType = AttributeType.STRING;
        }
    }

    private string ConvertArrayValueToJson(ArrayValue arrayValue)
    {
        try
        {
            var array = _converter.ConvertArrayValue(arrayValue, "logs");
            return System.Text.Json.JsonSerializer.Serialize(array);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert ArrayValue to JSON, falling back to string representation");
            return arrayValue.ToString();
        }
    }

    /// <summary>
    /// Converts KeyValueList to JSON string representation
    /// </summary>
    private string ConvertKeyValueListToJson(KeyValueList kvList)
    {
        try
        {
            var dict = _converter.ConvertKeyValueList(kvList, "logs");
            return System.Text.Json.JsonSerializer.Serialize(dict);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert KeyValueList to JSON, falling back to string representation");
            return kvList.ToString();
        }
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