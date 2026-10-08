using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Verification;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>
/// Wall-clock markers that every timeline in the report aligns on. Samples taken before <see cref="MeasuredStart"/> are warm-up and
/// excluded from headline numbers; <see cref="LoadStopped"/> to <see cref="QuiesceEnd"/> is the quiesce wait.
/// </summary>
public sealed record PhaseMarkers(
    DateTimeOffset? WarmupStart, DateTimeOffset? MeasuredStart, DateTimeOffset? MeasuredEnd,
    DateTimeOffset? LoadStopped, DateTimeOffset? QuiesceEnd);

public sealed record HostResult(
    HostRole Role, int Pid, HostLogSummary Log, ShutdownResult Shutdown, string LogFile, string? MetricsFile);

public sealed record RampStepResult(
    int Step, double Scale, DateTimeOffset Start, DateTimeOffset End,
    IReadOnlyList<WindowSummary> Windows, double RecordsDropped, double GateWaitP95Ms,
    IReadOnlyList<double?> TraceLagsMs, IReadOnlyList<double?> LogLagsMs, IReadOnlyList<string> Tripped, double CommitLagP95Ms = 0);

/// <summary>
/// <see cref="LastSustainedStep"/> is the last step that held with no criterion tripped: the provider's breaking point on this machine is the step after it.
/// It is null when the very first step already tripped. <see cref="TrippedStep"/> is null when the ramp reached its step limit without tripping anything.
/// </summary>
public sealed record RampResult(
    IReadOnlyList<RampStepResult> Steps, int? LastSustainedStep, int? TrippedStep, IReadOnlyList<string> TrippedCriteria, bool ReachedMaxSteps);

/// <summary>
/// The database stopped answering during the run (it refused connections after the ramp, from the harness's observers and the collector
/// alike). That is a finding about the database under overload, not a failure of the harness: <see cref="ContainerStatus"/>,
/// <see cref="ExitCode"/> and <see cref="OomKilled"/> are what Docker reported, and the DB-dependent steps after it (the database
/// observation and the correctness check) were skipped. Everything measured before it stands.
/// </summary>
public sealed record DatabaseOutage(string Message, string ContainerStatus, int? ExitCode, bool? OomKilled);

/// <summary>Everything one scenario measured (stress-test plan, Phase 6). <see cref="Correctness"/> compares <see cref="Load"/>'s ledger with the database; Phase 8 folds these into the report.</summary>
public sealed record ScenarioResult(
    int SchemaVersion, string Provider, string Topology, string Profile, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string? Error,
    ScenarioProfile ProfileUsed, PhaseMarkers Phases,
    LoadSnapshot? Load, IReadOnlyList<WindowSummary> MeasuredWindow, IReadOnlyList<MarkerResult> Markers,
    RampResult? Ramp, QuiesceResult? Quiesce,
    TourResults? Tour, string? BrowserError,
    DatabaseObservation? Database, CorrectnessResult? Correctness, IReadOnlyList<HostResult> Hosts,
    HistorySeedResult? Seed = null, IReadOnlyList<DetailProbeResult>? DetailProbes = null,
    DatabaseOutage? Outage = null)
{
    /// <summary>
    /// 2 (database-performance plan, Phase 0): pin-adjusted lag criteria and <c>lag_absolute</c>, maybe-landed export accounting, provider
    /// diagnostics and effective server settings. Ramp results from version 1 are not comparable with version 2.
    /// </summary>
    /// <summary>
    /// 3 (schema-simplification plan, Phase 1): ramp steps carry <c>commitLagP95Ms</c>, the profile carries <c>writeOnly</c>/<c>databaseCpuset</c>,
    /// and the span re-delivery ledger depends on the schema version under test.
    /// </summary>
    public const int CurrentSchemaVersion = 3;
}
