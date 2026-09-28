using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Orchestration;

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
    IReadOnlyList<double?> TraceLagsMs, IReadOnlyList<double?> LogLagsMs, IReadOnlyList<string> Tripped);

/// <summary>
/// <see cref="LastSustainedStep"/> is the last step that held with no criterion tripped: the provider's breaking point on this machine is the step after it.
/// It is null when the very first step already tripped. <see cref="TrippedStep"/> is null when the ramp reached its step limit without tripping anything.
/// </summary>
public sealed record RampResult(
    IReadOnlyList<RampStepResult> Steps, int? LastSustainedStep, int? TrippedStep, IReadOnlyList<string> TrippedCriteria, bool ReachedMaxSteps);

/// <summary>Everything one scenario measured (stress-test plan, Phase 6). Phase 7 adds the correctness check against <see cref="Load"/>'s ledger; Phase 8 folds these into the report.</summary>
public sealed record ScenarioResult(
    int SchemaVersion, string Provider, string Topology, string Profile, string Kind,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string? Error,
    ScenarioProfile ProfileUsed, PhaseMarkers Phases,
    LoadSnapshot? Load, IReadOnlyList<WindowSummary> MeasuredWindow, IReadOnlyList<MarkerResult> Markers,
    RampResult? Ramp, QuiesceResult? Quiesce,
    TourResults? Tour, string? BrowserError,
    DatabaseObservation? Database, IReadOnlyList<HostResult> Hosts)
{
    public const int CurrentSchemaVersion = 1;
}
