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
    private const string OldestOrder = "mat-button-toggle[value='oldest'] button";
    private const string NewestOrder = "mat-button-toggle[value='newest'] button";

    public static IReadOnlyList<TourStep> Build(string window, TourOptions options, TourData data, long tenantId)
    {
        // Tenant pages live under t/{tenantId}/ (a tenant-less URL would redirect there and cost every step a hop).
        var t = $"t/{tenantId}/";
        string Range(string extra = "") => $"range={window}{extra}";
        string Q(string text) => $"&q={Uri.EscapeDataString(text)}";
        var steps = new List<TourStep>
        {
            new("dashboard", "dashboard", [Cards, Chart], $"{t}dashboard?{Range()}"),

            new("traces", "traces", [Cards, Rows, Chart], $"{t}traces?{Range()}"),
            new("traces:errors", "traces", [Cards, Rows], $"{t}traces?{Range("&mode=errors")}"),
            new("traces:slow", "traces", [Cards, Rows], $"{t}traces?{Range("&mode=slow")}"),
            new("traces:search", "traces", [Cards, Rows], $"{t}traces?{Range(Q(options.FreeText))}"),
            new("traces:attribute-search", "traces", [Cards, Rows], $"{t}traces?{Range(Q(options.KeyValue))}"),
        };

        steps.Add(Navigate("traces:list", "traces", $"{t}traces?{Range()}", [Cards, Rows]));
        // The capped list shows one end of the window; the toggle reads the other end, and back.
        steps.Add(Click("traces:oldest", "traces", OldestOrder, [Rows]));
        steps.Add(Click("traces:newest", "traces", NewestOrder, [Rows]));
        steps.Add(new("trace-detail", "trace-detail", ["app-trace-waterfall, .waterfall, svg, mat-card"], Act: OpenFirstRow));

        steps.Add(new("metrics", "metrics", [Cards, Rows], $"{t}metrics?{Range()}"));
        if (data.Service is not null)
            steps.Add(new("metrics:service", "metrics", [Cards, Rows], $"{t}metrics?{Range($"&service={Uri.EscapeDataString(data.Service)}")}"));
        // Metric detail is reached by clicking through the list, never by deep link: OTel metric names contain dots,
        // and a hard load of /metrics/<dotted.name> is answered 404 by the UI host's "nonfile" fallback route.
        if (data.HistogramMetric is not null)
        {
            steps.AddRange(Detail("histogram", data.HistogramMetric, Range, t));
            steps.Add(Click("metric-detail:histogram-group-all", "metric-detail", "mat-button-toggle:has-text('All') button", [Chart], requiresApi: false));
        }
        if (data.SumMetric is not null)
            steps.AddRange(Detail("sum", data.SumMetric, Range, t));

        steps.Add(new("logs", "logs", [Cards, Rows], $"{t}logs?{Range()}"));
        steps.Add(new("logs:min-severity", "logs", [Cards, Rows], $"{t}logs?{Range("&severity=13")}"));
        steps.Add(new("logs:search", "logs", [Cards, Rows], $"{t}logs?{Range(Q(options.FreeText))}"));
        steps.Add(new("logs:attribute-search", "logs", [Cards, Rows], $"{t}logs?{Range(Q(options.KeyValue))}"));
        steps.Add(Navigate("logs:list", "logs", $"{t}logs?{Range()}", [Cards, Rows]));
        // The fields sidebar starts collapsed: open it, then open the first field's values.
        steps.Add(Click("logs:facets-open", "logs", "button:has(.fields-toggle-icon)", [".facet-key, .facet-empty"], requiresApi: false));
        steps.Add(Click("logs:facet-values", "logs", ".facet-key", [".facet-value"], requiresApi: false));
        steps.Add(Click("logs:oldest", "logs", OldestOrder, [Rows]));
        steps.Add(Click("logs:newest", "logs", NewestOrder, [Rows]));
        steps.Add(Click("logs:context", "logs", "tr.log-row", ["tr.detail-row .ctx-val, tr.detail-row"], requiresApi: false));

        steps.Add(new("alerts", "alerts", ["mat-card, table, app-empty-state"], $"{t}alerts"));
        steps.Add(new("settings", "settings", ["mat-form-field"], "settings"));

        if (options.Export)
        {
            steps.Add(Navigate("export:traces-page", "traces", $"{t}traces?{Range()}", [Cards, Rows]));
            steps.Add(Export("export:traces"));
            steps.Add(Navigate("export:logs-page", "logs", $"{t}logs?{Range()}", [Cards, Rows]));
            steps.Add(Export("export:logs"));
        }
        return steps;
    }

    /// <summary>Filters the catalog to one metric, then opens it from the list, as a user would.</summary>
    private static IEnumerable<TourStep> Detail(string kind, string metric, Func<string, string> range, string t)
    {
        yield return new TourStep($"metrics:find-{kind}", "metrics", [Rows], $"{t}metrics?{range($"&q={Uri.EscapeDataString(metric)}")}");
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
