using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Collector.Services.Helpers;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// gRPC service implementation for OpenTelemetry logs collection.
/// Handles OTLP (OpenTelemetry Protocol) log export requests and stores them using LogRepository.
/// </summary>
public class LogService : OpenTelemetry.Proto.Collector.Logs.V1.LogsService.LogsServiceBase
{
    private readonly ILogWriteRepository _logRepository;
    private readonly ILogger<LogService> _logger;
    private readonly ITenantResolver _tenantResolver;

    public LogService(ILogWriteRepository logRepository, ILogger<LogService> logger, ITenantResolver tenantResolver)
    {
        _logRepository = logRepository ?? throw new ArgumentNullException(nameof(logRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    }

    /// <summary>
    /// Handles the Export gRPC call for log data.
    /// Converts the incoming OTLP log data to Models and stores them using the LogRepository.
    /// </summary>
    /// <param name="request">The ExportLogsServiceRequest containing log data</param>
    /// <param name="context">The gRPC server call context</param>
    /// <returns>ExportLogsServiceResponse indicating success or failure</returns>
    public override async Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        if (request == null)
        {
            _logger.LogError("Received null ExportLogsServiceRequest");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request cannot be null"));
        }
        
        var errorMessage = string.Empty;
        var logRecords = new List<LogRecordModel>();
        var storedLogCount = 0;
        try
        {
            string? keyHash = ApiKeyHelper.GetKeyHash(context);
            var tenantId = await _tenantResolver.ResolveTenantIdAsync(keyHash, context.CancellationToken);
            if (tenantId <= 0)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));

            _logger.LogDebug("Received logs export request with {ResourceLogsCount} resource logs", request.ResourceLogs?.Count ?? 0);

            // Convert protobuf message to Models
            logRecords = ConvertToLogRecordModels(request, tenantId);

            if (!logRecords.Any())
            {
                _logger.LogDebug("No log records found in the request");
                return new ExportLogsServiceResponse
                {
                    PartialSuccess = new ExportLogsPartialSuccess
                    {
                        RejectedLogRecords = 0,
                        ErrorMessage = string.Empty
                    }
                };
            }

            // Store log records using the repository
            var storedIds = await _logRepository.StoreLogRecordsBatchAsync(logRecords, context.CancellationToken);
            storedLogCount = logRecords.Count;
            _logger.LogInformation("Received {LogCount} log records", logRecords.Count);
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Log export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            errorMessage = "Invalid argument in log export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing log export request";
            _logger.LogError(ex, errorMessage);
        }
        
        return new ExportLogsServiceResponse
        {
            PartialSuccess = new ExportLogsPartialSuccess
            {
                RejectedLogRecords = Math.Max(0, logRecords.Count - storedLogCount),
                ErrorMessage = errorMessage
            }
        };
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
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : schemaUrl,
            // Mirrors NormalizeResource's synthetic resource, but carrying the real tenant.
            Attributes = resource == null
                ? new Dictionary<string, object> { { "service.name", "unknown" } }
                : ConvertAttributes(resource.Attributes)
        };
    }

    /// <summary>
    /// Converts OTLP InstrumentationScope to InstrumentationScopeModel
    /// </summary>
    private InstrumentationScopeModel? ConvertInstrumentationScope(OpenTelemetry.Proto.Logs.V1.ScopeLogs? scopeLogs)
    {
        if (scopeLogs?.Scope == null)
            return null;

        return new InstrumentationScopeModel
        {
            Name = scopeLogs.Scope.Name ?? "unknown",
            Version = string.IsNullOrEmpty(scopeLogs.Scope.Version) ? null : scopeLogs.Scope.Version,
            SchemaUrl = string.IsNullOrEmpty(scopeLogs.SchemaUrl) ? null : scopeLogs.SchemaUrl,
            Attributes = ConvertAttributes(scopeLogs.Scope.Attributes)
        };
    }

    /// <summary>
    /// Converts OTLP LogRecord to LogRecordModel
    /// </summary>
    private LogRecordModel ConvertLogRecord(LogRecord logRecord, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var model = new LogRecordModel
        {
            TimeUnixNano = logRecord.TimeUnixNano == 0 ? null : (long)logRecord.TimeUnixNano,
            ObservedTimeUnixNano = logRecord.ObservedTimeUnixNano == 0 ? null : (long)logRecord.ObservedTimeUnixNano,
            SeverityNumber = logRecord.SeverityNumber == 0 ? null : (int)logRecord.SeverityNumber,
            SeverityText = string.IsNullOrEmpty(logRecord.SeverityText) ? null : logRecord.SeverityText,
            EventName = string.IsNullOrEmpty(logRecord.EventName) ? null : logRecord.EventName,
            DroppedAttributesCount = (int)logRecord.DroppedAttributesCount,
            Flags = (int)logRecord.Flags,
            TraceIdHex = ConvertTraceId(logRecord.TraceId),
            SpanIdHex = ConvertSpanId(logRecord.SpanId),
            Attributes = ConvertAttributes(logRecord.Attributes),
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
                model.BodyValue = body.StringValue;
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
                break;

            case AnyValue.ValueOneofCase.ArrayValue:
                model.BodyType = AttributeType.ARRAY;
                model.BodyValue = ConvertArrayValueToJson(body.ArrayValue);
                break;

            case AnyValue.ValueOneofCase.KvlistValue:
                model.BodyType = AttributeType.KVLIST;
                model.BodyValue = ConvertKeyValueListToJson(body.KvlistValue);
                break;

            default:
                model.BodyType = AttributeType.STRING;
                model.BodyValue = body.ToString();
                break;
        }
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
    /// Converts ArrayValue to JSON string representation
    /// </summary>
    private string ConvertArrayValueToJson(ArrayValue arrayValue)
    {
        try
        {
            var array = ConvertArrayValue(arrayValue);
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
            var dict = ConvertKeyValueList(kvList);
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