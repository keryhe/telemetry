using Keryhe.Telemetry.StressTests.Load;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>What one ramp step measured, in the terms the stop criteria are written in.</summary>
/// <param name="TraceLagsMs">Ingest-to-queryable lag of each marker probe sent during the step, in send order; null = never became visible.</param>
public sealed record StepMeasurements(
    IReadOnlyList<WindowSummary> Windows, double RecordsDropped, double GateWaitP95Ms,
    IReadOnlyList<double?> TraceLagsMs, IReadOnlyList<double?> LogLagsMs);

/// <summary>Applies <see cref="RampCriteria"/> to one step. Pure, so the stop rules are tested without a run.</summary>
public static class RampEvaluator
{
    public const string Dropped = "records_dropped", GateWait = "gate_wait_p95", ExportP99 = "export_p99", ErrorRate = "grpc_error_rate", LagGrowth = "lag_growth";

    /// <summary>The criteria that hold for the step, empty when it sustained.</summary>
    public static IReadOnlyList<string> Tripped(StepMeasurements m, RampCriteria c)
    {
        var tripped = new List<string>();
        if (m.RecordsDropped > c.MaxDroppedRecords) tripped.Add(Dropped);
        if (m.GateWaitP95Ms > c.MaxGateWaitP95Ms) tripped.Add(GateWait);
        if (m.Windows.Any(w => w.Latency.P99Ms > c.MaxExportP99Seconds * 1000)) tripped.Add(ExportP99);
        if (m.Windows.Any(w => w.ErrorRatePercent > c.MaxErrorRatePercent)) tripped.Add(ErrorRate);
        if (LagGrows(m.TraceLagsMs, c) || LagGrows(m.LogLagsMs, c)) tripped.Add(LagGrowth);
        return tripped;
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
