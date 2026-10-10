using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Collector.Authentication;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// Converts OTLP metric exports to models and enqueues them (see <see cref="IngestAsync"/>). The gRPC service and the HTTP endpoint are thin
/// adapters over it, so both transports share conversion, limits, queue behaviour and counters.
/// </summary>
public sealed class OtlpMetricIngestor
{
    private readonly IMetricWriteRepository _metricRepository;
    private readonly ILogger<OtlpMetricIngestor> _logger;
    private readonly TelemetryIngestionChannel _channel;
    private readonly OtlpAttributeConverter _converter;
    private readonly IngestionMetrics _metrics;

    public OtlpMetricIngestor(IMetricWriteRepository metricRepository, ILogger<OtlpMetricIngestor> logger, TelemetryIngestionChannel channel, IngestionMetrics metrics, OtlpAttributeConverter converter)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _metricRepository = metricRepository ?? throw new ArgumentNullException(nameof(metricRepository));
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
    public async Task<IngestResult> IngestAsync(ExportMetricsServiceRequest request, long tenantId, string protocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errorMessage = string.Empty;
        var requestCount = 0;
        var metrics = new List<MetricModel>();
        var totalDataPointCount = 0;
        var storedDataPointCount = 0;
        try
        {
            // A full queue refuses the export now, before it is converted (the repository's bounded wait applies after).
            requestCount = request.ResourceMetrics.Sum(r => r.ScopeMetrics.Sum(s => s.Metrics.Count));
            _channel.ThrowIfSaturated("metrics", _channel.MetricGate);

            _logger.LogDebug("Received metrics export request with {ResourceMetricsCount} resource metrics", request.ResourceMetrics?.Count ?? 0);

            metrics = ConvertToMetricModels(request, tenantId);
            if (metrics.Count == 0)
            {
                _logger.LogDebug("No metrics found in the request");
                return IngestResult.Ok;
            }

            // Counted before the store call so that a failure to enqueue reports the data points it lost.
            totalDataPointCount = CalculateTotalDataPoints(metrics);
            await _metricRepository.StoreMetricsBatchAsync(metrics, cancellationToken, request.CalculateSize());
            storedDataPointCount = totalDataPointCount;
            _metrics.RecordAccepted("metrics", tenantId, metrics.Count, protocol);
        }
        catch (IngestionRejectedException rejection)
        {
            _metrics.RecordRefused("metrics", tenantId, rejection.Reason, requestCount, protocol);
            _logger.LogDebug("Refused metrics export for tenant {TenantId}: {Reason}", tenantId, rejection.Reason);
            throw;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            // The ingestion worker is draining for host shutdown and has closed the channel. A retryable refusal, not a
            // partial-success rejection (which OTLP clients never retry), so the client resends -- behind a load balancer, to another instance.
            _metrics.RecordRefused("metrics", tenantId, RefusalReasons.ShuttingDown, requestCount, protocol);
            _logger.LogWarning("Rejected metrics export: collector is shutting down");
            throw new IngestionRejectedException("metrics", RefusalReasons.ShuttingDown, TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Metric export operation was cancelled";
            _logger.LogWarning(errorMessage);
        }
        catch (ArgumentException ex)
        {
            _metrics.RecordRefused("metrics", tenantId, RefusalReasons.Invalid, requestCount, protocol);
            errorMessage = "Invalid argument in metric export request";
            _logger.LogError(ex, errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = "Error processing metric export request";
            _logger.LogError(ex, errorMessage);
        }

        return new IngestResult(Math.Max(0, totalDataPointCount - storedDataPointCount), errorMessage);
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
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : _converter.ClipColumn(schemaUrl, ColumnLimits.SchemaUrl, "metrics", "schema_url"),
            // Mirrors NormalizeResource's synthetic resource, but carrying the real tenant. Truncated before the resource is hashed.
            Attributes = resource == null
                ? new Dictionary<string, object> { { "service.name", "unknown" } }
                : _converter.ClipServiceName(_converter.ConvertAttributes(resource.Attributes, "metrics"), "metrics")
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
            Name = _converter.ClipColumn(scope.Name, ColumnLimits.ScopeName, "metrics", "scope_name") ?? "unknown",
            Version = string.IsNullOrEmpty(scope.Version) ? null : _converter.ClipColumn(scope.Version, ColumnLimits.ScopeVersion, "metrics", "scope_version"),
            SchemaUrl = string.IsNullOrEmpty(schemaUrl) ? null : _converter.ClipColumn(schemaUrl, ColumnLimits.SchemaUrl, "metrics", "schema_url"),
            Attributes = _converter.ConvertAttributes(scope.Attributes, "metrics")
        };
    }

    /// <summary>
    /// Converts OTLP Metric to MetricModel
    /// </summary>
    private MetricModel ConvertMetric(Metric metric, ResourceModel? resource, InstrumentationScopeModel? scope)
    {
        var metricModel = new MetricModel
        {
            Name = _converter.ClipColumn(metric.Name, ColumnLimits.MetricName, "metrics", "metric_name") ?? "unknown",
            Description = string.IsNullOrEmpty(metric.Description) ? null : metric.Description,
            Unit = string.IsNullOrEmpty(metric.Unit) ? null : _converter.ClipColumn(metric.Unit, ColumnLimits.MetricUnit, "metrics", "metric_unit"),
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
            Attributes = _converter.ConvertAttributes(dp.Attributes, "metrics"),
            Exemplars = ConvertExemplars(dp.Exemplars)
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
            Attributes = _converter.ConvertAttributes(dp.Attributes, "metrics"),
            Exemplars = ConvertExemplars(dp.Exemplars)
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
            Attributes = _converter.ConvertAttributes(dp.Attributes, "metrics"),
            Exemplars = ConvertExemplars(dp.Exemplars)
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
            Attributes = _converter.ConvertAttributes(dp.Attributes, "metrics"),
            Exemplars = ConvertExemplars(dp.Exemplars)
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
            Attributes = _converter.ConvertAttributes(dp.Attributes, "metrics")
        }).ToList();
    }

    /// <summary>
    /// Converts a data point's OTLP exemplars. Returns null rather than an empty list when the
    /// point carries none -- which is the overwhelmingly common case -- so the bulk writers store
    /// SQL NULL in exemplars_json instead of the string "[]" on every row, and so no list is
    /// allocated for a point that has nothing to put in it.
    ///
    /// Every exemplar is kept. OTLP declares `repeated Exemplar exemplars` on NumberDataPoint,
    /// HistogramDataPoint and ExponentialHistogramDataPoint alike (Summary has none), and a
    /// histogram's reservoir typically holds one per bucket, so truncating to the first -- as the
    /// gauge/sum models did before 2.9.0 -- threw away most of what an SDK sent.
    /// </summary>
    private List<ExemplarModel>? ConvertExemplars(Google.Protobuf.Collections.RepeatedField<Exemplar>? exemplars)
    {
        if (exemplars == null || exemplars.Count == 0)
            return null;

        var result = new List<ExemplarModel>(exemplars.Count);
        foreach (var e in exemplars)
        {
            // Exemplars are stored as JSON on the data point row, so an empty attribute bag would
            // be serialized literally as {} on every one of them; null drops the key entirely.
            var filtered = _converter.ConvertAttributes(e.FilteredAttributes, "metrics");
            result.Add(new ExemplarModel
            {
                FilteredAttributes = filtered.Count == 0 ? null : filtered,
                TimeUnixNano = (long)e.TimeUnixNano,
                ValueDouble = e.ValueCase == Exemplar.ValueOneofCase.AsDouble ? e.AsDouble : null,
                ValueInt = e.ValueCase == Exemplar.ValueOneofCase.AsInt ? e.AsInt : null,
                SpanIdHex = ConvertSpanId(e.SpanId),
                TraceIdHex = ConvertTraceId(e.TraceId)
            });
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