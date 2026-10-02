using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

/// <summary>
/// Sends the same simulated telemetry through both sinks to a fake collector: hand-built OTLP (backfill) and
/// the real SDK (live). The two must look alike to a collector, otherwise a backfilled day and the live data
/// that follows it would not be one coherent dataset.
/// </summary>
public sealed class SinkParityFixture : IAsyncLifetime
{
    public const string ApiKey = "parity-key";

    public FakeCollector Backfill { get; private set; } = null!;
    public FakeCollector Live { get; private set; } = null!;
    public List<SimSpan> Spans { get; } = [];
    public List<SimLog> Logs { get; } = [];

    public async Task InitializeAsync()
    {
        Backfill = await FakeCollector.StartAsync();
        Live = await FakeCollector.StartAsync();

        var from = TestSupport.Day.AddHours(20);
        var chunks = new List<SimChunk>();
        var sim = TestSupport.Simulator();
        for (var i = 0; i < 3; i++)
            chunks.Add(sim.Simulate(from.AddMinutes(i), from.AddMinutes(i + 1), includeSamples: true));
        foreach (var c in chunks)
        {
            Spans.AddRange(c.AllSpans());
            Logs.AddRange(c.AllSpans().SelectMany(s => s.Logs).Concat(c.BackgroundLogs));
        }

        await using (var otlp = new OtlpSink("acme-retail", ApiKey, Backfill.Endpoint, 2000, 7, NullLogger.Instance))
            foreach (var c in chunks) await otlp.WriteAsync(c, CancellationToken.None);

        await using (var sdk = new SdkSink(Live.Endpoint, ApiKey, metricIntervalSeconds: 3600))
            foreach (var c in chunks) await sdk.WriteAsync(c, CancellationToken.None);
        // Disposing the SDK sink flushes every provider, which is what delivers the metrics.
    }

    public async Task DisposeAsync()
    {
        await Backfill.DisposeAsync();
        await Live.DisposeAsync();
    }
}

public class SinkParityTests : IClassFixture<SinkParityFixture>
{
    private readonly SinkParityFixture _f;

    public SinkParityTests(SinkParityFixture fixture) => _f = fixture;

    private static IEnumerable<(Resource Resource, string Scope, Span Span)> SpansOf(FakeCollector c) =>
        c.Traces.SelectMany(t => t.Request.ResourceSpans.SelectMany(rs => rs.ScopeSpans.SelectMany(ss => ss.Spans.Select(s => (rs.Resource, ss.Scope.Name, s)))));

    private static IEnumerable<(Resource Resource, string Scope, OpenTelemetry.Proto.Logs.V1.LogRecord Log)> LogsOf(FakeCollector c) =>
        c.Logs.SelectMany(t => t.Request.ResourceLogs.SelectMany(rl => rl.ScopeLogs.SelectMany(sl => sl.LogRecords.Select(l => (rl.Resource, sl.Scope.Name, l)))));

    private static IEnumerable<(Resource Resource, string Scope, Metric Metric)> MetricsOf(FakeCollector c) =>
        c.Metrics.SelectMany(t => t.Request.ResourceMetrics.SelectMany(rm => rm.ScopeMetrics.SelectMany(sm => sm.Metrics.Select(m => (rm.Resource, sm.Scope.Name, m)))));

    private static string Key(IEnumerable<KeyValue> attrs) => string.Join(",", attrs.Select(a => a.Key).Order());

    private static string Str(Resource r, string key) => r.Attributes.First(a => a.Key == key).Value.StringValue;

    [Fact]
    public void Both_sinks_authenticate_as_the_tenant()
    {
        foreach (var collector in new[] { _f.Backfill, _f.Live })
        {
            Assert.All(collector.Traces, t => Assert.Equal($"Bearer {SinkParityFixture.ApiKey}", t.Auth));
            Assert.All(collector.Logs, t => Assert.Equal($"Bearer {SinkParityFixture.ApiKey}", t.Auth));
            Assert.All(collector.Metrics, t => Assert.Equal($"Bearer {SinkParityFixture.ApiKey}", t.Auth));
        }
    }

    [Fact]
    public void Both_sinks_deliver_every_span()
    {
        Assert.Equal(_f.Spans.Count, SpansOf(_f.Backfill).Count());
        Assert.Equal(_f.Spans.Count, SpansOf(_f.Live).Count());
    }

    [Fact]
    public void Spans_have_the_same_shape()
    {
        string Shape(IEnumerable<(Resource Resource, string Scope, Span Span)> spans) => string.Join("\n",
            spans.GroupBy(s => $"{s.Span.Name}|{s.Span.Kind}|{Key(s.Span.Attributes)}|ev={string.Join(",", s.Span.Events.Select(e => e.Name).Order())}|links={s.Span.Links.Count}|status={s.Span.Status?.Code}")
                .Select(g => $"{g.Key} x{g.Count()}").Order());
        Assert.Equal(Shape(SpansOf(_f.Backfill)), Shape(SpansOf(_f.Live)));
    }

    [Fact]
    public void Span_timestamps_survive_the_sdk()
    {
        // The SDK path must keep the simulated start/end, not stamp the wall clock.
        var expected = _f.Spans.Select(s => (s.Name, s.Start.ToUnixTimeMilliseconds() , s.End.ToUnixTimeMilliseconds())).Order().ToList();
        var actual = SpansOf(_f.Live).Select(s => (s.Span.Name, (long)(s.Span.StartTimeUnixNano / 1_000_000), (long)(s.Span.EndTimeUnixNano / 1_000_000))).Order().ToList();
        Assert.Equal(expected.Count, actual.Count);
        // Millisecond rounding of ticks can differ by 1 between the two paths.
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Name, actual[i].Name);
            Assert.InRange(Math.Abs(expected[i].Item2 - actual[i].Item2), 0, 1);
        }
    }

    [Fact]
    public void Resources_are_identical_per_pod()
    {
        Dictionary<string, string> ByPod(FakeCollector c) => SpansOf(c).Select(s => s.Resource).DistinctBy(r => Str(r, "service.instance.id"))
            .ToDictionary(r => Str(r, "k8s.pod.name"), r => string.Join(";", r.Attributes.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value.StringValue}")));
        var backfill = ByPod(_f.Backfill);
        var live = ByPod(_f.Live);
        Assert.Equal(backfill.Keys.Order(), live.Keys.Order());
        foreach (var (pod, attrs) in backfill) Assert.Equal(attrs, live[pod]);
        Assert.All(backfill.Values, a => Assert.Contains("telemetry.sdk.name=opentelemetry", a));
    }

    [Fact]
    public void Trace_scopes_are_named_per_pod_in_both()
    {
        Assert.Equal(SpansOf(_f.Backfill).Select(s => s.Scope).Distinct().Order(), SpansOf(_f.Live).Select(s => s.Scope).Distinct().Order());
    }

    [Fact]
    public void Logs_arrive_with_the_same_bodies_severities_and_attributes()
    {
        string Shape(IEnumerable<(Resource Resource, string Scope, OpenTelemetry.Proto.Logs.V1.LogRecord Log)> logs) => string.Join("\n",
            logs.GroupBy(l => $"{l.Scope}|{l.Log.SeverityNumber}|{(l.Log.Body.StringValue.Length > 60 ? l.Log.Body.StringValue[..60] : l.Log.Body.StringValue)}|{Key(l.Log.Attributes)}|trace={!l.Log.TraceId.IsEmpty}")
                .Select(g => $"{g.Key} x{g.Count()}").Order());
        Assert.Equal(_f.Logs.Count, LogsOf(_f.Backfill).Count());
        Assert.Equal(_f.Logs.Count, LogsOf(_f.Live).Count());
        Assert.Equal(Shape(LogsOf(_f.Backfill)), Shape(LogsOf(_f.Live)));
    }

    [Fact]
    public void Sdk_logs_carry_the_trace_and_span_they_were_written_in_and_the_simulated_time()
    {
        var spans = SpansOf(_f.Live).ToDictionary(s => s.Span.SpanId.ToBase64(), s => s.Span);
        var correlated = 0;
        foreach (var (_, _, log) in LogsOf(_f.Live).Where(l => !l.Log.SpanId.IsEmpty))
        {
            var span = spans[log.SpanId.ToBase64()];
            Assert.Equal(span.TraceId, log.TraceId);
            Assert.InRange(log.TimeUnixNano, span.StartTimeUnixNano, span.EndTimeUnixNano);
            correlated++;
        }
        Assert.True(correlated > 1000);
    }

    [Fact]
    public void Metrics_have_the_same_names_units_types_and_attributes()
    {
        string Shape(IEnumerable<(Resource Resource, string Scope, Metric Metric)> metrics) => string.Join("\n",
            metrics.GroupBy(m => $"{m.Metric.Name}|{m.Metric.Unit}|{m.Metric.DataCase}|{Detail(m.Metric)}|{string.Join(",", Points(m.Metric).Select(p => Key(p.Attributes)).Distinct().Order())}")
                .Select(g => g.Key).Order());
        Assert.Equal(Shape(MetricsOf(_f.Backfill)), Shape(MetricsOf(_f.Live)));
    }

    private static string Detail(Metric m) => m.DataCase switch
    {
        Metric.DataOneofCase.Sum => $"mono={m.Sum.IsMonotonic},{m.Sum.AggregationTemporality}",
        Metric.DataOneofCase.Histogram => $"{m.Histogram.AggregationTemporality},bounds={string.Join(",", m.Histogram.DataPoints.First().ExplicitBounds)}",
        Metric.DataOneofCase.ExponentialHistogram => m.ExponentialHistogram.AggregationTemporality.ToString(),
        _ => "",
    };

    private static IEnumerable<(IEnumerable<KeyValue> Attributes, IEnumerable<Exemplar> Exemplars)> Points(Metric m) => m.DataCase switch
    {
        Metric.DataOneofCase.Sum => m.Sum.DataPoints.Select(p => ((IEnumerable<KeyValue>)p.Attributes, (IEnumerable<Exemplar>)p.Exemplars)),
        Metric.DataOneofCase.Gauge => m.Gauge.DataPoints.Select(p => ((IEnumerable<KeyValue>)p.Attributes, (IEnumerable<Exemplar>)p.Exemplars)),
        Metric.DataOneofCase.Histogram => m.Histogram.DataPoints.Select(p => ((IEnumerable<KeyValue>)p.Attributes, (IEnumerable<Exemplar>)p.Exemplars)),
        Metric.DataOneofCase.ExponentialHistogram => m.ExponentialHistogram.DataPoints.Select(p => ((IEnumerable<KeyValue>)p.Attributes, (IEnumerable<Exemplar>)p.Exemplars)),
        _ => [],
    };

    [Fact]
    public void Histograms_and_counters_count_what_the_requests_did_in_both_sinks()
    {
        long Count(FakeCollector c, string name) => MetricsOf(c).Where(m => m.Metric.Name == name)
            .GroupBy(m => m.Resource.Attributes.First(a => a.Key == "k8s.pod.name").Value.StringValue)
            .Sum(podGroup => podGroup.SelectMany(m => Points(m.Metric)).Count() > 0
                ? LatestCounts(podGroup.Select(m => m.Metric)) : 0);

        var expectedServer = _f.Spans.Count(s => s.Kind == SimSpanKind.Server);
        Assert.Equal(expectedServer, Count(_f.Backfill, Instruments.HttpServerDuration));
        Assert.Equal(expectedServer, Count(_f.Live, Instruments.HttpServerDuration));
        var expectedOrders = _f.Spans.Count(s => s.Kind == SimSpanKind.Producer);
        Assert.Equal(expectedOrders, Count(_f.Backfill, Instruments.OrdersPlaced));
        Assert.Equal(expectedOrders, Count(_f.Live, Instruments.OrdersPlaced));
    }

    /// <summary>For cumulative metrics: the final count of each distinct series, summed (earlier exports are superseded).</summary>
    private static long LatestCounts(IEnumerable<Metric> exports)
    {
        var latest = new Dictionary<string, (ulong Time, long Value)>();
        foreach (var m in exports)
        {
            switch (m.DataCase)
            {
                case Metric.DataOneofCase.Histogram:
                    foreach (var p in m.Histogram.DataPoints) Keep(latest, p.Attributes, p.TimeUnixNano, (long)p.Count);
                    break;
                case Metric.DataOneofCase.Sum:
                    foreach (var p in m.Sum.DataPoints) Keep(latest, p.Attributes, p.TimeUnixNano, p.HasAsInt ? p.AsInt : (long)p.AsDouble);
                    break;
            }
        }
        return latest.Values.Sum(v => v.Value);
    }

    private static void Keep(Dictionary<string, (ulong Time, long Value)> latest, IEnumerable<KeyValue> attrs, ulong time, long value)
    {
        var key = string.Join("|", attrs.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value}"));
        if (!latest.TryGetValue(key, out var existing) || time >= existing.Time) latest[key] = (time, value);
    }

    [Fact]
    public void Exemplars_point_at_real_spans_in_both_sinks()
    {
        foreach (var collector in new[] { _f.Backfill, _f.Live })
        {
            var traceIds = SpansOf(collector).Select(s => s.Span.TraceId.ToBase64()).ToHashSet();
            var exemplars = MetricsOf(collector).SelectMany(m => Points(m.Metric).SelectMany(p => p.Exemplars)).ToList();
            Assert.NotEmpty(exemplars);
            Assert.All(exemplars, e =>
            {
                Assert.False(e.TraceId.IsEmpty);
                Assert.Contains(e.TraceId.ToBase64(), traceIds);
            });
        }
    }

    [Fact]
    public void Gauges_and_up_down_sums_are_delivered_for_the_resource_metrics()
    {
        foreach (var collector in new[] { _f.Backfill, _f.Live })
        {
            var names = MetricsOf(collector).Select(m => m.Metric.Name).ToHashSet();
            Assert.Contains(Instruments.ProcessCpu, names);
            Assert.Contains(Instruments.ProcessMemory, names);
            Assert.Contains(Instruments.DbConnections, names);
        }
    }
}
