using Keryhe.Telemetry.StressTests.Browser;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>The headless tour's pure logic (stress-test plan, Phase 5): URL templating, response markers, the step plan and its summaries. Needs no browser.</summary>
public class BrowserTourTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5188/api/traces/summary?start=1&end=2", "traces/summary")]
    [InlineData("http://h/telemetry/../api/traces/4bf92f3577b34da6a3ce929d0e0e4736/spans", "traces/{id}/spans")]
    [InlineData("http://h/api/metrics/by-name/http.server.duration", "metrics/by-name/{name}")]
    [InlineData("http://h/api/metrics/labels/http.server.duration?x=1", "metrics/labels/{name}")]
    [InlineData("http://h/api/logs/context?id=7", "logs/context")]
    [InlineData("/api/alerts/rules/42", "alerts/rules/{n}")]
    [InlineData("http://h/api/tenants/3/traces/summary?start=1", "traces/summary")]
    [InlineData("/api/tenants/12/alerts/rules/42", "alerts/rules/{n}")]
    [InlineData("/api/tenants", "tenants")]
    public void Api_urls_become_stable_templates(string url, string template) =>
        Assert.Equal(template, ApiRequestNormalizer.Template(url));

    [Fact]
    public void Only_api_urls_are_captured()
    {
        Assert.True(ApiRequestNormalizer.IsApi("http://h/api/capabilities"));
        Assert.False(ApiRequestNormalizer.IsApi("http://h/telemetry/main-abc.js"));
        Assert.False(ApiRequestNormalizer.IsApi("http://h/telemetry/config.json"));
    }

    [Fact]
    public void Summary_markers_are_read_case_insensitively_and_tolerate_other_bodies()
    {
        Assert.Equal(("rollup", true, false), ApiRequestNormalizer.Markers("{\"Source\":\"rollup\",\"totalIsLowerBound\":true,\"timedOut\":false,\"x\":1}"));
        Assert.Equal((null, null, null), ApiRequestNormalizer.Markers("[1,2,3]"));
        Assert.Equal((null, null, null), ApiRequestNormalizer.Markers("not json"));
        Assert.True(ApiRequestNormalizer.CarriesMarkers("logs/summary"));
        Assert.True(ApiRequestNormalizer.CarriesMarkers("metrics/series"));
        Assert.False(ApiRequestNormalizer.CarriesMarkers("capabilities"));
    }

    private static readonly TourData Full = new("checkout", "latency.ms", "requests.total");

    [Fact]
    public void Plan_covers_every_page_in_order_and_deep_links_the_window()
    {
        var steps = TourPlan.Build("6h", new TourOptions(), Full, 1);
        var names = steps.Select(s => s.Name).ToList();

        Assert.Equal("dashboard", names[0]);
        foreach (var page in new[] { "traces", "trace-detail", "metrics", "metric-detail", "logs", "alerts", "settings" })
            Assert.Contains(steps, s => s.Page == page);
        Assert.Equal(["traces:next-1", "traces:next-2", "traces:next-3"], names.Where(n => n.StartsWith("traces:next")));
        Assert.Equal(["logs:next-1", "logs:next-2", "logs:next-3"], names.Where(n => n.StartsWith("logs:next")));
        Assert.True(names.IndexOf("traces:next-1") > names.IndexOf("traces:list"));

        Assert.All(steps.Where(s => s.RelativeUrl is not null && s.Page is not ("alerts" or "settings")),
            s => Assert.Contains("range=6h", s.RelativeUrl));
        Assert.All(steps, s => Assert.True((s.RelativeUrl is null) ^ (s.Act is null), s.Name));
        Assert.Contains(steps, s => s.RelativeUrl == "t/1/traces?range=6h&mode=errors");
        Assert.Contains(steps, s => s.RelativeUrl == "t/1/traces?range=6h&mode=slow");
        Assert.Contains(steps, s => s.RelativeUrl == "t/1/logs?range=6h&severity=13");
        // Dotted metric names are never deep-linked (the UI host 404s them); the detail is opened from a filtered list.
        Assert.Contains(steps, s => s.RelativeUrl == "t/1/metrics?range=6h&q=latency.ms");
        Assert.DoesNotContain(steps, s => s.RelativeUrl?.StartsWith("t/1/metrics/") == true);
        Assert.Contains(steps, s => s.Name == "metric-detail:histogram" && s.Act is not null);
        Assert.Contains(steps, s => s.RelativeUrl == "t/1/traces?range=6h&q=attr.k0%3Av1");
    }

    [Fact]
    public void Exports_are_off_by_default_and_on_when_asked()
    {
        Assert.DoesNotContain(TourPlan.Build("1h", new TourOptions(), Full, 1), s => s.Name.StartsWith("export:"));
        var on = TourPlan.Build("1h", new TourOptions { Export = true }, Full, 1);
        Assert.Contains(on, s => s.Name == "export:traces");
        Assert.Contains(on, s => s.Name == "export:logs");
    }

    [Fact]
    public void Undiscovered_metrics_and_service_are_left_out_of_the_plan()
    {
        var names = TourPlan.Build("1h", new TourOptions(), new TourData(null, null, null), 1).Select(s => s.Name).ToList();
        Assert.DoesNotContain(names, n => n.StartsWith("metric-detail"));
        Assert.DoesNotContain("metrics:service", names);
        Assert.Contains("metrics", names);
    }

    private static PageResult Result(string step, double? ready, bool timedOut = false, params ApiRequestRecord[] requests) =>
        new(0, 1, step, "p", "1h", DateTimeOffset.UnixEpoch, ready, timedOut, null, null, requests, []);

    private static ApiRequestRecord Call(string template, double ms, int status = 200, string? source = null) =>
        new(DateTimeOffset.UnixEpoch, "GET", template, status, ms, 10, source, null, null, Failed: false);

    [Fact]
    public void Step_summary_ranks_by_p95_and_counts_timeouts_apart_from_ready_times()
    {
        var results = new[]
        {
            Result("fast", 100), Result("fast", 120),
            Result("slow", 900), Result("slow", 1500), Result("slow", null, timedOut: true),
        };
        var summary = TourSummary.Steps(results);
        Assert.Equal("slow", summary[0].Step);
        Assert.Equal((3, 1, 1500d), (summary[0].Runs, summary[0].Timeouts, summary[0].MaxMs!.Value));
        Assert.Equal(120, summary[1].P95Ms);
    }

    [Fact]
    public void Request_summary_groups_by_template_and_keeps_400s_out_of_failures()
    {
        var results = new[]
        {
            Result("a", 1, false, Call("logs/summary", 50, source: "rollup"), Call("logs/summary", 250, source: "raw"), Call("logs/page", 20, status: 400)),
        };
        var summary = TourSummary.Requests(results);
        var logs = Assert.Single(summary, s => s.Template == "logs/summary");
        Assert.Equal((2, 250d, "rollup 1, raw 1"), (logs.Calls, logs.MaxMs, logs.Sources));
        var page = Assert.Single(summary, s => s.Template == "logs/page");
        Assert.Equal((0, 1), (page.Failed, page.Status400));
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        double[] v = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(50, TourSummary.Percentile(v, 0.5));
        Assert.Equal(100, TourSummary.Percentile(v, 0.95));
        Assert.Equal(10, TourSummary.Percentile(v, 0));
    }
}
