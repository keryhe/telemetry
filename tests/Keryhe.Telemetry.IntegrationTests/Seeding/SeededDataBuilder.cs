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

    /// <summary>
    /// ~200 three-span traces (root + two children) across three services, several with an error
    /// status. <paramref name="seedOffset"/> shifts the deterministic trace/span id sequence so a
    /// second call (e.g. a "late-arriving" batch inserted after some baseline traces) produces a
    /// disjoint set of trace ids rather than colliding with the first.
    /// </summary>
    public static List<SpanModel> BasicTraceWindow(long tenantId, DateTime start, int traceCount = 200, int seedOffset = 0)
    {
        var services = new[] { "checkout-api", "payments-worker", "inventory-api" };
        var scope = Scope();
        var spans = new List<SpanModel>(traceCount * 3);
        for (var t = 0; t < traceCount; t++)
        {
            var traceId = HexId(32, seedOffset + t * 3 + 1);
            var rootId = HexId(16, seedOffset + t * 3 + 2);
            var childId = HexId(16, seedOffset + t * 3 + 3);
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

    /// <summary>A trace whose earliest span's parent id matches no span anywhere (the real root never arrived). It anchors on its earliest span like any other trace.</summary>
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

    /// <summary>A span batch to send twice, simulating gRPC re-delivery: schema 3.0.0 stores both copies.</summary>
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

    // =========================================================================
    // METRICS — Phase 4 (list-pages-server-side plan): database-side bucketed aggregation
    // =========================================================================

    /// <summary>One delta-temporality sum stream, one point per second — for SQL SUM-per-bucket checks.</summary>
    public static MetricModel SumDeltaSeries(long tenantId, DateTime start, string serviceName = "orders-svc", int points = 20, long perPointValue = 3)
    {
        var resource = Resource(tenantId, serviceName);
        var dps = new List<SumDataPointModel>(points);
        for (var i = 0; i < points; i++)
        {
            dps.Add(new SumDataPointModel
            {
                TimeUnixNano = ToUnixNano(start.AddSeconds(i)),
                ValueInt = perPointValue,
                AggregationTemporality = AggregationTemporality.DELTA,
                IsMonotonic = true,
                Attributes = new Dictionary<string, object> { ["route"] = "/orders" }
            });
        }
        return new MetricModel
        {
            Name = "phase4.orders.delta",
            Type = MetricType.SUM,
            Unit = "1",
            SumDataPoints = dps,
            Resource = resource,
            InstrumentationScope = Scope()
        };
    }

    /// <summary>
    /// One cumulative monotonic counter stream that started at <paramref name="start"/>, reporting
    /// <paramref name="values"/> every <paramref name="intervalSeconds"/> seconds (first report one
    /// interval after start) — the export cadence of a real SDK, for rate/elapsed-time checks.
    /// </summary>
    public static MetricModel CumulativeCounterSeries(long tenantId, DateTime start, long[] values, int intervalSeconds = 15, string serviceName = "rate-svc")
    {
        var streamStart = ToUnixNano(start);
        return new MetricModel
        {
            Name = "correctness.requests.cumulative",
            Type = MetricType.SUM,
            Unit = "1",
            SumDataPoints = values.Select((v, i) => new SumDataPointModel
            {
                StartTimeUnixNano = streamStart,
                TimeUnixNano = ToUnixNano(start.AddSeconds((i + 1) * intervalSeconds)),
                ValueInt = v,
                AggregationTemporality = AggregationTemporality.CUMULATIVE,
                IsMonotonic = true,
                Attributes = new Dictionary<string, object> { ["route"] = "/rate" }
            }).ToList(),
            Resource = Resource(tenantId, serviceName),
            InstrumentationScope = Scope()
        };
    }

    /// <summary>
    /// One cumulative explicit-bounds histogram stream that started at <paramref name="start"/>:
    /// (count, sum) pairs reported every <paramref name="intervalSeconds"/> seconds, every
    /// observation landing in the first bucket — for the cumulative-sum-is-differenced check.
    /// </summary>
    public static MetricModel CumulativeHistogramSeries(long tenantId, DateTime start, (long Count, double Sum)[] points, int intervalSeconds = 10, string serviceName = "latency-cumulative-svc")
    {
        var streamStart = ToUnixNano(start);
        return new MetricModel
        {
            Name = "correctness.latency.cumulative",
            Type = MetricType.HISTOGRAM,
            Unit = "ms",
            HistogramDataPoints = points.Select((p, i) => new HistogramDataPointModel
            {
                StartTimeUnixNano = streamStart,
                TimeUnixNano = ToUnixNano(start.AddSeconds((i + 1) * intervalSeconds)),
                Count = p.Count,
                Sum = p.Sum,
                Min = 1,
                Max = 400,
                ExplicitBounds = [500d],
                BucketCounts = [p.Count, 0],
                AggregationTemporality = AggregationTemporality.CUMULATIVE,
                Attributes = new Dictionary<string, object> { ["route"] = "/checkout" }
            }).ToList(),
            Resource = Resource(tenantId, serviceName),
            InstrumentationScope = Scope()
        };
    }

    /// <summary>
    /// One cumulative explicit-bounds histogram stream built to exercise all three
    /// <c>MetricBucketPoint.MinMaxApproximate</c> cases (still-not-changed correctness plan, item 3):
    /// a pre-window baseline, then three in-window points — both Min and Max move together at each
    /// point, so each assertion is unambiguous (the flag is a single bool covering both dimensions:
    /// approximate whenever *either* one is, per the model's own doc comment).
    /// <list type="bullet">
    /// <item>+10s: the lifetime min/max both move (50 → 10, 250 → 350); a moved extreme is
    /// necessarily attributable to this bucket, so both are exact.</item>
    /// <item>+20s: the lifetime min/max stay put (new observations all land in one lower
    /// explicit-bounds bucket); both are only bound estimates, tightened to that bucket's own
    /// edges — approximate.</item>
    /// <item>+30s: <c>StartTimeUnixNano</c> advances (a counter reset), so the reported min/max cover
    /// exactly this bucket's own lifetime — exact again.</item>
    /// </list>
    /// Bounds <c>[100, 200, 300]</c> give four buckets: [0,100), [100,200), [200,300), [300,∞).
    /// </summary>
    public static MetricModel CumulativeHistogramMinMaxSeries(long tenantId, DateTime start, string serviceName = "minmax-svc")
    {
        var bounds = new[] { 100d, 200d, 300d };
        var streamStart = ToUnixNano(start.AddSeconds(-5));
        var resetStart = ToUnixNano(start.AddSeconds(25));
        var attrs = new Dictionary<string, object> { ["route"] = "/search" };

        HistogramDataPointModel Point(DateTime ts, long? startNano, long count, double sum, double min, double max, long[] bucketCounts) => new()
        {
            StartTimeUnixNano = startNano,
            TimeUnixNano = ToUnixNano(ts),
            Count = count,
            Sum = sum,
            Min = min,
            Max = max,
            ExplicitBounds = bounds,
            BucketCounts = bucketCounts,
            AggregationTemporality = AggregationTemporality.CUMULATIVE,
            Attributes = attrs
        };

        return new MetricModel
        {
            Name = "correctness.minmax.cumulative",
            Type = MetricType.HISTOGRAM,
            Unit = "ms",
            HistogramDataPoints =
            [
                // Pre-window baseline: 10 obs, all in [200,300).
                Point(start.AddSeconds(-5), streamStart, 10, 1000, 50, 250, [0, 0, 10, 0]),
                // +10s: min drops to 10, max grows to 350 — the 5 new obs land in [300,∞).
                Point(start.AddSeconds(10), streamStart, 15, 1500, 10, 350, [0, 0, 10, 5]),
                // +20s: min/max unchanged — the 5 new obs land in [100,200) this time.
                Point(start.AddSeconds(20), streamStart, 20, 2000, 10, 350, [0, 5, 10, 5]),
                // +30s: reset (new StartTimeUnixNano) — reported min/max cover just this bucket.
                Point(start.AddSeconds(30), resetStart, 3, 300, 2, 99, [1, 0, 2, 0]),
            ],
            Resource = Resource(tenantId, serviceName),
            InstrumentationScope = Scope()
        };
    }

    /// <summary>One explicit-bounds histogram stream, delta temporality, increasing counts over time — for SQL SUM(count)/unbuffered bucket-count merge checks.</summary>
    public static MetricModel HistogramDeltaSeries(long tenantId, DateTime start, string serviceName = "latency-svc", int points = 10)
    {
        var bounds = new[] { 10d, 50d, 100d, 500d };
        var resource = Resource(tenantId, serviceName);
        var dps = new List<HistogramDataPointModel>(points);
        for (var i = 0; i < points; i++)
        {
            dps.Add(new HistogramDataPointModel
            {
                TimeUnixNano = ToUnixNano(start.AddSeconds(i)),
                Count = 4,
                Sum = 120,
                Min = 5,
                Max = 400,
                ExplicitBounds = bounds,
                BucketCounts = [1, 1, 1, 1, 0], // len = bounds.Length + 1
                AggregationTemporality = AggregationTemporality.DELTA,
                Attributes = new Dictionary<string, object> { ["route"] = "/checkout" }
            });
        }
        return new MetricModel
        {
            Name = "phase4.latency.delta",
            Type = MetricType.HISTOGRAM,
            Unit = "ms",
            HistogramDataPoints = dps,
            Resource = resource,
            InstrumentationScope = Scope()
        };
    }

    /// <summary>
    /// Two streams of the same histogram metric with DIFFERENT explicit_bounds (a config change
    /// between two service instances) — for decision 42's layout-exclusion check. The first stream
    /// carries more total observations, so it should be the one charted.
    /// </summary>
    public static List<MetricModel> HistogramMismatchedLayouts(long tenantId, DateTime start, string serviceName = "mixed-svc")
    {
        var scope = Scope();
        var majority = new MetricModel
        {
            Name = "phase4.mixed.histogram",
            Type = MetricType.HISTOGRAM,
            Unit = "ms",
            HistogramDataPoints =
            [
                new HistogramDataPointModel
                {
                    TimeUnixNano = ToUnixNano(start.AddSeconds(1)),
                    Count = 100, Sum = 1000, Min = 1, Max = 50,
                    ExplicitBounds = [10, 20, 30],
                    BucketCounts = [25, 25, 25, 25],
                    AggregationTemporality = AggregationTemporality.DELTA,
                    Attributes = new Dictionary<string, object> { ["k8s.pod.name"] = "mixed-svc-0" }
                }
            ],
            Resource = Resource(tenantId, serviceName, instanceId: "pod-0"),
            InstrumentationScope = scope
        };
        var minority = new MetricModel
        {
            Name = "phase4.mixed.histogram",
            Type = MetricType.HISTOGRAM,
            Unit = "ms",
            HistogramDataPoints =
            [
                new HistogramDataPointModel
                {
                    TimeUnixNano = ToUnixNano(start.AddSeconds(1)),
                    Count = 5, Sum = 50, Min = 1, Max = 20,
                    ExplicitBounds = [5, 15], // different layout, fewer total observations
                    BucketCounts = [2, 2, 1],
                    AggregationTemporality = AggregationTemporality.DELTA,
                    Attributes = new Dictionary<string, object> { ["k8s.pod.name"] = "mixed-svc-1" }
                }
            ],
            Resource = Resource(tenantId, serviceName, instanceId: "pod-1"),
            InstrumentationScope = scope
        };
        return [majority, minority];
    }

    /// <summary>
    /// Two exponential-histogram points at different scales (a scale change mid-stream) — for
    /// decision 42's exp-histogram downscale-to-coarsest check. Scale 3 is finer (narrower buckets)
    /// than scale 1; the coarser scale 1 should be the query's target.
    /// </summary>
    public static MetricModel ExpHistogramMultiScale(long tenantId, DateTime start, string serviceName = "exp-svc")
    {
        var resource = Resource(tenantId, serviceName);
        return new MetricModel
        {
            Name = "phase4.exp.histogram",
            Type = MetricType.EXPONENTIAL_HISTOGRAM,
            Unit = "ms",
            ExponentialHistogramDataPoints =
            [
                new ExponentialHistogramDataPointModel
                {
                    TimeUnixNano = ToUnixNano(start.AddSeconds(1)),
                    Count = 8, Sum = 80, Min = 1, Max = 20,
                    Scale = 3, ZeroCount = 0, PositiveOffset = 0,
                    PositiveBucketCounts = [1, 1, 1, 1, 1, 1, 1, 1],
                    AggregationTemporality = AggregationTemporality.DELTA,
                    Attributes = new Dictionary<string, object> { ["route"] = "/search" }
                },
                new ExponentialHistogramDataPointModel
                {
                    TimeUnixNano = ToUnixNano(start.AddSeconds(2)),
                    Count = 4, Sum = 80, Min = 1, Max = 20,
                    Scale = 1, ZeroCount = 0, PositiveOffset = 0,
                    PositiveBucketCounts = [1, 1, 1, 1],
                    AggregationTemporality = AggregationTemporality.DELTA,
                    Attributes = new Dictionary<string, object> { ["route"] = "/search" }
                }
            ],
            Resource = resource,
            InstrumentationScope = Scope()
        };
    }

    /// <summary>A summary stream (quantiles p50/p90/p99) — for the per-bucket-average summary check.</summary>
    public static MetricModel SummarySeries(long tenantId, DateTime start, string serviceName = "gateway-svc", int points = 5)
    {
        var resource = Resource(tenantId, serviceName);
        var dps = new List<SummaryDataPointModel>(points);
        for (var i = 0; i < points; i++)
        {
            dps.Add(new SummaryDataPointModel
            {
                TimeUnixNano = ToUnixNano(start.AddSeconds(i * 10)),
                Count = 100 + i,
                Sum = 5000 + i * 10,
                QuantileValues =
                [
                    new QuantileValueModel { Quantile = 0.5, Value = 20 + i },
                    new QuantileValueModel { Quantile = 0.9, Value = 80 + i },
                    new QuantileValueModel { Quantile = 0.99, Value = 150 + i }
                ]
            });
        }
        return new MetricModel
        {
            Name = "phase4.gateway.summary",
            Type = MetricType.SUMMARY,
            Unit = "ms",
            SummaryDataPoints = dps,
            Resource = resource,
            InstrumentationScope = Scope()
        };
    }

    /// <summary>
    /// Ten distinct gauge streams (services) for one metric name, with a clear magnitude ranking —
    /// for the top-N + "other" fold check (default top = 8). <paramref name="metricName"/> and
    /// <paramref name="servicePrefix"/> default to the values <c>MetricPhase4TestsBase</c>'s own
    /// top-N test uses; a caller in a different test class sharing the same provider fixture
    /// collection (e.g. Phase 8's export tests) should pass distinct values — <c>metrics</c> is
    /// truncated between test classes but the process-lifetime <c>ResourceScopeCache</c> is not
    /// (see each fixture's own <c>ResetAsync</c> doc comment), so reusing the exact same metric
    /// name/service pair across two test classes hands the second flush a cached metric id whose
    /// row no longer exists, failing its data-point insert on the foreign key.
    /// </summary>
    public static List<MetricModel> ManyStreamsForTopN(long tenantId, DateTime start, int streamCount = 10, string metricName = "phase4.topn.gauge", string servicePrefix = "svc")
    {
        var scope = Scope();
        var metrics = new List<MetricModel>(streamCount);
        for (var i = 0; i < streamCount; i++)
        {
            var serviceName = $"{servicePrefix}-{i:D2}";
            // Descending magnitude: index 0 is the largest, the last index the smallest — so the
            // two lowest ranks past top=8 fold into "other".
            var value = (streamCount - i) * 100;
            metrics.Add(new MetricModel
            {
                Name = metricName,
                Type = MetricType.GAUGE,
                Unit = "1",
                GaugeDataPoints =
                [
                    new GaugeDataPointModel { TimeUnixNano = ToUnixNano(start.AddSeconds(1)), ValueDouble = value }
                ],
                Resource = Resource(tenantId, serviceName),
                InstrumentationScope = scope
            });
        }
        return metrics;
    }

    /// <summary>A gauge metric whose points each carry one exemplar — for exemplar keyset/capped paging checks.</summary>
    public static MetricModel GaugeWithExemplars(long tenantId, DateTime start, string serviceName = "exemplar-svc", int points = 30)
    {
        var resource = Resource(tenantId, serviceName);
        var dps = new List<GaugeDataPointModel>(points);
        for (var i = 0; i < points; i++)
        {
            dps.Add(new GaugeDataPointModel
            {
                TimeUnixNano = ToUnixNano(start.AddSeconds(i)),
                ValueDouble = i,
                Attributes = new Dictionary<string, object> { ["route"] = "/checkout" },
                Exemplars =
                [
                    new ExemplarModel { TimeUnixNano = ToUnixNano(start.AddSeconds(i)), ValueDouble = i, TraceIdHex = i.ToString("x").PadLeft(32, '0') }
                ]
            });
        }
        return new MetricModel
        {
            Name = "phase4.exemplar.gauge",
            Type = MetricType.GAUGE,
            Unit = "1",
            GaugeDataPoints = dps,
            Resource = resource,
            InstrumentationScope = Scope()
        };
    }

    // =========================================================================
    // PHASE 5: metrics catalog (list-pages-server-side plan)
    // =========================================================================

    /// <summary>
    /// One metric instance with a single data point at <paramref name="pointTime"/>, for catalog
    /// paging/filtering tests. <paramref name="instanceId"/> distinguishes several instances of the
    /// same <paramref name="name"/> (same or different <paramref name="serviceName"/>) so
    /// <c>groupBy=name</c>'s instance count/service list can be exercised.
    /// </summary>
    public static MetricModel CatalogMetric(
        long tenantId, string name, MetricType type, string serviceName, DateTime pointTime, string? instanceId = null)
    {
        var resource = Resource(tenantId, serviceName, instanceId);
        var scope = Scope();
        var metric = new MetricModel { Name = name, Type = type, Unit = "1", Resource = resource, InstrumentationScope = scope };
        var nano = ToUnixNano(pointTime);
        switch (type)
        {
            case MetricType.GAUGE:
                metric.GaugeDataPoints = [new GaugeDataPointModel { TimeUnixNano = nano, ValueDouble = 1 }];
                break;
            case MetricType.SUM:
                metric.SumDataPoints = [new SumDataPointModel { TimeUnixNano = nano, ValueDouble = 1, AggregationTemporality = AggregationTemporality.DELTA }];
                break;
            case MetricType.HISTOGRAM:
                metric.HistogramDataPoints =
                [
                    new HistogramDataPointModel
                    {
                        TimeUnixNano = nano, Count = 1, Sum = 1,
                        BucketCounts = [1, 0], ExplicitBounds = [10],
                        AggregationTemporality = AggregationTemporality.DELTA
                    }
                ];
                break;
            case MetricType.EXPONENTIAL_HISTOGRAM:
                metric.ExponentialHistogramDataPoints =
                [
                    new ExponentialHistogramDataPointModel
                    {
                        TimeUnixNano = nano, Count = 1, Sum = 1, Scale = 0, ZeroCount = 0,
                        AggregationTemporality = AggregationTemporality.DELTA
                    }
                ];
                break;
            case MetricType.SUMMARY:
                metric.SummaryDataPoints =
                [
                    new SummaryDataPointModel { TimeUnixNano = nano, Count = 1, Sum = 1, QuantileValues = [new QuantileValueModel { Quantile = 0.5, Value = 1 }] }
                ];
                break;
        }
        return metric;
    }
}
