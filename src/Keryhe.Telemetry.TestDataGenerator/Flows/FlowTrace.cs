using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;

namespace Keryhe.Telemetry.TestDataGenerator.Flows;

/// <summary>What a tenant's flows need to know about the world: its pods and its incidents.</summary>
public sealed class SimEnvironment
{
    public required string Tenant { get; init; }
    public required GeneratorOptions Options { get; init; }
    public required InstanceRegistry Instances { get; init; }
    public required IncidentSchedule Incidents { get; init; }
}

/// <summary>
/// One trace under construction. A message published to a queue starts a second trace (the consumer's),
/// which is why roots are collected on a list shared by every trace of one user request.
/// </summary>
public sealed class FlowTrace
{
    public FlowTrace(SimEnvironment env, SimRandom rng, List<SimSpan> roots)
    {
        Env = env;
        Rng = rng;
        Roots = roots;
        TraceId = rng.TraceId();
    }

    public SimEnvironment Env { get; }
    public SimRandom Rng { get; }
    public string TraceId { get; }

    /// <summary>Every root span produced by the request (the user's trace plus any consumer traces).</summary>
    public List<SimSpan> Roots { get; }

    /// <summary>A new trace for work triggered by a message, sharing this request's random stream and root list.</summary>
    public FlowTrace NewTrace() => new(Env, Rng, Roots);
}
