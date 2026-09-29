using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Keryhe.Telemetry.StressTests.Load;

public enum MetricKind { Gauge, Sum, Histogram, ExponentialHistogram, Summary }

/// <summary>
/// Emits data points round-robin over each tenant's fixed set of streams (service x metric x series),
/// so the metric catalog stays small and stable while data-point volume follows the target rate. A
/// cumulative stream's value only ever grows; a delta stream reports only what changed.
/// </summary>
public sealed class MetricShaper
{
    private static readonly double[] Bounds = [5, 10, 25, 50, 100, 250, 500, 1000];
    private static readonly double[] Quantiles = [0.0, 0.5, 0.9, 0.99, 1.0];

    private sealed class Stream
    {
        public required ServiceInfo Service;
        public required string MetricName;
        public required MetricKind Kind;
        public required bool Delta;
        public required KeyValue[] Labels;
        public required long StartNanos;
        public double Value;                // gauge level / sum total
        public ulong Count;                 // histogram-family observation count
        public double Sum, Min = double.MaxValue, Max = double.MinValue;
        public ulong[] Buckets = new ulong[Bounds.Length + 1];
    }

    private sealed class TenantStreams
    {
        public required List<Stream> Streams;
        public int Cursor;
    }

    private static readonly string[] Units = ["ms", "By", "1", "{request}"];

    private readonly LoadProfile _profile;
    private readonly Topology _topology;
    private readonly Random _rng;
    private readonly List<TenantStreams> _tenants;

    public MetricShaper(LoadProfile profile, Topology topology, Random rng)
    {
        _profile = profile;
        _topology = topology;
        _rng = rng;

        var load = profile.Metrics;
        var kinds = load.TypeMix.Where(k => k.Value > 0 && Enum.TryParse<MetricKind>(k.Key, true, out _)).ToList();
        if (kinds.Count == 0) throw new InvalidDataException("Metrics.TypeMix has no positive weight for a known type.");
        var totalWeight = kinds.Sum(k => k.Value);
        var startNanos = TimeStamps.NowNanos();

        _tenants = topology.ServicesByTenant.Select(services =>
        {
            var streams = new List<Stream>();
            foreach (var service in services)
            {
                for (var m = 0; m < Math.Max(1, load.MetricNamesPerService); m++)
                {
                    // Type by weighted position, so the mix holds however few metrics a service has.
                    var slot = (m + 0.5) / Math.Max(1, load.MetricNamesPerService) * totalWeight;
                    var acc = 0.0;
                    var kind = Enum.Parse<MetricKind>(kinds[^1].Key, true);
                    foreach (var (name, weight) in kinds)
                    {
                        acc += weight;
                        if (slot < acc) { kind = Enum.Parse<MetricKind>(name, true); break; }
                    }
                    var delta = kind is MetricKind.Sum or MetricKind.Histogram or MetricKind.ExponentialHistogram
                                && rng.NextDouble() < load.DeltaFraction;
                    for (var s = 0; s < Math.Max(1, load.SeriesPerMetric); s++)
                    {
                        streams.Add(new Stream
                        {
                            Service = service,
                            MetricName = $"stress.{kind.ToString().ToLowerInvariant()}.m{m}",
                            Kind = kind,
                            Delta = delta,
                            Labels = [Topology.Str("instance", $"i{s}"), Topology.Str("region", $"r{s % 3}")],
                            StartNanos = startNanos,
                            Value = rng.NextDouble() * 100
                        });
                    }
                }
            }
            return new TenantStreams { Streams = streams };
        }).ToList();
    }

    public Payload<ExportMetricsServiceRequest> Next()
    {
        var tenantIndex = _topology.PickTenant(_rng);
        var tenant = _tenants[tenantIndex];
        var target = _profile.Transport.RecordsPerExport;
        var interval = Math.Max(1, _profile.Metrics.ExportIntervalSeconds) * 1_000_000_000L;

        var request = new ExportMetricsServiceRequest();
        var entries = new Dictionary<(string Table, RecordAge Age), long>();

        ResourceMetrics? rm = null;
        ScopeMetrics? sm = null;
        Metric? metric = null;
        Stream? previous = null;

        for (var i = 0; i < target; i++)
        {
            var stream = tenant.Streams[tenant.Cursor];
            tenant.Cursor = (tenant.Cursor + 1) % tenant.Streams.Count;

            if (previous is null || previous.Service != stream.Service)
            {
                rm = new ResourceMetrics { Resource = stream.Service.Resource };
                sm = new ScopeMetrics { Scope = _topology.Scope };
                rm.ScopeMetrics.Add(sm);
                request.ResourceMetrics.Add(rm);
                previous = null;
            }
            if (previous is null || previous.MetricName != stream.MetricName)
            {
                metric = NewMetric(stream);
                sm!.Metrics.Add(metric);
            }
            previous = stream;

            var (nanos, age) = TimeStamps.Pick(_profile.Time, _rng);
            var startNanos = stream.Delta ? nanos - interval : stream.StartNanos;
            AddPoint(metric!, stream, (ulong)nanos, (ulong)startNanos);

            var table = TableFor(stream.Kind);
            entries[(table, age)] = entries.GetValueOrDefault((table, age)) + 1;
        }

        var ledger = entries.Select(e => new LedgerEntry(e.Key.Table, e.Key.Age, e.Value, false, Dedups: false)).ToList();
        return new Payload<ExportMetricsServiceRequest>(request, tenantIndex, target, ledger,
            _rng.NextDouble() < _profile.Time.RedeliveryFraction);
    }

    public static string TableFor(MetricKind kind) => kind switch
    {
        MetricKind.Gauge => "gauge_data_points",
        MetricKind.Sum => "sum_data_points",
        MetricKind.Histogram => "histogram_data_points",
        MetricKind.ExponentialHistogram => "exponential_histogram_data_points",
        _ => "summary_data_points"
    };

    private Metric NewMetric(Stream s)
    {
        var metric = new Metric { Name = s.MetricName, Description = "stress metric", Unit = Units[Math.Abs(s.MetricName.GetHashCode()) % Units.Length] };
        var temporality = s.Delta ? AggregationTemporality.Delta : AggregationTemporality.Cumulative;
        switch (s.Kind)
        {
            case MetricKind.Gauge: metric.Gauge = new Gauge(); break;
            case MetricKind.Sum: metric.Sum = new Sum { IsMonotonic = true, AggregationTemporality = temporality }; break;
            case MetricKind.Histogram: metric.Histogram = new Histogram { AggregationTemporality = temporality }; break;
            case MetricKind.ExponentialHistogram: metric.ExponentialHistogram = new ExponentialHistogram { AggregationTemporality = temporality }; break;
            default: metric.Summary = new Summary(); break;
        }
        return metric;
    }

    private void AddPoint(Metric metric, Stream s, ulong time, ulong start)
    {
        switch (s.Kind)
        {
            case MetricKind.Gauge:
            {
                s.Value = Math.Max(0, s.Value + (_rng.NextDouble() - 0.5) * 10);
                var dp = new NumberDataPoint { TimeUnixNano = time, AsDouble = s.Value };
                dp.Attributes.AddRange(s.Labels);
                AddExemplar(dp.Exemplars, time);
                metric.Gauge.DataPoints.Add(dp);
                break;
            }
            case MetricKind.Sum:
            {
                var increment = _rng.NextDouble() * 20;
                double value;
                if (s.Delta) value = increment; else { s.Value += increment; value = s.Value; }
                var dp = new NumberDataPoint { TimeUnixNano = time, StartTimeUnixNano = start, AsDouble = value };
                dp.Attributes.AddRange(s.Labels);
                AddExemplar(dp.Exemplars, time);
                metric.Sum.DataPoints.Add(dp);
                break;
            }
            case MetricKind.Histogram:
            {
                var dp = new HistogramDataPoint { TimeUnixNano = time, StartTimeUnixNano = start };
                dp.ExplicitBounds.AddRange(Bounds);
                Observe(s, out var count, out var sum, out var min, out var max, out var buckets);
                dp.Count = count; dp.Sum = sum; dp.Min = min; dp.Max = max;
                dp.BucketCounts.AddRange(buckets);
                dp.Attributes.AddRange(s.Labels);
                AddExemplar(dp.Exemplars, time);
                metric.Histogram.DataPoints.Add(dp);
                break;
            }
            case MetricKind.ExponentialHistogram:
            {
                Observe(s, out var count, out var sum, out var min, out var max, out var buckets);
                var dp = new ExponentialHistogramDataPoint
                {
                    TimeUnixNano = time, StartTimeUnixNano = start, Scale = 2, ZeroCount = 0,
                    Count = count, Sum = sum, Min = min, Max = max,
                    Positive = new ExponentialHistogramDataPoint.Types.Buckets { Offset = 0 }
                };
                dp.Positive.BucketCounts.AddRange(buckets);
                dp.Attributes.AddRange(s.Labels);
                AddExemplar(dp.Exemplars, time);
                metric.ExponentialHistogram.DataPoints.Add(dp);
                break;
            }
            default:
            {
                Observe(s, out var count, out var sum, out var min, out var max, out _);
                var dp = new SummaryDataPoint { TimeUnixNano = time, StartTimeUnixNano = start, Count = count, Sum = sum };
                foreach (var q in Quantiles)
                    dp.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = q, Value = min + (max - min) * q });
                dp.Attributes.AddRange(s.Labels);
                metric.Summary.DataPoints.Add(dp);
                break;
            }
        }
    }

    // Records 1..20 new observations. Delta streams report just those; cumulative ones report the running totals.
    private void Observe(Stream s, out ulong count, out double sum, out double min, out double max, out ulong[] buckets)
    {
        var n = _rng.Next(1, 21);
        var fresh = new ulong[Bounds.Length + 1];
        double freshSum = 0, freshMin = double.MaxValue, freshMax = double.MinValue;
        for (var i = 0; i < n; i++)
        {
            var v = Math.Exp(_rng.NextDouble() * Math.Log(1500));
            var b = Array.FindIndex(Bounds, bound => v <= bound);
            fresh[b < 0 ? Bounds.Length : b]++;
            freshSum += v; freshMin = Math.Min(freshMin, v); freshMax = Math.Max(freshMax, v);
        }

        if (s.Delta)
        {
            count = (ulong)n; sum = freshSum; min = freshMin; max = freshMax; buckets = fresh;
            return;
        }
        for (var i = 0; i < fresh.Length; i++) s.Buckets[i] += fresh[i];
        s.Count += (ulong)n; s.Sum += freshSum;
        s.Min = Math.Min(s.Min, freshMin); s.Max = Math.Max(s.Max, freshMax);
        count = s.Count; sum = s.Sum; min = s.Min; max = s.Max; buckets = (ulong[])s.Buckets.Clone();
    }

    private void AddExemplar(Google.Protobuf.Collections.RepeatedField<Exemplar> exemplars, ulong time)
    {
        if (_rng.NextDouble() >= _profile.Metrics.ExemplarFraction) return;
        var exemplar = new Exemplar
        {
            TimeUnixNano = time, AsDouble = _rng.NextDouble() * 100,
            TraceId = Topology.RandomId(_rng, 16), SpanId = Topology.RandomId(_rng, 8)
        };
        exemplar.FilteredAttributes.Add(Topology.Str("exemplar.kind", "sample"));
        exemplars.Add(exemplar);
    }
}
