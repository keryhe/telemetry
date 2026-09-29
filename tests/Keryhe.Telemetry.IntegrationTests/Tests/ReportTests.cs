using System.Text.Json;
using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Process;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Reporting;
using Keryhe.Telemetry.StressTests.Scenarios;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>The report (stress-test plan, Phase 8): chart maths, the analyzer's summaries, the JSON round trip, and that the HTML is self-contained.</summary>
public class ReportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0.0007, 0.001)]
    [InlineData(7, 10)]
    [InlineData(23, 25)]
    [InlineData(101, 200)]
    [InlineData(0, 1)]
    [InlineData(4900, 5000)]
    public void Axis_maximum_is_the_next_round_number(double max, double expected) =>
        Assert.Equal(expected, SvgChart.NiceMax(max), 9);

    [Fact]
    public void X_axis_ticks_stay_readable_from_a_minute_to_a_soak()
    {
        Assert.Equal(10, SvgChart.XStep(100));
        Assert.Equal(1800, SvgChart.XStep(4 * 3600 + 900));
        Assert.All(new[] { 30d, 600, 4 * 3600 + 900 }, t => Assert.InRange(t / SvgChart.XStep(t), 1, 12));
        Assert.Equal("1:05", SvgChart.Clock(65));
        Assert.Equal("4:00:00", SvgChart.Clock(4 * 3600));
    }

    [Fact]
    public void Long_lines_are_thinned_and_peaks_survive_when_asked()
    {
        var points = Enumerable.Range(0, 14400).Select(i => new SeriesPoint(i, i == 7000 ? 900 : 10)).ToList();
        var mean = SvgChart.Thin(points, 900, 14400, peak: false);
        var peak = SvgChart.Thin(points, 900, 14400, peak: true);
        Assert.InRange(peak.Count, 100, 1200);
        Assert.Equal(900, peak.Max(p => p.V));
        Assert.True(mean.Max(p => p.V) < 900);
        // Short lines are left alone.
        var short_ = points.Take(50).ToList();
        Assert.Same(short_, SvgChart.Thin(short_, 900, 50, false));
    }

    [Fact]
    public void Figure_is_inline_svg_with_markers_a_legend_and_escaped_text()
    {
        var series = new[] { new NamedSeries("g", "a<b>", "ms", [new(0, 1), new(10, 5), new(20, 3)]) };
        var svg = SvgChart.Figure("Lag & more", "ms", series, 30, [new(15, "sweep", "retention sweep: 5 rows")]);
        Assert.Contains("<svg", svg);
        Assert.Contains("polyline", svg);
        Assert.Contains("marker sweep", svg);
        Assert.Contains("retention sweep: 5 rows", svg);
        Assert.Contains("Lag &amp; more", svg);
        Assert.Contains("a&lt;b&gt;", svg);
        Assert.DoesNotContain("<script", svg);
        Assert.Contains("No data recorded", SvgChart.Figure("Empty", "ms", [new("g", "x", "ms", [])], 30, []));
    }

    [Theory]
    [InlineData("api/Traces/{traceId}/spans", "traces/{}/spans")]
    [InlineData("traces/{id}/spans", "traces/{}/spans")]
    [InlineData("api/metrics/by-name/{name}", "metrics/by-name/{}")]
    [InlineData("/api/Logs/page?x=1", "logs/page")]
    public void Server_routes_and_client_templates_meet_on_one_key(string route, string key) =>
        Assert.Equal(key, ScenarioAnalyzer.EndpointKey(route));

    [Fact]
    public void Screenshots_are_linked_relative_to_the_scenario_folder()
    {
        Assert.Equal("screenshots/u0-global.png", ScenarioAnalyzer.RelativeScreenshot("/Users/x/stress-results/run/pg-allinone-smoke/screenshots/u0-global.png"));
        Assert.Null(ScenarioAnalyzer.RelativeScreenshot(null));
    }

    private static MetricSample Hist(string instrument, int sec, double count, double p95, string? signal = "traces", string? route = null)
    {
        var tags = new Dictionary<string, string>();
        if (signal is not null) tags["signal"] = signal;
        if (route is not null) tags["http.route"] = route;
        return new MetricSample(T0.AddSeconds(sec), "m", instrument, "histogram", tags, p95 / 2, count, null, p95 / 2, p95, p95 * 1.5);
    }

    [Fact]
    public void Histogram_quantiles_are_weighted_by_how_many_observations_each_second_covered()
    {
        var w = ScenarioAnalyzer.Weighted("gate_wait", "traces", [Hist("h", 1, 90, 10), Hist("h", 2, 10, 110)]);
        Assert.Equal(100, w.Count);
        Assert.Equal(20, w.P95Ms, 6); // (90*10 + 10*110) / 100
    }

    [Fact]
    public void Write_side_counts_only_the_measured_window_and_skips_empty_intervals()
    {
        var samples = new Dictionary<HostRole, IReadOnlyList<MetricSample>>
        {
            [HostRole.AllInOne] = [Hist("keryhe.telemetry.ingestion.gate_wait", 1, 10, 500), Hist("keryhe.telemetry.ingestion.gate_wait", 20, 10, 4), Hist("keryhe.telemetry.ingestion.gate_wait", 21, 0, 999)]
        };
        var stats = ScenarioAnalyzer.WriteSide(samples, T0.AddSeconds(10), T0.AddSeconds(30));
        var gate = Assert.Single(stats);
        Assert.Equal((10L, 4d), (gate.Count, gate.P95Ms));
    }

    private static PageResult Page(string step, double? ready, params ApiRequestRecord[] requests) =>
        new(0, 1, step, "p", "1h", T0, ready, ready is null, null, null, requests, []);

    [Fact]
    public void Endpoints_pair_client_timings_with_the_servers_for_the_same_route()
    {
        var tour = new TourResults([Page("a", 100,
            new(T0, "GET", "traces/{id}/spans", 200, 80, 10, null, null, null, false),
            new(T0, "GET", "traces/{id}/spans", 200, 120, 10, null, null, null, false),
            new(T0, "GET", "logs/summary", 400, 5, 10, "raw", null, null, false))], 1, []);
        var samples = new Dictionary<HostRole, IReadOnlyList<MetricSample>>
        {
            [HostRole.Api] = [Hist("http.server.request.duration", 15, 2, 0.1, null, "api/Traces/{traceId}/spans")]
        };
        var stats = ScenarioAnalyzer.Endpoints(tour, samples, T0, T0.AddSeconds(60));

        var spans = stats.Single(e => e.Template == "traces/{id}/spans");
        Assert.Equal((2, 2L), (spans.Calls, spans.ServerCalls));
        Assert.Equal(100, spans.ServerP95Ms!.Value, 6); // seconds to milliseconds
        var logs = stats.Single(e => e.Template == "logs/summary");
        Assert.Equal((1, 1, null), (logs.Status400, logs.Raw, logs.ServerP95Ms));
    }

    [Fact]
    public void Blocking_chains_group_repeated_pairs_and_rank_by_worst_wait()
    {
        var wait = new Keryhe.Telemetry.StressTests.Observers.Database.LockWait("2", "1", "UPDATE b", "UPDATE a", "tenants", "Lock", 500);
        var db = new Keryhe.Telemetry.StressTests.Observers.Database.DatabaseObservation("PostgreSQL",
            [new(T0, [wait], [], []), new(T0.AddSeconds(1), [wait with { WaitMs = 1500 }], [], [])],
            new(0, new Dictionary<string, double>(), [], [], new Dictionary<string, string>(), []),
            new("x", [], []), [], []);
        var chain = Assert.Single(ScenarioAnalyzer.BlockingChains(db));
        Assert.Equal((2, 1500d), (chain.Samples, chain.MaxWaitMs));
    }

    private static ScenarioResult Scenario(string provider = "PostgreSQL", string profile = "smoke", RampResult? ramp = null) =>
        new(ScenarioResult.CurrentSchemaVersion, provider, "AllInOne", profile, ramp is null ? "fixed" : "ramp",
            T0, T0.AddMinutes(3), null, new ScenarioProfile { Name = profile },
            new PhaseMarkers(T0, T0.AddSeconds(60), T0.AddSeconds(120), T0.AddSeconds(120), T0.AddSeconds(130)),
            null, [new WindowSummary("traces", 60, 30000, 29000, 0, 0, 0, 300, 0, 0, new LatencySummary(300, 5, 4, 8, 12, 20))],
            [new MarkerResult(0, T0.AddSeconds(70), 5000, 30, null)], ramp, new QuiesceResult(true, 10, 180, 0), null, null, null, null, []);

    [Fact]
    public void Ramp_headline_is_the_last_sustained_step_not_the_step_that_tripped()
    {
        WindowSummary W(double acked) => new("traces", 60, 60000, (long)(acked * 60), 0, 0, 0, 100, 0, 0, new LatencySummary(100, 1, 1, 1, 1, 1));
        var steps = new[]
        {
            new RampStepResult(0, 1, T0, T0.AddSeconds(60), [W(1000)], 0, 0, [], [], []),
            new RampStepResult(1, 2, T0.AddSeconds(60), T0.AddSeconds(120), [W(1900)], 0, 0, [], [], []),
            new RampStepResult(2, 3, T0.AddSeconds(120), T0.AddSeconds(180), [W(2100)], 5, 300, [], [], ["records_dropped"]),
        };
        var report = ScenarioAnalyzer.Analyze("x", Scenario(profile: "ramp", ramp: new RampResult(steps, 1, 2, ["records_dropped"], false)) with { MeasuredWindow = [] }, new Dictionary<HostRole, IReadOnlyList<MetricSample>>());
        Assert.Equal(1900, report.Headline.Signals.Single().AckedPerSecond, 3);
        Assert.Equal((1, 2d), (report.Headline.RampLastSustainedStep, report.Headline.RampLastSustainedScale));
        Assert.Contains(report.Markers, m => m.Kind == "step" && m.Label.Contains("x3"));
    }

    [Fact]
    public void A_scenario_result_survives_the_json_round_trip_the_report_depends_on()
    {
        var original = Scenario();
        var json = JsonSerializer.Serialize(original, ResultJson.Options);
        var back = JsonSerializer.Deserialize<ScenarioResult>(json, ResultJson.Options)!;
        Assert.Equal((original.Provider, original.Phases.MeasuredStart), (back.Provider, back.Phases.MeasuredStart));
        Assert.Equal(original.MeasuredWindow[0].Latency.P99Ms, back.MeasuredWindow[0].Latency.P99Ms);
        Assert.Equal(5000, back.Markers[0].LogLagMs);
        Assert.Null(back.Markers[0].Error);
    }

    [Fact]
    public async Task Building_a_report_from_a_folder_writes_self_contained_outputs_and_a_comparison_for_a_matrix()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stress-report-{Guid.NewGuid():N}");
        try
        {
            foreach (var provider in new[] { "PostgreSQL", "SqlServer" })
            {
                var folder = Path.Combine(dir, provider.ToLowerInvariant() + "-allinone-smoke");
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "scenario.json"), JsonSerializer.Serialize(Scenario(provider), ResultJson.Options));
            }

            var written = await ReportBuilder.BuildAsync(dir);
            Assert.Equal(["result.json", "report.html", "comparison.html"], written.Select(Path.GetFileName));

            var html = await File.ReadAllTextAsync(Path.Combine(dir, "report.html"));
            Assert.Contains("PostgreSQL / AllInOne / smoke", html);
            Assert.Contains("SqlServer / AllInOne / smoke", html);
            Assert.DoesNotContain("<script", html);
            Assert.DoesNotContain("http://", html.Replace("http://www.w3.org", ""));
            Assert.DoesNotContain("https://", html);

            var result = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(dir, "result.json")), ResultJson.Options)!;
            Assert.Equal(RunResult.CurrentSchemaVersion, result.SchemaVersion);
            Assert.Equal(2, result.Scenarios.Count);

            var comparison = await File.ReadAllTextAsync(Path.Combine(dir, "comparison.html"));
            Assert.Contains("Achieved ingest rate", comparison);
            Assert.Contains("<th>AllInOne</th>", comparison);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
