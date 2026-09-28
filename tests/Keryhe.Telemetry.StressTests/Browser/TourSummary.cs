namespace Keryhe.Telemetry.StressTests.Browser;

public sealed record StepSummary(string Step, int Runs, int Timeouts, int Errors, double? P50Ms, double? P95Ms, double? MaxMs);

public sealed record RequestSummary(string Template, int Calls, int Failed, int Status400, double P50Ms, double P95Ms, double MaxMs, string? Sources);

/// <summary>Per-step ready times and per-endpoint API latencies over a tour's results. Numbers only, no verdicts.</summary>
public static class TourSummary
{
    public static IReadOnlyList<StepSummary> Steps(IEnumerable<PageResult> results) =>
        results.GroupBy(r => r.Step).Select(g =>
        {
            var ready = g.Where(r => r.ReadyMs is not null).Select(r => r.ReadyMs!.Value).OrderBy(v => v).ToList();
            return new StepSummary(g.Key, g.Count(), g.Count(r => r.TimedOut), g.Count(r => r.Error is not null),
                ready.Count == 0 ? null : Percentile(ready, 0.5), ready.Count == 0 ? null : Percentile(ready, 0.95), ready.Count == 0 ? null : ready[^1]);
        }).OrderByDescending(s => s.P95Ms ?? double.MaxValue).ToList();

    /// <summary>
    /// One row per endpoint template. A <c>400</c> is counted apart from failures: on the standard tier it is the
    /// documented answer to a search outside the raw-search window, not an error.
    /// </summary>
    public static IReadOnlyList<RequestSummary> Requests(IEnumerable<PageResult> results) =>
        results.SelectMany(r => r.Requests).GroupBy(r => r.Template).Select(g =>
        {
            var done = g.Where(r => !r.Failed).Select(r => r.DurationMs).OrderBy(v => v).ToList();
            var sources = g.Where(r => r.Source is not null).GroupBy(r => r.Source!).Select(s => $"{s.Key} {s.Count()}");
            return new RequestSummary(g.Key, g.Count(), g.Count(r => r.Failed), g.Count(r => r.Status == 400),
                done.Count == 0 ? 0 : Percentile(done, 0.5), done.Count == 0 ? 0 : Percentile(done, 0.95), done.Count == 0 ? 0 : done[^1],
                sources.Any() ? string.Join(", ", sources) : null);
        }).OrderByDescending(s => s.P95Ms).ToList();

    /// <summary>Nearest-rank percentile over an ascending list.</summary>
    public static double Percentile(IReadOnlyList<double> ascending, double p) =>
        ascending[Math.Clamp((int)Math.Ceiling(p * ascending.Count) - 1, 0, ascending.Count - 1)];
}
