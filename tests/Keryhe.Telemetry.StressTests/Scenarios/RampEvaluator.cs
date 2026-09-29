using Keryhe.Telemetry.StressTests.Load;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>What one ramp step measured, in the terms the stop criteria are written in.</summary>
/// <param name="TraceLagsMs">Ingest-to-queryable lag of each marker probe sent during the step, in send order; null = never became visible.</param>
/// <param name="LogLagsMs">The same for the log marker, as measured (including the <c>asOf</c> pin offset).</param>
/// <param name="LogPinOffsetMs">
/// The provider's declared <c>asOf</c> back-off (<c>GET /api/capabilities</c>'s <c>asOfBackoffSeconds</c>): the log probe reads through the
/// pinned list page, so every log lag carries this constant. It is subtracted before any criterion is applied. The trace probe is not pinned.
/// </param>
public sealed record StepMeasurements(
    IReadOnlyList<WindowSummary> Windows, double RecordsDropped, double GateWaitP95Ms,
    IReadOnlyList<double?> TraceLagsMs, IReadOnlyList<double?> LogLagsMs, double LogPinOffsetMs = 0);

/// <summary>Applies <see cref="RampCriteria"/> to one step. Pure, so the stop rules are tested without a run.</summary>
public static class RampEvaluator
{
    public const string Dropped = "records_dropped", GateWait = "gate_wait_p95", ExportP99 = "export_p99", ErrorRate = "grpc_error_rate",
        LagGrowth = "lag_growth", LagAbsolute = "lag_absolute";

    /// <summary>The criteria that hold for the step, empty when it sustained.</summary>
    public static IReadOnlyList<string> Tripped(StepMeasurements m, RampCriteria c)
    {
        var tripped = new List<string>();
        if (m.RecordsDropped > c.MaxDroppedRecords) tripped.Add(Dropped);
        if (m.GateWaitP95Ms > c.MaxGateWaitP95Ms) tripped.Add(GateWait);
        if (m.Windows.Any(w => w.Latency.P99Ms > c.MaxExportP99Seconds * 1000)) tripped.Add(ExportP99);
        if (m.Windows.Any(w => w.ErrorRatePercent > c.MaxErrorRatePercent)) tripped.Add(ErrorRate);
        var logLags = AdjustForPin(m.LogLagsMs, m.LogPinOffsetMs);
        if (LagGrows(m.TraceLagsMs, c) || LagGrows(logLags, c)) tripped.Add(LagGrowth);
        if (LagTooHigh(m.TraceLagsMs, c) || LagTooHigh(logLags, c)) tripped.Add(LagAbsolute);
        return tripped;
    }

    /// <summary>Subtracts the constant <c>asOf</c> pin offset from each lag, never below zero; a probe that never appeared stays null.</summary>
    public static IReadOnlyList<double?> AdjustForPin(IReadOnlyList<double?> lags, double offsetMs) =>
        offsetMs <= 0 ? lags : lags.Select(l => l is { } v ? Math.Max(0, v - offsetMs) : (double?)null).ToList();

    /// <summary>
    /// Whether the last third of a step's (pin-adjusted) probes averaged more than <see cref="RampCriteria.MaxLagSeconds"/>: data is taking too long
    /// to become queryable even if the lag is steady rather than growing. Probes that never appeared are left to <see cref="LagGrows"/>.
    /// </summary>
    public static bool LagTooHigh(IReadOnlyList<double?> lags, RampCriteria c)
    {
        if (c.MaxLagSeconds <= 0 || lags.Count < 4) return false;
        var last = lags.Skip(lags.Count - lags.Count / 3).Where(l => l is not null).Select(l => l!.Value).ToList();
        return last.Count > 0 && last.Average() > c.MaxLagSeconds * 1000;
    }

    /// <summary>
    /// Whether a lag series keeps growing instead of plateauing: comparing the mean of the last third of probes with the first
    /// third's. A probe that never appeared in the last third counts as growth outright; fewer than 4 probes is too few to judge.
    /// </summary>
    public static bool LagGrows(IReadOnlyList<double?> lags, RampCriteria c)
    {
        if (lags.Count < 4) return false;
        var third = lags.Count / 3;
        var first = lags.Take(third).ToList();
        var last = lags.Skip(lags.Count - third).ToList();
        if (last.Any(l => l is null)) return true;

        var firstMean = first.Where(l => l is not null).Select(l => l!.Value).DefaultIfEmpty(0).Average();
        var lastMean = last.Select(l => l!.Value).Average();
        return lastMean > firstMean * c.LagGrowthFactor && lastMean - firstMean > c.LagGrowthMinMs;
    }
}
