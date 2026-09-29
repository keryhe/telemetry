using System.Text;
using static Keryhe.Telemetry.StressTests.Reporting.HtmlReportWriter;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>
/// The cross-provider comparison page of a matrix run (stress-test plan, Phase 8): for each profile, one table per headline metric with providers as rows and
/// topologies as columns. It ranks nothing and judges nothing; it puts the numbers side by side.
/// </summary>
public static class ComparisonWriter
{
    private sealed record Metric(string Title, string Unit, Func<ScenarioReport, string> Value);

    private static string Sum(ScenarioReport s, Func<SignalHeadline, double> f) => N(s.Headline.Signals.Sum(f));
    private static string Worst(ScenarioReport s, Func<SignalHeadline, double> f) => s.Headline.Signals.Count == 0 ? "n/a" : N(s.Headline.Signals.Max(f));

    private static readonly Metric[] Metrics =
    [
        new("Achieved ingest rate", "records/s, all signals", s => Sum(s, x => x.AckedPerSecond)),
        new("Offered ingest rate", "records/s, all signals", s => Sum(s, x => x.OfferedPerSecond)),
        new("Export latency p99 (worst signal)", "ms", s => Worst(s, x => x.ExportP99Ms)),
        new("Gate wait p95", "ms", s => N(s.Headline.GateWaitP95Ms, "N1")),
        new("Ingest-to-queryable lag p50, log", "ms", s => N(s.Headline.LogLagP50Ms)),
        new("Ingest-to-queryable lag p50, trace", "ms", s => N(s.Headline.TraceLagP50Ms)),
        new("Page ready p95", "ms", s => N(s.Headline.PageReadyP95Ms)),
        new("Browser timeouts", "count", s => N(s.Headline.BrowserTimeouts)),
        new("Records dropped", "count", s => N(s.Headline.RecordsDropped)),
        new("Deadlocks", "count", s => N(s.Headline.Deadlocks)),
        new("Lock waits sampled", "count", s => N(s.Headline.LockWaits)),
        new("Database peak CPU", "cores", s => N(s.Headline.DbPeakCpuCores, "N2")),
        new("Database peak memory", "MB", s => N(s.Headline.DbPeakMemoryMb)),
        new("Correctness mismatches", "cells", s => s.Headline.CorrectnessMismatches is { } m ? N(m) : "n/a"),
        new("Retention sweep, slowest", "ms", s => s.Headline.Retention.Sweeps == 0 ? "none" : N(s.Headline.Retention.MaxElapsedMs)),
        new("Ramp breaking point", "last sustained scale; criterion that tripped", s =>
            s.Scenario.Ramp is null ? "" :
            $"{(s.Headline.RampLastSustainedScale is { } x ? "x" + N(x, "0.##") : "none")}" + (s.Headline.RampReachedMax ? " (step limit, none tripped)" : $" ({string.Join(", ", s.Headline.RampTripped)})")),
    ];

    public static string Write(RunResult run)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Cross-provider comparison</h1><p class=\"sub\">Providers as rows, topologies as columns, per profile. Numbers only. <a href=\"report.html\">Full report</a>.</p>");
        foreach (var profile in run.Scenarios.GroupBy(s => (s.Scenario.Profile, s.Scenario.Kind)))
        {
            var topologies = profile.Select(s => s.Scenario.Topology).Distinct().OrderBy(t => t).ToList();
            var providers = profile.Select(s => s.Scenario.Provider).Distinct().ToList();
            sb.Append("<h2>Profile: ").Append(E(profile.Key.Profile)).Append(" <span class=\"muted\">(").Append(E(profile.Key.Kind)).Append(")</span></h2>");
            foreach (var metric in Metrics)
            {
                if (metric.Title.StartsWith("Ramp") && profile.Key.Kind != "ramp") continue;
                sb.Append("<h3>").Append(E(metric.Title)).Append(" <span class=\"muted\">").Append(E(metric.Unit)).Append("</span></h3><table><thead><tr><th class=\"l\">Provider</th>");
                foreach (var t in topologies) sb.Append("<th>").Append(E(t)).Append("</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var p in providers)
                {
                    sb.Append("<tr><td class=\"l\">").Append(E(p)).Append("</td>");
                    foreach (var t in topologies)
                    {
                        var s = profile.FirstOrDefault(x => x.Scenario.Provider == p && x.Scenario.Topology == t);
                        sb.Append("<td>");
                        if (s is null) sb.Append("&ndash;");
                        else if (s.Scenario.Error is not null && s.Scenario.Load is null) sb.Append("<span class=\"bad\">failed</span>");
                        else sb.Append("<a href=\"report.html#").Append(E(s.Id)).Append("\">").Append(E(metric.Value(s))).Append("</a>");
                        sb.Append("</td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
        }
        return Page("Cross-provider comparison", sb.ToString());
    }
}
