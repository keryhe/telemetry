namespace Keryhe.Telemetry.TestDataGenerator.Model;

public enum SimSpanKind { Internal, Server, Client, Producer, Consumer }

public enum SimStatus { Unset, Ok, Error }

public enum SimSeverity { Debug, Info, Warn, Error }

/// <summary>One running copy of a service: a pod. Identity is what OTel calls the resource.</summary>
public sealed record ServiceInstance(
    string Tenant,
    string Service,
    string Version,
    string PodName,
    string InstanceId,
    string HostName,
    string Region,
    string Namespace,
    string Environment);

public sealed class SimEvent
{
    public required DateTimeOffset Time { get; init; }
    public required string Name { get; init; }
    public Tags Attributes { get; init; } = [];
}

/// <summary>A span link; the target is held by reference so a sink can resolve it however it addresses spans.</summary>
public sealed class SimLink
{
    public required SimSpan Target { get; init; }
    public Tags Attributes { get; init; } = [];
}

public sealed class SimLog
{
    public required DateTimeOffset Time { get; init; }
    public required SimSeverity Severity { get; init; }
    public required string Body { get; init; }
    public required ServiceInstance Instance { get; init; }

    /// <summary>The logger category, which becomes the instrumentation scope name.</summary>
    public required string Category { get; init; }
    public Tags Attributes { get; init; } = [];

    /// <summary>Set for a log written while a span was current; null for background logs.</summary>
    public SimSpan? Span { get; init; }
}

/// <summary>One observation of a histogram or counter, taken while <see cref="SimSpan"/> was current.</summary>
public sealed class SimMeasurement
{
    public required string Instrument { get; init; }
    public required double Value { get; init; }
    public Tags Attributes { get; init; } = [];
}

/// <summary>A periodic reading of a gauge or up-down counter (cpu, memory, pool size).</summary>
public sealed class SimSample
{
    public required ServiceInstance Instance { get; init; }
    public required string Instrument { get; init; }
    public required double Value { get; init; }
    public required DateTimeOffset Time { get; init; }
    public Tags Attributes { get; init; } = [];
}

public sealed class SimSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required ServiceInstance Instance { get; init; }
    public required string Name { get; set; }
    public required SimSpanKind Kind { get; init; }
    public required DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public SimStatus Status { get; set; } = SimStatus.Unset;
    public string? StatusMessage { get; set; }
    public Tags Attributes { get; } = [];
    public List<SimEvent> Events { get; } = [];
    public List<SimLink> Links { get; } = [];
    public List<SimLog> Logs { get; } = [];
    public List<SimMeasurement> Measurements { get; } = [];
    public List<SimSpan> Children { get; } = [];

    public IEnumerable<SimSpan> Descendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.Descendants())
                yield return d;
    }
}

/// <summary>Everything a tenant produced for a stretch of virtual time.</summary>
public sealed class SimChunk
{
    public required string Tenant { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    /// <summary>Root spans of every trace that started in the window (including queue-consumer traces).</summary>
    public required IReadOnlyList<SimSpan> Traces { get; init; }

    /// <summary>Logs not written inside any span (startup, heartbeat, configuration reload).</summary>
    public required IReadOnlyList<SimLog> BackgroundLogs { get; init; }

    /// <summary>Gauge and up-down readings taken at <see cref="To"/>; empty when no reading is due.</summary>
    public required IReadOnlyList<SimSample> Samples { get; init; }

    public IEnumerable<SimSpan> AllSpans() => Traces.SelectMany(t => t.Descendants());
}
