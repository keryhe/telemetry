using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Model;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Metrics.V1;
using ProtoExemplar = OpenTelemetry.Proto.Metrics.V1.Exemplar;
using ProtoMetric = OpenTelemetry.Proto.Metrics.V1.Metric;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>
/// Turns per-request measurements and periodic samples into cumulative OTLP metrics, the temporality the
/// SDK uses. A stream (pod, instrument, attribute set) is exported only when it changed since the last
/// flush, which a cumulative stream may do without losing anything.
/// </summary>
internal sealed class MetricAggregator
{
    private const int ExemplarsPerPoint = 2;

    private readonly DateTimeOffset _startTime;
    private readonly SimRandom _rng;
    private readonly Dictionary<(string Pod, string Instrument, string AttrKey), Stream> _streams = [];

    public MetricAggregator(DateTimeOffset startTime, SimRandom rng)
    {
        _startTime = startTime;
        _rng = rng;
    }

    private sealed class Stream
    {
        public required ServiceInstance Instance { get; init; }
        public required InstrumentDef Def { get; init; }
        public required Tags Attributes { get; init; }
        public bool Dirty;
        public double Sum;
        public ulong Count;
        public double Min = double.PositiveInfinity;
        public double Max = double.NegativeInfinity;
        public ulong[]? Buckets;
        public SortedDictionary<int, ulong>? Exp;
        public ulong ZeroCount;
        public double Value;
        public readonly List<(double Value, DateTimeOffset Time, SimSpan Span)> Exemplars = [];
        public int ExemplarsSeen;
    }

    private Stream StreamFor(ServiceInstance instance, string instrument, Tags attrs)
    {
        var key = (instance.PodName, instrument, attrs.Key());
        if (_streams.TryGetValue(key, out var stream)) return stream;
        var def = Instruments.Get(instrument);
        stream = new Stream
        {
            Instance = instance,
            Def = def,
            Attributes = attrs,
            Buckets = def.Kind == InstrumentKind.HistogramExplicit ? new ulong[def.Boundaries!.Length + 1] : null,
            Exp = def.Kind == InstrumentKind.HistogramExponential ? [] : null,
        };
        _streams[key] = stream;
        return stream;
    }

    public void Observe(SimSpan span, SimMeasurement m)
    {
        var stream = StreamFor(span.Instance, m.Instrument, m.Attributes);
        stream.Dirty = true;
        var v = m.Value;
        switch (stream.Def.Kind)
        {
            case InstrumentKind.Counter:
                stream.Value += v;
                return;
            case InstrumentKind.HistogramExplicit:
            {
                var bounds = stream.Def.Boundaries!;
                var i = 0;
                while (i < bounds.Length && v > bounds[i]) i++; // bucket i holds (bounds[i-1], bounds[i]]
                stream.Buckets![i]++;
                break;
            }
            case InstrumentKind.HistogramExponential:
            {
                if (v <= 0) stream.ZeroCount++;
                else
                {
                    var index = (int)Math.Ceiling(Math.Log2(v) * Math.Pow(2, Instruments.ExponentialScale)) - 1;
                    stream.Exp![index] = stream.Exp.GetValueOrDefault(index) + 1;
                }
                break;
            }
        }
        stream.Count++;
        stream.Sum += v;
        stream.Min = Math.Min(stream.Min, v);
        stream.Max = Math.Max(stream.Max, v);

        // Reservoir sampling: each measurement of the interval has an equal chance of being an exemplar.
        stream.ExemplarsSeen++;
        if (stream.Exemplars.Count < ExemplarsPerPoint) stream.Exemplars.Add((v, span.End, span));
        else
        {
            var slot = _rng.Next(stream.ExemplarsSeen);
            if (slot < ExemplarsPerPoint) stream.Exemplars[slot] = (v, span.End, span);
        }
    }

    public void Sample(SimSample s)
    {
        var stream = StreamFor(s.Instance, s.Instrument, s.Attributes);
        stream.Value = s.Value;
        stream.Dirty = true;
    }

    /// <summary>Builds one export holding every stream that changed, stamped <paramref name="time"/>; null if none did.</summary>
    public ExportMetricsServiceRequest? Flush(DateTimeOffset time)
    {
        var request = new ExportMetricsServiceRequest();
        var endNano = OtlpMapper.UnixNano(time);
        var startNano = OtlpMapper.UnixNano(_startTime);

        foreach (var perPod in _streams.Values.Where(s => s.Dirty).GroupBy(s => s.Instance.PodName))
        {
            var instance = perPod.First().Instance;
            var scopeMetrics = new ScopeMetrics { Scope = OtlpMapper.Scope(instance.PodName) };
            foreach (var perInstrument in perPod.GroupBy(s => s.Def.Name))
            {
                var def = perInstrument.First().Def;
                var metric = new ProtoMetric { Name = def.Name, Description = def.Description, Unit = def.Unit };
                switch (def.Kind)
                {
                    case InstrumentKind.HistogramExplicit:
                        metric.Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Cumulative };
                        foreach (var s in perInstrument) metric.Histogram.DataPoints.Add(ExplicitPoint(s, startNano, endNano));
                        break;
                    case InstrumentKind.HistogramExponential:
                        metric.ExponentialHistogram = new ExponentialHistogram { AggregationTemporality = AggregationTemporality.Cumulative };
                        foreach (var s in perInstrument) metric.ExponentialHistogram.DataPoints.Add(ExponentialPoint(s, startNano, endNano));
                        break;
                    case InstrumentKind.Counter:
                        metric.Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true };
                        foreach (var s in perInstrument) metric.Sum.DataPoints.Add(NumberPoint(s, startNano, endNano));
                        break;
                    case InstrumentKind.UpDownSum:
                        metric.Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = false };
                        foreach (var s in perInstrument) metric.Sum.DataPoints.Add(NumberPoint(s, startNano, endNano));
                        break;
                    case InstrumentKind.Gauge:
                        metric.Gauge = new Gauge();
                        foreach (var s in perInstrument) metric.Gauge.DataPoints.Add(NumberPoint(s, 0, endNano));
                        break;
                }
                scopeMetrics.Metrics.Add(metric);
            }
            var resourceMetrics = new ResourceMetrics { Resource = OtlpMapper.ResourceOf(instance) };
            resourceMetrics.ScopeMetrics.Add(scopeMetrics);
            request.ResourceMetrics.Add(resourceMetrics);
        }

        foreach (var s in _streams.Values)
        {
            s.Dirty = false;
            s.Exemplars.Clear();
            s.ExemplarsSeen = 0;
        }
        return request.ResourceMetrics.Count == 0 ? null : request;
    }

    private static HistogramDataPoint ExplicitPoint(Stream s, ulong start, ulong end)
    {
        var p = new HistogramDataPoint { StartTimeUnixNano = start, TimeUnixNano = end, Count = s.Count, Sum = s.Sum, Min = s.Min, Max = s.Max };
        OtlpMapper.AddAttributes(p.Attributes, s.Attributes);
        p.BucketCounts.AddRange(s.Buckets!);
        p.ExplicitBounds.AddRange(s.Def.Boundaries!);
        AddExemplars(p.Exemplars, s);
        return p;
    }

    private static ExponentialHistogramDataPoint ExponentialPoint(Stream s, ulong start, ulong end)
    {
        var p = new ExponentialHistogramDataPoint
        {
            StartTimeUnixNano = start, TimeUnixNano = end, Count = s.Count, Sum = s.Sum, Min = s.Min, Max = s.Max,
            Scale = Instruments.ExponentialScale, ZeroCount = s.ZeroCount, Positive = new ExponentialHistogramDataPoint.Types.Buckets(),
        };
        OtlpMapper.AddAttributes(p.Attributes, s.Attributes);
        if (s.Exp!.Count > 0)
        {
            var min = s.Exp.Keys.First();
            var max = s.Exp.Keys.Last();
            p.Positive.Offset = min;
            for (var i = min; i <= max; i++) p.Positive.BucketCounts.Add(s.Exp.GetValueOrDefault(i));
        }
        AddExemplars(p.Exemplars, s);
        return p;
    }

    private static NumberDataPoint NumberPoint(Stream s, ulong start, ulong end)
    {
        var p = new NumberDataPoint { StartTimeUnixNano = start, TimeUnixNano = end };
        // The SDK's counters are integral (Counter<long>); sampled instruments are doubles.
        if (s.Def.Kind == InstrumentKind.Counter) p.AsInt = (long)s.Value; else p.AsDouble = s.Value;
        OtlpMapper.AddAttributes(p.Attributes, s.Attributes);
        return p;
    }

    private static void AddExemplars(Google.Protobuf.Collections.RepeatedField<ProtoExemplar> target, Stream s)
    {
        foreach (var (value, time, span) in s.Exemplars)
        {
            target.Add(new ProtoExemplar
            {
                TimeUnixNano = OtlpMapper.UnixNano(time),
                AsDouble = value,
                TraceId = OtlpMapper.Id(span.TraceId),
                SpanId = OtlpMapper.Id(span.SpanId),
            });
        }
    }
}
