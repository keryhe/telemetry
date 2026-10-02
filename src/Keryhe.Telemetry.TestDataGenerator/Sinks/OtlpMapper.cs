using Google.Protobuf;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>Converts the simulation model into OTLP protobuf messages.</summary>
internal static class OtlpMapper
{
    public const string ScopeVersion = "1.0.0";

    private static readonly Dictionary<string, Resource> ResourceCache = [];

    public static ulong UnixNano(DateTimeOffset t) => (ulong)(t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100UL;

    public static ByteString Id(string hex) => ByteString.CopyFrom(Convert.FromHexString(hex));

    public static Resource ResourceOf(ServiceInstance instance)
    {
        lock (ResourceCache)
        {
            if (ResourceCache.TryGetValue(instance.PodName, out var cached)) return cached;
            var resource = new Resource();
            foreach (var kv in ResourceFactory.Build(instance).Attributes)
                resource.Attributes.Add(Kv(kv.Key, kv.Value));
            ResourceCache[instance.PodName] = resource;
            return resource;
        }
    }

    public static InstrumentationScope Scope(string name) => new() { Name = name, Version = ScopeVersion };

    public static KeyValue Kv(string key, object? value) => new() { Key = key, Value = Any(value) };

    public static AnyValue Any(object? value) => value switch
    {
        null => new AnyValue(),
        string s => new AnyValue { StringValue = s },
        bool b => new AnyValue { BoolValue = b },
        long l => new AnyValue { IntValue = l },
        int i => new AnyValue { IntValue = i },
        double d => new AnyValue { DoubleValue = d },
        _ => new AnyValue { StringValue = value.ToString() ?? "" },
    };

    public static void AddAttributes(Google.Protobuf.Collections.RepeatedField<KeyValue> target, IEnumerable<KeyValuePair<string, object?>> source)
    {
        foreach (var kv in source) target.Add(Kv(kv.Key, kv.Value));
    }

    // ---------------------------------------------------------------- spans

    public static Span ToSpan(SimSpan s)
    {
        var span = new Span
        {
            TraceId = Id(s.TraceId),
            SpanId = Id(s.SpanId),
            Name = s.Name,
            Kind = s.Kind switch
            {
                SimSpanKind.Server => Span.Types.SpanKind.Server,
                SimSpanKind.Client => Span.Types.SpanKind.Client,
                SimSpanKind.Producer => Span.Types.SpanKind.Producer,
                SimSpanKind.Consumer => Span.Types.SpanKind.Consumer,
                _ => Span.Types.SpanKind.Internal,
            },
            StartTimeUnixNano = UnixNano(s.Start),
            EndTimeUnixNano = UnixNano(s.End),
        };
        if (s.ParentSpanId is not null) span.ParentSpanId = Id(s.ParentSpanId);
        AddAttributes(span.Attributes, s.Attributes);
        foreach (var e in s.Events)
        {
            var ev = new Span.Types.Event { TimeUnixNano = UnixNano(e.Time), Name = e.Name };
            AddAttributes(ev.Attributes, e.Attributes);
            span.Events.Add(ev);
        }
        foreach (var l in s.Links)
        {
            var link = new Span.Types.Link { TraceId = Id(l.Target.TraceId), SpanId = Id(l.Target.SpanId) };
            AddAttributes(link.Attributes, l.Attributes);
            span.Links.Add(link);
        }
        if (s.Status != SimStatus.Unset)
        {
            span.Status = new Status
            {
                Code = s.Status == SimStatus.Error ? Status.Types.StatusCode.Error : Status.Types.StatusCode.Ok,
                Message = s.StatusMessage ?? "",
            };
        }
        return span;
    }

    // ---------------------------------------------------------------- logs

    public static LogRecord ToLog(SimLog l)
    {
        var record = new LogRecord
        {
            TimeUnixNano = UnixNano(l.Time),
            ObservedTimeUnixNano = UnixNano(l.Time),
            SeverityNumber = l.Severity switch
            {
                SimSeverity.Debug => SeverityNumber.Debug,
                SimSeverity.Warn => SeverityNumber.Warn,
                SimSeverity.Error => SeverityNumber.Error,
                _ => SeverityNumber.Info,
            },
            SeverityText = l.Severity switch
            {
                SimSeverity.Debug => "Debug",
                SimSeverity.Warn => "Warning",
                SimSeverity.Error => "Error",
                _ => "Information",
            },
            Body = new AnyValue { StringValue = l.Body },
        };
        AddAttributes(record.Attributes, l.Attributes);
        if (l.Span is not null)
        {
            record.TraceId = Id(l.Span.TraceId);
            record.SpanId = Id(l.Span.SpanId);
            record.Flags = 1; // sampled
        }
        return record;
    }
}
