using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.IntegrationTests.Seeding;

/// <summary>
/// Deterministic builders for <see cref="ITelemetryBulkWriter"/> input: fixed timestamps,
/// services, attributes, span trees and metric streams, so a test's expectations can be computed
/// by hand instead of re-deriving them from whatever a run happened to generate. Deliberately not
/// <c>Keryhe.Telemetry.TestDataGenerator</c> — that emits random, time-dependent OTLP traffic
/// through the real gRPC path, which is the wrong shape for a repeatable characterization test
/// (see the plan's Phase 0 section).
///
/// Every builder writes through the same <see cref="Keryhe.Telemetry.Core.ITelemetryBulkWriter"/>
/// the ingestion worker uses, so hashing/dedup/upsert logic runs exactly as it does in production.
/// </summary>
public static class SeededDataBuilder
{
    public static long ToUnixNano(DateTime timestampUtc) =>
        (timestampUtc.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100;

    public static ResourceModel Resource(long tenantId, string serviceName, string? instanceId = null, Dictionary<string, object>? extra = null)
    {
        var attributes = new Dictionary<string, object> { ["service.name"] = serviceName };
        if (instanceId != null)
            attributes["service.instance.id"] = instanceId;
        if (extra != null)
            foreach (var (key, value) in extra)
                attributes[key] = value;

        return new ResourceModel { TenantId = tenantId, Attributes = attributes };
    }

    public static InstrumentationScopeModel Scope(string name = "phase0.instrumentation", string version = "1.0.0") =>
        new() { Name = name, Version = version };

    // =========================================================================
    // LOGS
    // =========================================================================

    private static readonly string[] Severities = ["DEBUG", "INFO", "WARN", "ERROR"];
    private static readonly int[] SeverityNumbers = [5, 9, 13, 17];

    /// <summary>~600 logs across three services and four severities, one every second starting at <paramref name="start"/>.</summary>
    public static List<LogRecordModel> BasicLogWindow(long tenantId, DateTime start, int count = 600)
    {
        var services = new[] { "checkout-api", "payments-worker", "inventory-api" };
        var scope = Scope();
        var records = new List<LogRecordModel>(count);
        for (var i = 0; i < count; i++)
        {
            var service = services[i % services.Length];
            var severityIndex = i % Severities.Length;
            var timestamp = start.AddSeconds(i);
            records.Add(new LogRecordModel
            {
                TimeUnixNano = ToUnixNano(timestamp),
                ObservedTimeUnixNano = ToUnixNano(timestamp),
                SeverityNumber = SeverityNumbers[severityIndex],
                SeverityText = Severities[severityIndex],
                BodyType = AttributeType.STRING,
                BodyValue = $"phase0 log #{i} from {service}",
                Attributes = new Dictionary<string, object>
                {
                    ["http.status_code"] = severityIndex == 3 ? 500 : 200,
                    ["retry"] = severityIndex == 3,
                    ["k8s.pod.name"] = $"{service}-pod-{i % 5}"
                },
                Resource = Resource(tenantId, service),
                InstrumentationScope = scope
            });
        }
        return records;
    }

    /// <summary>A batch whose event time falls well inside <paramref name="pinnedWindowStart"/>/<paramref name="pinnedWindowEnd"/> but is flushed "now" — for pinning/late-arrival tests.</summary>
    public static List<LogRecordModel> LateArrivingLogs(long tenantId, DateTime pinnedWindowStart, DateTime pinnedWindowEnd, int count = 20)
    {
        var midpoint = pinnedWindowStart + (pinnedWindowEnd - pinnedWindowStart) / 2;
        var scope = Scope();
        var records = new List<LogRecordModel>(count);
        for (var i = 0; i < count; i++)
        {
            var timestamp = midpoint.AddSeconds(i);
            records.Add(new LogRecordModel
            {
                TimeUnixNano = ToUnixNano(timestamp),
                ObservedTimeUnixNano = ToUnixNano(timestamp),
                SeverityNumber = 9,
                SeverityText = "INFO",
                BodyType = AttributeType.STRING,
                BodyValue = $"late-arriving log #{i}",
                Attributes = new Dictionary<string, object>(),
                Resource = Resource(tenantId, "late-arrivals-svc"),
                InstrumentationScope = scope
            });
        }
        return records;
    }

    // =========================================================================
    // TRACES
    // =========================================================================

    private static string HexId(int length, long seed)
    {
        var bytes = new byte[length / 2];
        var rnd = new Random((int)(seed & 0x7FFFFFFF));
        rnd.NextBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>~200 three-span traces (root + two children) across three services, several with an error status.</summary>
    public static List<SpanModel> BasicTraceWindow(long tenantId, DateTime start, int traceCount = 200)
    {
        var services = new[] { "checkout-api", "payments-worker", "inventory-api" };
        var scope = Scope();
        var spans = new List<SpanModel>(traceCount * 3);
        for (var t = 0; t < traceCount; t++)
        {
            var traceId = HexId(32, t * 3 + 1);
            var rootId = HexId(16, t * 3 + 2);
            var childId = HexId(16, t * 3 + 3);
            var service = services[t % services.Length];
            var isError = t % 17 == 0;
            var traceStart = start.AddSeconds(t);

            spans.Add(new SpanModel
            {
                TraceIdHex = traceId,
                SpanIdHex = rootId,
                ParentSpanIdHex = null,
                Name = "POST /checkout",
                Kind = SpanKind.SERVER,
                StartTimeUnixNano = ToUnixNano(traceStart),
                EndTimeUnixNano = ToUnixNano(traceStart.AddMilliseconds(120 + t % 50)),
                StatusCode = isError ? SpanStatusCode.ERROR : SpanStatusCode.OK,
                StatusMessage = isError ? "internal error" : null,
                Attributes = new Dictionary<string, object>
                {
                    ["http.status_code"] = isError ? 500 : 200,
                    ["http.route"] = "/checkout"
                },
                Resource = Resource(tenantId, service),
                InstrumentationScope = scope
            });

            spans.Add(new SpanModel
            {
                TraceIdHex = traceId,
                SpanIdHex = childId,
                ParentSpanIdHex = rootId,
                Name = "SELECT inventory",
                Kind = SpanKind.CLIENT,
                StartTimeUnixNano = ToUnixNano(traceStart.AddMilliseconds(10)),
                EndTimeUnixNano = ToUnixNano(traceStart.AddMilliseconds(60)),
                StatusCode = SpanStatusCode.OK,
                Attributes = new Dictionary<string, object> { ["db.system"] = "postgresql" },
                Resource = Resource(tenantId, service),
                InstrumentationScope = scope
            });
        }
        return spans;
    }

    /// <summary>A trace whose earliest span's parent id matches no span anywhere — the anchor for the <c>orphan_roots</c>/decision-41 behavior.</summary>
    public static List<SpanModel> OrphanTrace(long tenantId, DateTime start)
    {
        var traceId = HexId(32, 999_001);
        var missingParent = HexId(16, 999_002);
        var fragmentRoot = HexId(16, 999_003);
        var child = HexId(16, 999_004);
        var scope = Scope();
        var resource = Resource(tenantId, "gateway-forwarded-svc");

        return
        [
            new SpanModel
            {
                TraceIdHex = traceId,
                SpanIdHex = fragmentRoot,
                ParentSpanIdHex = missingParent,
                Name = "GET /orders/{id}",
                Kind = SpanKind.SERVER,
                StartTimeUnixNano = ToUnixNano(start),
                EndTimeUnixNano = ToUnixNano(start.AddMilliseconds(80)),
                StatusCode = SpanStatusCode.OK,
                Resource = resource,
                InstrumentationScope = scope
            },
            new SpanModel
            {
                TraceIdHex = traceId,
                SpanIdHex = child,
                ParentSpanIdHex = fragmentRoot,
                Name = "SELECT orders",
                Kind = SpanKind.CLIENT,
                StartTimeUnixNano = ToUnixNano(start.AddMilliseconds(5)),
                EndTimeUnixNano = ToUnixNano(start.AddMilliseconds(40)),
                StatusCode = SpanStatusCode.OK,
                Resource = resource,
                InstrumentationScope = scope
            }
        ];
    }

    /// <summary>Sends the same span batch twice, simulating gRPC re-delivery before a ClickHouse <c>ReplacingMergeTree</c> merge.</summary>
    public static List<SpanModel> RedeliveredSpanBatch(long tenantId, DateTime start) => BasicTraceWindow(tenantId, start, traceCount: 5);

    // =========================================================================
    // METRICS
    // =========================================================================

    /// <summary>Several resources (distinct <c>service.instance.id</c>) sharing one <c>service.name</c>, each emitting a gauge — for stream-merging tests.</summary>
    public static List<MetricModel> MultiInstanceGauge(long tenantId, DateTime start, string serviceName = "shared-svc", int instanceCount = 4, int pointsPerInstance = 30)
    {
        var scope = Scope();
        var metrics = new List<MetricModel>();
        for (var instance = 0; instance < instanceCount; instance++)
        {
            var resource = Resource(tenantId, serviceName, instanceId: $"pod-{instance}");
            var points = new List<GaugeDataPointModel>(pointsPerInstance);
            for (var i = 0; i < pointsPerInstance; i++)
            {
                points.Add(new GaugeDataPointModel
                {
                    TimeUnixNano = ToUnixNano(start.AddSeconds(i * 10)),
                    ValueDouble = 50 + instance * 5 + i % 7,
                    Attributes = new Dictionary<string, object>
                    {
                        ["k8s.pod.name"] = $"{serviceName}-{instance}",
                        ["http.status_code"] = 200,
                        ["healthy"] = true
                    }
                });
            }
            metrics.Add(new MetricModel
            {
                Name = "phase0.cpu.utilization",
                Type = MetricType.GAUGE,
                Unit = "1",
                GaugeDataPoints = points,
                Resource = resource,
                InstrumentationScope = scope
            });
        }
        return metrics;
    }

    /// <summary>
    /// Two cumulative-sum flushes for the same stream where the second value is lower than the
    /// first (a forced counter reset) — feed the two lists through separate
    /// <c>FlushMetricsAsync</c> calls to reproduce a reset arriving mid-series.
    /// </summary>
    public static (MetricModel first, MetricModel second) CumulativeCounterReset(long tenantId, DateTime start, string serviceName = "requests-svc")
    {
        var scope = Scope();
        var resource = Resource(tenantId, serviceName);
        var streamStart = ToUnixNano(start);

        var first = new MetricModel
        {
            Name = "phase0.requests.total",
            Type = MetricType.SUM,
            Unit = "1",
            SumDataPoints =
            [
                new SumDataPointModel
                {
                    StartTimeUnixNano = streamStart,
                    TimeUnixNano = ToUnixNano(start.AddSeconds(10)),
                    ValueInt = 1000,
                    AggregationTemporality = AggregationTemporality.CUMULATIVE,
                    IsMonotonic = true,
                    Attributes = new Dictionary<string, object> { ["route"] = "/checkout" }
                }
            ],
            Resource = resource,
            InstrumentationScope = scope
        };

        // Process restart: StartTimeUnixNano advances and the counter drops below its prior value.
        var second = new MetricModel
        {
            Name = "phase0.requests.total",
            Type = MetricType.SUM,
            Unit = "1",
            SumDataPoints =
            [
                new SumDataPointModel
                {
                    StartTimeUnixNano = ToUnixNano(start.AddSeconds(20)),
                    TimeUnixNano = ToUnixNano(start.AddSeconds(30)),
                    ValueInt = 50,
                    AggregationTemporality = AggregationTemporality.CUMULATIVE,
                    IsMonotonic = true,
                    Attributes = new Dictionary<string, object> { ["route"] = "/checkout" }
                }
            ],
            Resource = resource,
            InstrumentationScope = scope
        };

        return (first, second);
    }

    /// <summary>501 distinct metric instances (names) — for the metrics-list ">500 instances" check. Cheap at any scale, per the plan's Load section: one data point each.</summary>
    public static List<MetricModel> MoreThan500Instances(long tenantId, DateTime start, int count = 501)
    {
        var scope = Scope();
        var resource = Resource(tenantId, "catalog-svc");
        var metrics = new List<MetricModel>(count);
        for (var i = 0; i < count; i++)
        {
            metrics.Add(new MetricModel
            {
                Name = $"phase0.instance.metric.{i}",
                Type = MetricType.GAUGE,
                Unit = "1",
                GaugeDataPoints =
                [
                    new GaugeDataPointModel { TimeUnixNano = ToUnixNano(start.AddSeconds(i)), ValueDouble = i }
                ],
                Resource = resource,
                InstrumentationScope = scope
            });
        }
        return metrics;
    }
}
