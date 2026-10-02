using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>
/// The OpenTelemetry SDK for one pod, set up as a real service would: its own resource, tracer, meter and
/// logger providers, each with an OTLP/gRPC exporter authenticated as the tenant.
/// </summary>
internal sealed class PodTelemetry : IDisposable
{
    private readonly ServiceInstance _instance;
    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Meter _meter;
    private readonly Dictionary<string, object> _recorders = [];
    private readonly Dictionary<string, ILogger> _loggers = [];
    private readonly ConcurrentDictionary<(string Instrument, string AttrKey), SimSample> _samples = new();

    public PodTelemetry(ServiceInstance instance, Uri endpoint, string apiKey, int metricIntervalMs)
    {
        _instance = instance;
        // One source/meter name per pod and tenant: a provider listens by name, so a shared name would
        // make every pod's provider export every pod's data.
        var scopeName = instance.PodName;
        Source = new ActivitySource(scopeName, OtlpMapper.ScopeVersion);
        _meter = new Meter(scopeName, OtlpMapper.ScopeVersion);
        var resource = ResourceFactory.Builder(instance);
        var headers = $"Authorization=Bearer {apiKey}";

        void Configure(OtlpExporterOptions o)
        {
            o.Endpoint = endpoint;
            o.Headers = headers;
            o.Protocol = OtlpExportProtocol.Grpc;
        }

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(scopeName)
            .AddOtlpExporter(Configure)
            .Build();

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(scopeName)
            // Exemplars are off by default in the SDK. Trace-based ones carry the trace/span id of the span
            // current when a measurement was recorded, which is what the UI links to.
            .SetExemplarFilter(ExemplarFilterType.TraceBased);
        foreach (var def in Instruments.All)
        {
            if (def.Kind == InstrumentKind.HistogramExplicit)
                meterBuilder.AddView(def.Name, new ExplicitBucketHistogramConfiguration { Boundaries = def.Boundaries });
            else if (def.Kind == InstrumentKind.HistogramExponential)
                meterBuilder.AddView(def.Name, new Base2ExponentialBucketHistogramConfiguration());
        }
        _meterProvider = meterBuilder
            .AddOtlpExporter((exporter, reader) =>
            {
                Configure(exporter);
                reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = metricIntervalMs;
            })
            .Build();

        _loggerFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(resource);
                o.IncludeFormattedMessage = true;
                // Must come before the exporter: it rewrites the record's timestamp to the simulated time.
                o.AddProcessor(new SimulatedTimeProcessor());
                o.AddOtlpExporter(Configure);
            });
        });

        CreateInstruments();
    }

    public ActivitySource Source { get; }

    private void CreateInstruments()
    {
        foreach (var def in Instruments.All)
        {
            switch (def.Kind)
            {
                case InstrumentKind.HistogramExplicit:
                case InstrumentKind.HistogramExponential:
                    _recorders[def.Name] = _meter.CreateHistogram<double>(def.Name, def.Unit, def.Description);
                    break;
                case InstrumentKind.Counter:
                    _recorders[def.Name] = _meter.CreateCounter<long>(def.Name, def.Unit, def.Description);
                    break;
                case InstrumentKind.Gauge:
                    _meter.CreateObservableGauge(def.Name, () => Observe(def.Name), def.Unit, def.Description);
                    break;
                case InstrumentKind.UpDownSum:
                    _meter.CreateObservableUpDownCounter(def.Name, () => Observe(def.Name), def.Unit, def.Description);
                    break;
            }
        }
    }

    private IEnumerable<Measurement<double>> Observe(string instrument)
    {
        foreach (var ((name, _), sample) in _samples)
        {
            if (name == instrument) yield return new Measurement<double>(sample.Value, ToTagList(sample.Attributes));
        }
    }

    public void SetSample(SimSample sample) => _samples[(sample.Instrument, sample.Attributes.Key())] = sample;

    public void Record(SimMeasurement m)
    {
        var tags = ToTagList(m.Attributes);
        switch (_recorders[m.Instrument])
        {
            case Histogram<double> h: h.Record(m.Value, tags); break;
            case Counter<long> c: c.Add((long)m.Value, tags); break;
        }
    }

    public void Log(SimLog log)
    {
        if (!_loggers.TryGetValue(log.Category, out var logger))
            _loggers[log.Category] = logger = _loggerFactory.CreateLogger(log.Category);

        var level = log.Severity switch
        {
            SimSeverity.Debug => LogLevel.Debug,
            SimSeverity.Warn => LogLevel.Warning,
            SimSeverity.Error => LogLevel.Error,
            _ => LogLevel.Information,
        };
        SimulatedTimeProcessor.Current = log.Time.UtcDateTime;
        try
        {
            logger.Log(level, default, new LogState(log), null, static (state, _) => state.Body);
        }
        finally
        {
            SimulatedTimeProcessor.Current = null;
        }
    }

    private static TagList ToTagList(Tags tags)
    {
        var list = new TagList();
        foreach (var kv in tags) list.Add(kv.Key, kv.Value);
        return list;
    }

    public void Dispose()
    {
        _tracerProvider.ForceFlush(10_000);
        _meterProvider.ForceFlush(10_000);
        _tracerProvider.Dispose();
        _meterProvider.Dispose();
        _loggerFactory.Dispose();
        _meter.Dispose();
        Source.Dispose();
    }

    /// <summary>The log's attributes as the logger state; the formatter supplies the body.</summary>
    private sealed class LogState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly List<KeyValuePair<string, object?>> _items;

        public LogState(SimLog log)
        {
            Body = log.Body;
            _items = [.. log.Attributes];
        }

        public string Body { get; }
        public int Count => _items.Count;
        public KeyValuePair<string, object?> this[int index] => _items[index];
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public override string ToString() => Body;
    }

    /// <summary>
    /// ILogger stamps a record with the wall clock when it is written. The simulator writes a request's logs
    /// moments after it "happened", so the record is re-stamped with the simulated time, and the trace and span
    /// ids it picked up from the current Activity are left as they are.
    /// </summary>
    private sealed class SimulatedTimeProcessor : BaseProcessor<LogRecord>
    {
        [ThreadStatic] private static DateTime? _current;

        public static DateTime? Current
        {
            get => _current;
            set => _current = value;
        }

        public override void OnEnd(LogRecord data)
        {
            if (_current is { } t) data.Timestamp = t;
        }
    }
}
