using Keryhe.Telemetry.TestDataGenerator;
using Keryhe.Telemetry.TestDataGenerator.Config;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

internal static class TestSupport
{
    /// <summary>A fixed Wednesday, so weekday and time-of-day assumptions are stable.</summary>
    public static readonly DateTimeOffset Day = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);

    public static GeneratorOptions Options(int seed = 7, double peak = 2, params IncidentOptions[] incidents) => new()
    {
        Seed = seed,
        PeakRequestsPerSecond = peak,
        Tenants = [new TenantOptions { Name = "acme-retail", ApiKey = "test-key", Scale = 1.0 }],
        Incidents = [.. incidents],
    };

    public static TenantSimulator Simulator(GeneratorOptions? options = null) =>
        new((options ?? Options()).Tenants[0], options ?? Options());

    /// <summary>Several minutes of evening traffic: enough requests to exercise every journey and some failures.</summary>
    public static SimChunkSet Evening(GeneratorOptions? options = null, int minutes = 10)
    {
        var sim = Simulator(options);
        var from = Day.AddHours(20);
        return new SimChunkSet(sim.Simulate(from, from.AddMinutes(minutes), includeSamples: true));
    }
}

internal sealed class SimChunkSet
{
    public SimChunkSet(Keryhe.Telemetry.TestDataGenerator.Model.SimChunk chunk)
    {
        Chunk = chunk;
        Spans = chunk.AllSpans().ToList();
    }

    public Keryhe.Telemetry.TestDataGenerator.Model.SimChunk Chunk { get; }
    public List<Keryhe.Telemetry.TestDataGenerator.Model.SimSpan> Spans { get; }
}
