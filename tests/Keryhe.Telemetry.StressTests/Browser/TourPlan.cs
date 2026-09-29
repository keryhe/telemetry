using Microsoft.Playwright;

namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>
/// One measured step. Exactly one of <see cref="RelativeUrl"/> (a full navigation, measured from navigation
/// start) or <see cref="Act"/> (an interaction on the current page, measured from the trigger) is set.
/// <see cref="Ready"/> is a list of CSS selectors; each is itself an OR-list (comma-separated), and the step
/// is ready when every one has a visible match and the page's <c>/api</c> requests have settled. An interaction that is
/// purely client-side (opening a sidebar, expanding a row) sets <see cref="RequiresApi"/> false, since it may issue no request at all.
/// </summary>
public sealed record TourStep(string Name, string Page, IReadOnlyList<string> Ready, string? RelativeUrl = null, Func<IPage, Task>? Act = null, bool RequiresApi = true);

/// <summary>What the plan needs to know about the data under test, discovered once per run through the API.</summary>
public sealed record TourData(string? Service, string? HistogramMetric, string? SumMetric);

/// <summary>Builds the ordered page tour for one time window (plan Phase 5, steps 1 to 10). Pure apart from the interaction lambdas.</summary>
public static class TourPlan
{
    private const string Cards = "app-stat-card";
    private const string Rows = "tr.mat-mdc-row, app-empty-state";
    private const string Chart = "apx-chart svg, app-empty-state";
    private const string NextPage = "button.mat-mdc-paginator-navigation-next:not([disabled])";
    private const int PagesToTurn = 3;

    public static IReadOnlyList<TourStep> Build(string window, TourOptions options, TourData data)
    {
        string Range(string extra = "") => $"range={window}{extra}";
        string Q(string text) => $"&q={Uri.EscapeDataString(text)}";
        var steps = new List<TourStep>
        {
            new("global", "global", [".tenant-card, app-empty-state"], $"global?{Range()}"),
            new("dashboard", "dashboard", [Cards, Chart], $"dashboard?{Range()}"),

            new("traces", "traces", [Cards, Rows, Chart], $"traces?{Range()}"),
            new("traces:errors", "traces", [Cards, Rows], $"traces?{Range("&mode=errors")}"),
            new("traces:slow", "traces", [Cards, Rows], $"traces?{Range("&mode=slow")}"),
            new("traces:search", "traces", [Cards, Rows], $"traces?{Range(Q(options.FreeText))}"),
            new("traces:attribute-search", "traces", [Cards, Rows], $"traces?{Range(Q(options.KeyValue))}"),
        };

        steps.Add(Navigate("traces:list", "traces", $"traces?{Range()}", [Cards, Rows]));
        for (var i = 1; i <= PagesToTurn; i++)
            steps.Add(Click($"traces:next-{i}", "traces", NextPage, [Rows]));
        steps.Add(new("trace-detail", "trace-detail", ["app-trace-waterfall, .waterfall, svg, mat-card"], Act: OpenFirstRow));

        steps.Add(new("metrics", "metrics", [Cards, Rows], $"metrics?{Range()}"));
        if (data.Service is not null)
            steps.Add(new("metrics:service", "metrics", [Cards, Rows], $"metrics?{Range($"&service={Uri.EscapeDataString(data.Service)}")}"));
        // Metric detail is reached by clicking through the list, never by deep link: OTel metric names contain dots,
        // and a hard load of /metrics/<dotted.name> is answered 404 by the UI host's "nonfile" fallback route.
        if (data.HistogramMetric is not null)
        {
            steps.AddRange(Detail("histogram", data.HistogramMetric, Range));
            steps.Add(Click("metric-detail:histogram-group-all", "metric-detail", "mat-button-toggle:has-text('All') button", [Chart], requiresApi: false));
        }
        if (data.SumMetric is not null)
            steps.AddRange(Detail("sum", data.SumMetric, Range));

        steps.Add(new("logs", "logs", [Cards, Rows], $"logs?{Range()}"));
        steps.Add(new("logs:min-severity", "logs", [Cards, Rows], $"logs?{Range("&severity=13")}"));
        steps.Add(new("logs:search", "logs", [Cards, Rows], $"logs?{Range(Q(options.FreeText))}"));
        steps.Add(new("logs:attribute-search", "logs", [Cards, Rows], $"logs?{Range(Q(options.KeyValue))}"));
        steps.Add(Navigate("logs:list", "logs", $"logs?{Range()}", [Cards, Rows]));
        // The fields sidebar starts collapsed: open it, then open the first field's values.
        steps.Add(Click("logs:facets-open", "logs", "button:has(.fields-toggle-icon)", [".facet-key, .facet-empty"], requiresApi: false));
        steps.Add(Click("logs:facet-values", "logs", ".facet-key", [".facet-value"], requiresApi: false));
        for (var i = 1; i <= PagesToTurn; i++)
            steps.Add(Click($"logs:next-{i}", "logs", NextPage, [Rows]));
        steps.Add(Click("logs:context", "logs", "tr.log-row", ["tr.detail-row .ctx-val, tr.detail-row"], requiresApi: false));

        steps.Add(new("alerts", "alerts", ["mat-card, table, app-empty-state"], "alerts"));
        steps.Add(new("settings", "settings", ["mat-form-field"], "settings"));

        if (options.Export)
        {
            steps.Add(Navigate("export:traces-page", "traces", $"traces?{Range()}", [Cards, Rows]));
            steps.Add(Export("export:traces"));
            steps.Add(Navigate("export:logs-page", "logs", $"logs?{Range()}", [Cards, Rows]));
            steps.Add(Export("export:logs"));
        }
        return steps;
    }

    /// <summary>Filters the catalog to one metric, then opens it from the list, as a user would.</summary>
    private static IEnumerable<TourStep> Detail(string kind, string metric, Func<string, string> range)
    {
        yield return new TourStep($"metrics:find-{kind}", "metrics", [Rows], $"metrics?{range($"&q={Uri.EscapeDataString(metric)}")}");
        yield return new TourStep($"metric-detail:{kind}", "metric-detail", [Cards, Chart],
            Act: p => p.Locator($"a.metric-link:text-is('{metric}')").First.ClickAsync());
    }

    private static TourStep Navigate(string name, string page, string url, string[] ready) => new(name, page, ready, url);

    private static TourStep Click(string name, string page, string selector, string[] ready, bool requiresApi = true) =>
        new(name, page, ready, Act: p => p.Locator(selector).First.ClickAsync(), RequiresApi: requiresApi);

    private static Task OpenFirstRow(IPage page) => page.Locator("tr.mat-mdc-row").First.ClickAsync();

    /// <summary>Opens the toolbar's download menu and starts the server-side NDJSON export, waiting for the download to finish.</summary>
    private static TourStep Export(string name) => new(name, "export", [],
        Act: async p =>
        {
            await p.Locator("mat-icon:text-is('download')").First.ClickAsync();
            var download = await p.RunAndWaitForDownloadAsync(
                () => p.Locator("button[mat-menu-item]:has-text('NDJSON')").First.ClickAsync(),
                new PageRunAndWaitForDownloadOptions { Timeout = 10 * 60_000 });
            await download.PathAsync(); // completes when the whole body has arrived
        });
}
