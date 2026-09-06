using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Collector.Services.Helpers;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// gRPC service implementation for OpenTelemetry metrics collection.
/// Handles OTLP (OpenTelemetry Protocol) metric export requests and stores them using MetricRepository.
/// </summary>
public class MetricService : MetricsService.MetricsServiceBase
{
    private readonly IMetricWriteRepository _metricRepository;
    private readonly ILogger<MetricService> _logger;
    private readonly ITenantResolver _tenantResolver;

    public MetricService(IMetricWriteRepository metricRepository, ILogger<MetricService> logger, ITenantResolver tenantResolver)
    {
        _metricRepository = metricRepository ?? throw new ArgumentNullException(nameof(metricRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    }

    /// <summary>
    /// Handles the Export gRPC call for metric data.
    /// Converts the incoming OTLP metric data to Models and stores them using the MetricRepository.
    /// </summary>
    /// <param name="request">The ExportMetricsServiceRequest containing metric data</param>
    /// <param name="context">The gRPC server call context</param>
    /// <returns>ExportMetricsServiceResponse indicating success or failure</returns>
    public override async Task<ExportMetricsServiceResponse> Export(
        ExportMetricsServiceRequest request, 
        ServerCallContext context)
    {
        if (request == null)
        {
            _logger.LogError("Received null ExportMetricsServiceRequest");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request cannot be null"));
        }
        
        var errorMessage = string.Empty;
        var metrics = new List<MetricModel>();
        var totalDataPointCount = 0;
        var storedDataPointCount = 0;
        try
        {
            string? keyHash = ApiKeyHelper.GetKeyHash(context);
            var tenantId = await _tenantResolver.ResolveTenantIdAsync(keyHash, context.CancellationToken);
            if (tenantId <= 0)
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));

            _logger.LogDebug("Received metrics export request with {ResourceMetricsCount} resource metrics", 
                request.ResourceMetrics?.Count ?? 0);

            // Convert protobuf message to Models
            metrics = ConvertToMetricModels(request, tenantId);

            if (!metrics.Any())
            {
                _logger.LogDebug("No metrics found in the request");
                return new ExportMetricsServiceResponse
                {
                    PartialSuccess = new ExportMetricsPartialSuccess
                    {
                        RejectedDataPoints = 0,
                        ErrorMessage = string.Empty
                    }
                };
            }

            // Store metrics using the repository
            var storedMetricIds = await _metricRepository.StoreMetricsBatchAsync(metrics, context.CancellationToken);
            totalDataPointCount = CalculateTotalDataPoints(metrics);
            storedDataPointCount = totalDataPointCount;
            _logger.LogInformation("Received {MetricCount} metrics with {TotalDataPointCount} data points", metrics.Count, totalDataPointCount);
            
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Metric export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            errorMessage = "Invalid argument in metric export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing metric export request";
            _logger.LogError(ex, errorMessage);
        }
        
        return new ExportMetricsServiceResponse
        {
            PartialSuccess = new ExportMetricsPartialSuccess
            {
                RejectedDataPoints = Math.Max(0, totalDataPointCount - storedDataPointCount),
                ErrorMessage = errorMessage
            }
        };
    }

    /// <summary>
    /// Converts OTLP ExportMetricsServiceRequest to a list of MetricModel objects
    /// </summary>
    private List<MetricModel> ConvertToMetricModels(ExportMetricsServiceRequest request, long tenantId)
    {
        var metricModels = new List<MetricModel>();

        foreach (var resourceMetrics in request.ResourceMetrics)
        {
            // Convert resource information
            var resourceModel = ConvertResource(resourceMetrics.SchemaUrl, resourceMetrics.Resource, tenantId);

            foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
            {
                // Convert instrumentation scope information
                var instrumentationScopeModel = ConvertInstrumentationScope(scopeMetrics.SchemaUrl, scopeMetrics.Scope);

                foreach (var metric in scopeMetrics.Metrics)
                {
                    try
                    {
                        var metricModel = ConvertMetric(metric, resourceModel, instrumentationScopeModel);
                        metricModels.Add(metricModel);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to convert metric '{MetricName}', skipping", metric.Name);
                    }
                }
            }
        }

        return metricModels;
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
    /// Converts OTLP Metric to MetricModel
    /// </summary>
    private MetricModel ConvertMetric(Metric metric, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var metricModel = new MetricModel
        {
            Name = metric.Name ?? "unknown",
            Description = string.IsNullOrEmpty(metric.Description) ? null : metric.Description,
            Unit = string.IsNullOrEmpty(metric.Unit) ? null : metric.Unit,
            Resource = resource,
            InstrumentationScope = scope
        };

        // Convert based on metric data type
        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                metricModel.Type = MetricType.GAUGE;
                metricModel.GaugeDataPoints = ConvertGaugeDataPoints(metric.Gauge);
                break;

            case Metric.DataOneofCase.Sum:
                metricModel.Type = MetricType.SUM;
                metricModel.SumDataPoints = ConvertSumDataPoints(metric.Sum);
                break;

            case Metric.DataOneofCase.Histogram:
                metricModel.Type = MetricType.HISTOGRAM;
                metricModel.HistogramDataPoints = ConvertHistogramDataPoints(metric.Histogram);
                break;

            case Metric.DataOneofCase.ExponentialHistogram:
                metricModel.Type = MetricType.EXPONENTIAL_HISTOGRAM;
                metricModel.ExponentialHistogramDataPoints = ConvertExponentialHistogramDataPoints(metric.ExponentialHistogram);
                break;

            case Metric.DataOneofCase.Summary:
                metricModel.Type = MetricType.SUMMARY;
                metricModel.SummaryDataPoints = ConvertSummaryDataPoints(metric.Summary);
                break;

            default:
                throw new ArgumentException($"Unknown or unsupported metric data type: {metric.DataCase}");
        }

        return metricModel;
    }

    /// <summary>
    /// Converts OTLP Gauge data points to GaugeDataPointModel list
    /// </summary>
    private List<GaugeDataPointModel> ConvertGaugeDataPoints(Gauge gauge)
    {
        return gauge.DataPoints.Select(dp => new GaugeDataPointModel
        {
            StartTimeUnixNano = dp.StartTimeUnixNano == 0 ? null : (long)dp.StartTimeUnixNano,
            TimeUnixNano = (long)dp.TimeUnixNano,
            ValueDouble = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble ? dp.AsDouble : null,
            ValueInt = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsInt ? dp.AsInt : null,
            Flags = (int)dp.Flags,
            Attributes = ConvertAttributes(dp.Attributes),
            Exemplar = dp.Exemplars.FirstOrDefault() != null ? ConvertExemplar(dp.Exemplars.First()) : null
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP Sum data points to SumDataPointModel list
    /// </summary>
    private List<SumDataPointModel> ConvertSumDataPoints(Sum sum)
    {
        return sum.DataPoints.Select(dp => new SumDataPointModel
        {
            StartTimeUnixNano = dp.StartTimeUnixNano == 0 ? null : (long)dp.StartTimeUnixNano,
            TimeUnixNano = (long)dp.TimeUnixNano,
            ValueDouble = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble ? dp.AsDouble : null,
            ValueInt = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsInt ? dp.AsInt : null,
            AggregationTemporality = ConvertAggregationTemporality(sum.AggregationTemporality),
            IsMonotonic = sum.IsMonotonic,
            Flags = (int)dp.Flags,
            Attributes = ConvertAttributes(dp.Attributes),
            Exemplar = dp.Exemplars.FirstOrDefault() != null ? ConvertExemplar(dp.Exemplars.First()) : null
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP Histogram data points to HistogramDataPointModel list
    /// </summary>
    private List<HistogramDataPointModel> ConvertHistogramDataPoints(Histogram histogram)
    {
        return histogram.DataPoints.Select(dp => new HistogramDataPointModel
        {
            StartTimeUnixNano = dp.StartTimeUnixNano == 0 ? null : (long)dp.StartTimeUnixNano,
            TimeUnixNano = (long)dp.TimeUnixNano,
            Count = (long)dp.Count,
            Sum = dp.HasSum ? dp.Sum : null,
            BucketCounts = dp.BucketCounts.Select(c => (long)c).ToArray(),
            ExplicitBounds = dp.ExplicitBounds.ToArray(),
            AggregationTemporality = ConvertAggregationTemporality(histogram.AggregationTemporality),
            Flags = (int)dp.Flags,
            Min = dp.HasMin ? dp.Min : null,
            Max = dp.HasMax ? dp.Max : null,
            Attributes = ConvertAttributes(dp.Attributes),
            Exemplars = dp.Exemplars.Select(ConvertExemplar).ToList()
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP ExponentialHistogram data points to ExponentialHistogramDataPointModel list
    /// </summary>
    private List<ExponentialHistogramDataPointModel> ConvertExponentialHistogramDataPoints(ExponentialHistogram expHistogram)
    {
        return expHistogram.DataPoints.Select(dp => new ExponentialHistogramDataPointModel
        {
            StartTimeUnixNano = dp.StartTimeUnixNano == 0 ? null : (long)dp.StartTimeUnixNano,
            TimeUnixNano = (long)dp.TimeUnixNano,
            Count = (long)dp.Count,
            Sum = dp.HasSum ? dp.Sum : null,
            Scale = dp.Scale,
            ZeroCount = (long)dp.ZeroCount,
            PositiveOffset = dp.Positive?.Offset,
            PositiveBucketCounts = dp.Positive?.BucketCounts?.Select(c => (long)c).ToArray(),
            NegativeOffset = dp.Negative?.Offset,
            NegativeBucketCounts = dp.Negative?.BucketCounts?.Select(c => (long)c).ToArray(),
            AggregationTemporality = ConvertAggregationTemporality(expHistogram.AggregationTemporality),
            Flags = (int)dp.Flags,
            Min = dp.HasMin ? dp.Min : null,
            Max = dp.HasMax ? dp.Max : null,
            Attributes = ConvertAttributes(dp.Attributes),
            Exemplars = dp.Exemplars.Select(ConvertExemplar).ToList()
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP Summary data points to SummaryDataPointModel list
    /// </summary>
    private List<SummaryDataPointModel> ConvertSummaryDataPoints(Summary summary)
    {
        return summary.DataPoints.Select(dp => new SummaryDataPointModel
        {
            StartTimeUnixNano = dp.StartTimeUnixNano == 0 ? null : (long)dp.StartTimeUnixNano,
            TimeUnixNano = (long)dp.TimeUnixNano,
            Count = (long)dp.Count,
            Sum = dp.Sum,
            QuantileValues = dp.QuantileValues.Select(qv => new QuantileValueModel
            {
                Quantile = qv.Quantile,
                Value = qv.Value
            }).ToList(),
            Flags = (int)dp.Flags,
            Attributes = ConvertAttributes(dp.Attributes)
        }).ToList();
    }

    /// <summary>
    /// Converts OTLP Exemplar to ExemplarModel
    /// </summary>
    private ExemplarModel ConvertExemplar(Exemplar exemplar)
    {
        return new ExemplarModel
        {
            FilteredAttributes = ConvertAttributes(exemplar.FilteredAttributes),
            TimeUnixNano = (long)exemplar.TimeUnixNano,
            ValueDouble = exemplar.ValueCase == Exemplar.ValueOneofCase.AsDouble ? exemplar.AsDouble : null,
            ValueInt = exemplar.ValueCase == Exemplar.ValueOneofCase.AsInt ? exemplar.AsInt : null,
            SpanIdHex = ConvertSpanId(exemplar.SpanId),
            TraceIdHex = ConvertTraceId(exemplar.TraceId)
        };
    }

    /// <summary>
    /// Converts OTLP AggregationTemporality to local AggregationTemporality enum
    /// </summary>
    private Keryhe.Telemetry.Core.Models.AggregationTemporality ConvertAggregationTemporality(OpenTelemetry.Proto.Metrics.V1.AggregationTemporality otlpTemporality)
    {
        return otlpTemporality switch
        {
            OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Delta => Keryhe.Telemetry.Core.Models.AggregationTemporality.DELTA,
            OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative => Keryhe.Telemetry.Core.Models.AggregationTemporality.CUMULATIVE,
            _ => Keryhe.Telemetry.Core.Models.AggregationTemporality.UNSPECIFIED
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

    /// <summary>
    /// Calculates the total number of data points across all metrics
    /// </summary>
    private int CalculateTotalDataPoints(IEnumerable<MetricModel> metrics)
    {
        var totalDataPoints = 0;

        foreach (var metric in metrics)
        {
            totalDataPoints += metric.Type switch
            {
                MetricType.GAUGE => metric.GaugeDataPoints?.Count ?? 0,
                MetricType.SUM => metric.SumDataPoints?.Count ?? 0,
                MetricType.HISTOGRAM => metric.HistogramDataPoints?.Count ?? 0,
                MetricType.EXPONENTIAL_HISTOGRAM => metric.ExponentialHistogramDataPoints?.Count ?? 0,
                MetricType.SUMMARY => metric.SummaryDataPoints?.Count ?? 0,
                _ => 0
            };
        }

        return totalDataPoints;
    }
}