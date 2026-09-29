using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Scenarios;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>What the numbers must be read against (stress-test plan, Phase 8): the code, the machine, Docker's VM and the images.</summary>
public sealed record RunMetadata(
    DateTimeOffset CreatedAt, string? GitSha, bool GitDirty, string Os, int Cpus, long MemoryBytes, string DotNet,
    int? DockerCpus, long? DockerMemoryBytes, string? DockerVersion,
    IReadOnlyDictionary<string, string> ContainerImages, string CommandLine);

/// <summary>Seconds since the scenario's warm-up began, so every chart in a scenario shares one x-axis.</summary>
public sealed record SeriesPoint(double T, double V);

/// <summary>One line on a chart. <see cref="Group"/> says which chart it belongs to.</summary>
public sealed record NamedSeries(string Group, string Name, string Unit, IReadOnlyList<SeriesPoint> Points);

/// <summary>A vertical marker on every timeline: warm-up end, a retention sweep, a ramp step, the end of the measured window.</summary>
public sealed record TimelineMarker(double T, string Kind, string Label);

public sealed record SignalHeadline(
    string Signal, double OfferedPerSecond, double AckedPerSecond, long NotSentRecords, long FailedExports,
    double ExportP50Ms, double ExportP95Ms, double ExportP99Ms);

/// <summary>A server-side histogram over the measured window. The quantiles are the count-weighted mean of the per-second quantiles, so they are approximate.</summary>
public sealed record HistogramStat(string Name, string Signal, long Count, double MeanMs, double P50Ms, double P95Ms, double P99Ms);

public sealed record PageStat(string Step, string Page, int Runs, int Timeouts, int Errors, double? P50Ms, double? P95Ms, double? P99Ms, double? MaxMs);

/// <summary>One API endpoint as the browser saw it (client) and as the host recorded it (server). Server figures are null when the host reported none.</summary>
public sealed record EndpointStat(
    string Template, int Calls, int Errors, int Status400,
    double ClientP50Ms, double ClientP95Ms, double ClientP99Ms,
    double? ServerP50Ms, double? ServerP95Ms, double? ServerP99Ms, long ServerCalls,
    int Rollup, int Raw);

public sealed record SlowPageLoad(string Step, string Window, int User, double? ReadyMs, bool TimedOut, string? Error, string? Screenshot);

/// <summary>A pair of statements seen blocking each other, aggregated over the run's 1s samples.</summary>
public sealed record BlockingChain(string? BlockingQuery, string? BlockedQuery, string? Resource, int Samples, double MaxWaitMs);

public sealed record RetentionSummary(int Sweeps, long SpanRows, long DataPointRows, long LogRows, long MaxElapsedMs);

public sealed record Headline(
    IReadOnlyList<SignalHeadline> Signals, double RecordsDropped, long Deadlocks, double LockWaitSeconds, int LockWaits,
    int? CorrectnessMismatches, string? BackdatedOutcome, RetentionSummary Retention,
    int? RampLastSustainedStep, double? RampLastSustainedScale, IReadOnlyList<string> RampTripped, bool RampReachedMax,
    double? LogLagP50Ms, double? TraceLagP50Ms, int MarkersTimedOut,
    double GateWaitP95Ms, double? PageReadyP95Ms, int BrowserTimeouts,
    double DbPeakCpuCores, double DbPeakMemoryMb, bool QuiesceReached, bool DrainCompleted);

public sealed record ScenarioReport(
    string Id, ScenarioResult Scenario, Headline Headline,
    IReadOnlyList<HistogramStat> WriteSide, IReadOnlyList<PageStat> Pages, IReadOnlyList<EndpointStat> Endpoints,
    IReadOnlyList<SlowPageLoad> SlowestLoads, IReadOnlyList<BlockingChain> BlockingChains,
    IReadOnlyList<NamedSeries> Series, IReadOnlyList<TimelineMarker> Markers, double TimelineSeconds);

/// <summary>The whole run: the full <c>result.json</c>. <see cref="SchemaVersion"/> changes when the shape does, so later runs can be compared by tooling.</summary>
public sealed record RunResult(int SchemaVersion, RunMetadata? Metadata, IReadOnlyList<ScenarioReport> Scenarios)
{
    /// <summary>2: scenarios carry the database-performance plan's Phase 0 additions (see <c>ScenarioResult.CurrentSchemaVersion</c>).</summary>
    public const int CurrentSchemaVersion = 2;
}
