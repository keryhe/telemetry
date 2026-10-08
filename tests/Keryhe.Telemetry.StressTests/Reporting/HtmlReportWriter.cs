using System.Globalization;
using System.Net;
using System.Text;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Scenarios;
using Keryhe.Telemetry.StressTests.Verification;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>
/// The self-contained <c>report.html</c> (stress-test plan, Phase 8): inline CSS, inline SVG charts, no scripts, nothing fetched. It states numbers only;
/// there is no pass/fail verdict (decision 6). Artifacts (logs, deadlock graphs, screenshots) are linked by relative path, so the folder travels as a unit.
/// </summary>
public static class HtmlReportWriter
{
    public static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    public static string N(double v, string format = "N0") => double.IsNaN(v) ? "n/a" : v.ToString(format, CultureInfo.InvariantCulture);
    public static string N(double? v, string format = "N0") => v is null ? "n/a" : N(v.Value, format);

    public const string Css = """
        :root { --bg:#fff; --fg:#1d232b; --muted:#5b6672; --line:#d8dde3; --card:#f6f8fa; --axis:#8a94a0; --grid:#e6eaee; --warn:#b54708; --bad:#b42318; }
        @media (prefers-color-scheme: dark) { :root { --bg:#12161b; --fg:#e4e8ec; --muted:#98a2ae; --line:#2c343d; --card:#1a2028; --axis:#6b7683; --grid:#252d36; --warn:#f0a35a; --bad:#f97066; } }
        body { margin:0; padding:24px 20px 60px; background:var(--bg); color:var(--fg); font:14px/1.45 system-ui,-apple-system,Segoe UI,sans-serif; }
        main { max-width:1040px; margin:0 auto; }
        h1 { font-size:24px; margin:0 0 4px; } h2 { font-size:19px; margin:34px 0 8px; padding-top:14px; border-top:2px solid var(--line); }
        h3 { font-size:15px; margin:22px 0 6px; } p.sub, .muted { color:var(--muted); }
        table { border-collapse:collapse; width:100%; margin:6px 0 12px; font-size:13px; }
        th, td { text-align:right; padding:4px 8px; border-bottom:1px solid var(--line); vertical-align:top; }
        th:first-child, td:first-child, td.l, th.l { text-align:left; } th { color:var(--muted); font-weight:600; }
        td.q { text-align:left; font-family:ui-monospace,Menlo,monospace; font-size:12px; word-break:break-word; max-width:420px; }
        .cards { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:8px; margin:10px 0; }
        .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:8px 10px; } .card b { display:block; font-size:18px; } .card span { color:var(--muted); font-size:12px; }
        .warn { color:var(--warn); } .bad { color:var(--bad); }
        figure.chart { margin:8px 0 14px; } figcaption { font-weight:600; margin-bottom:2px; } .unit { color:var(--muted); font-weight:400; }
        svg { width:100%; height:auto; display:block; } .grid { stroke:var(--grid); } .axis { stroke:var(--axis); } .tick { fill:var(--muted); font-size:10px; }
        .marker { stroke-width:1.2; } .marker.phase { stroke:var(--axis); } .marker.sweep { stroke:#f28e2b; stroke-dasharray:4 3; } .marker.step { stroke:#4e79a7; stroke-dasharray:1 3; }
        .legend { display:flex; flex-wrap:wrap; gap:4px 14px; font-size:12px; color:var(--muted); } .legend i { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:4px; }
        .keyline { font-size:12px; color:var(--muted); } pre { background:var(--card); border:1px solid var(--line); padding:8px; overflow:auto; font-size:12px; max-height:260px; }
        nav a { margin-right:14px; } .nodata { color:var(--muted); font-style:italic; margin:4px 0; }
        """;

    public static string Page(string title, string body) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{E(title)}</title><style>{Css}</style></head><body><main>{body}</main></body></html>";

    public static string Write(RunResult run)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>Stress test report</h1><p class=\"sub\">").Append(run.Scenarios.Count).Append(" scenario(s). Numbers only: no pass/fail verdict.");
        if (run.Scenarios.Count > 1) sb.Append(" <a href=\"comparison.html\">Cross-provider comparison</a>.");
        sb.Append("</p>");
        if (run.Metadata is { } m) Metadata(sb, m);

        sb.Append("<nav>");
        foreach (var s in run.Scenarios) sb.Append("<a href=\"#").Append(E(s.Id)).Append("\">").Append(E(s.Id)).Append("</a>");
        sb.Append("</nav>");

        foreach (var s in run.Scenarios) Scenario(sb, s, run);
        return Page("Stress test report", sb.ToString());
    }

    private static void Metadata(StringBuilder sb, RunMetadata m)
    {
        sb.Append("<h3>Run</h3><table><tbody>");
        void Row(string k, string? v) => sb.Append("<tr><td class=\"l\">").Append(E(k)).Append("</td><td class=\"l\">").Append(E(v)).Append("</td></tr>");
        Row("Created", m.CreatedAt.ToString("u"));
        Row("Git", m.GitSha is null ? "n/a" : m.GitSha[..Math.Min(12, m.GitSha.Length)] + (m.GitDirty ? " (uncommitted changes)" : ""));
        Row("Machine", $"{m.Os}, {m.Cpus} CPUs, {m.MemoryBytes / 1073741824.0:N1} GB");
        Row(".NET", m.DotNet);
        Row("Docker VM", m.DockerCpus is null ? "n/a" : $"{m.DockerCpus} CPUs, {(m.DockerMemoryBytes ?? 0) / 1073741824.0:N1} GB, Docker {m.DockerVersion}");
        Row("Images", string.Join("; ", m.ContainerImages.Select(kv => $"{kv.Key}: {kv.Value}")));
        Row("Command", m.CommandLine);
        sb.Append("</tbody></table><p class=\"muted\">One machine runs the database container, the hosts, the load tool and Chromium, so absolute numbers are specific to it; compare providers on the same machine.</p>");
    }

    private static void Scenario(StringBuilder sb, ScenarioReport s, RunResult run)
    {
        var r = s.Scenario;
        var h = s.Headline;
        sb.Append("<h2 id=\"").Append(E(s.Id)).Append("\">").Append(E(r.Provider)).Append(" / ").Append(E(r.Topology)).Append(" / ").Append(E(r.Profile)).Append(" <span class=\"muted\">(").Append(E(r.Kind)).Append(")</span></h2>");
        if (r.Error is not null) sb.Append("<p class=\"bad\">Scenario failed: ").Append(E(r.Error.Split('\n')[0])).Append("</p>");
        if (r.Outage is { } outage)
            sb.Append("<p class=\"bad\">The database stopped answering after the run: ").Append(E(outage.Message)).Append(" (container ").Append(E(outage.ContainerStatus))
              .Append(", exit ").Append(E(outage.ExitCode?.ToString() ?? "?")).Append(", OOM-killed ").Append(E(outage.OomKilled?.ToString() ?? "?"))
              .Append("). Everything measured before it stands; the database observation and the correctness check were skipped.</p>");

        if (r.ProfileUsed.WriteOnly) sb.Append("<p class=\"keyline\">Write-only run: no browsers and no marker probes, so nothing read the database. The ramp judged drops, gate wait, client export latency/errors and commit lag only.</p>");
        CpuSeparation(sb, s, run);
        Summary(sb, s);
        HistorySeed(sb, r);

        sb.Append("<h3>Timelines</h3><p class=\"keyline\">All charts share one x-axis (time since warm-up began). Vertical lines: grey = phase boundary, orange dashed = retention sweep, blue dotted = ramp step. Hover a line for its label.</p>");
        Charts(sb, s, "Write-side timelines", [
            ("client.throughput", "Client throughput: offered vs acked", false),
            ("client.latency", "Export latency (client-side)", true),
            ("lag", "Ingest-to-queryable lag", true),
            ("write.gate_wait", "Ingestion gate wait p95", true),
            ("write.flush_duration", "Flush duration p95 (successful)", true),
            ("write.commit_lag", "Commit lag p95 (enqueue to commit)", true),
            ("write.batch_size", "Flush batch size p95", false),
            ("write.resident", "Records resident in the ingestion queue", true),
            ("write.retries", "Flush retries and dropped records", true)]);
        Charts(sb, s, "Database timelines", [
            ("db.lock_waits", "Lock waits sampled", true),
            ("db.parts", "ClickHouse active parts", true),
            ("db.merges", "ClickHouse merges running", true),
            ("db.mutations", "ClickHouse mutations pending", true)]);
        Charts(sb, s, "Resource timelines", [
            ("db.container_cpu", "Database container CPU", true),
            ("db.container_memory", "Database container memory", false),
            ("host.cpu", "Host process CPU", true),
            ("host.memory", "Host process working set", false),
            ("host.gc_pause", "Host GC pause", true),
            ("host.threadpool_queue", "Host thread-pool queue length", true)]);

        WriteSideTables(sb, s);
        ReadSide(sb, s);
        DatabaseSection(sb, s);
        Artifacts(sb, s);
    }

    /// <summary>The history seed step and the trace-detail probe over what it sent (trace-list-detail-performance plan, Phase 0).</summary>
    private static void HistorySeed(StringBuilder sb, ScenarioResult r)
    {
        if (r.Seed is not { } seed) return;
        sb.Append("<h3>History seed</h3><p class=\"keyline\">").Append(seed.Days).Append(" day(s) of backdated traces at ").Append(seed.SpansPerDay.ToString("N0"))
          .Append(" spans/day, sent before the warm-up: ").Append(seed.SpansSent.ToString("N0")).Append(" spans accepted, ").Append(seed.SpansFailed.ToString("N0"))
          .Append(" failed, in ").Append(seed.Seconds.ToString("N0")).Append(" s. A run with history is not comparable with one without.</p>");
        if (r.DetailProbes is not { Count: > 0 } probes) return;
        sb.Append("<table><thead><tr><th class=\"l\">Seeded traces</th><th>Spans</th><th>Start hint</th><th>Runs</th><th>Errors</th><th>p50 ms</th><th>p95 ms</th><th>max ms</th><th>Avg KB</th></tr></thead><tbody>");
        foreach (var p in probes)
            sb.Append("<tr><td class=\"l\">").Append(E(p.Kind)).Append("</td><td>").Append(p.Spans.ToString("N0")).Append("</td><td>").Append(p.Hinted ? "yes" : "no")
              .Append("</td><td>").Append(p.Runs).Append("</td><td>").Append(p.Errors).Append("</td><td>").Append(p.P50Ms.ToString("N0")).Append("</td><td>")
              .Append(p.P95Ms.ToString("N0")).Append("</td><td>").Append(p.MaxMs.ToString("N0")).Append("</td><td>").Append((p.AvgBytes / 1024).ToString("N0")).Append("</td></tr>");
        sb.Append("</tbody></table><p class=\"muted\">Trace detail (<code>GET /api/traces/{id}/spans</code>) as the client saw it, after the run: the same traces without and with the time hint (<code>?start=&amp;end=</code>) the trace list passes.</p>");
    }

    /// <summary>What the database shared CPUs with (schema-simplification plan, Phase 1 item 6): the numbers past about x6-x8 mean nothing without it.</summary>
    private static void CpuSeparation(StringBuilder sb, ScenarioReport s, RunResult run)
    {
        var p = s.Scenario.ProfileUsed;
        var vm = run.Metadata?.DockerCpus is { } cpus ? $"the Docker VM's {cpus} CPUs" : "the Docker VM's CPUs";
        sb.Append("<p class=\"keyline\">CPU sharing: the database container is capped at ").Append(N(p.ContainerCpus, "0.##")).Append(" CPUs and ")
          .Append(p.DatabaseCpuset is { Length: > 0 } set ? $"pinned to cpuset <code>{E(set)}</code> of {vm} (the VM still shares the host's cores with the hosts, load tool and browsers, unless the database runs on another machine)"
                                                          : $"not pinned, so it competes for {vm} with whatever else runs there; the hosts, load tool and Chromium run on the host machine itself")
          .Append(".</p>");
    }

    private static void Charts(StringBuilder sb, ScenarioReport s, string heading, (string Group, string Title, bool Peak)[] charts)
    {
        sb.Append("<h3>").Append(E(heading)).Append("</h3>");
        foreach (var (group, title, peak) in charts)
        {
            var series = s.Series.Where(x => x.Group == group).ToList();
            if (series.Count == 0) continue;
            sb.Append(SvgChart.Figure(title, series[0].Unit, series, s.TimelineSeconds, s.Markers, peak));
        }
    }

    private static void Card(StringBuilder sb, string label, string value, string? cls = null) =>
        sb.Append("<div class=\"card\"><b").Append(cls is null ? "" : $" class=\"{cls}\"").Append('>').Append(E(value)).Append("</b><span>").Append(E(label)).Append("</span></div>");

    private static void Summary(StringBuilder sb, ScenarioReport s)
    {
        var h = s.Headline;
        sb.Append("<h3>Summary</h3><div class=\"cards\">");
        foreach (var sig in h.Signals) Card(sb, $"{sig.Signal} acked / offered per s", $"{N(sig.AckedPerSecond)} / {N(sig.OfferedPerSecond)}");
        Card(sb, "records dropped", N(h.RecordsDropped), h.RecordsDropped > 0 ? "bad" : null);
        Card(sb, "deadlocks", N(h.Deadlocks), h.Deadlocks > 0 ? "bad" : null);
        Card(sb, "lock-wait seconds (sampled)", N(h.LockWaitSeconds));
        Card(sb, "gate wait p95 (ms)", N(h.GateWaitP95Ms, "N1"));
        Card(sb, "commit lag p95 (ms)", N(h.CommitLagP95Ms, "N1"));
        Card(sb, "lag p50 log / trace (ms)", $"{N(h.LogLagP50Ms)} / {N(h.TraceLagP50Ms)}");
        Card(sb, "page ready p95 (ms)", N(h.PageReadyP95Ms));
        Card(sb, "browser timeouts", N(h.BrowserTimeouts), h.BrowserTimeouts > 0 ? "warn" : null);
        Card(sb, "DB peak CPU (cores) / memory (MB)", $"{N(h.DbPeakCpuCores, "N2")} / {N(h.DbPeakMemoryMb)}");
        Card(sb, "retention sweeps", $"{h.Retention.Sweeps} ({N(h.Retention.SpanRows + h.Retention.DataPointRows + h.Retention.LogRows)} rows)");
        Card(sb, "quiesce", h.QuiesceReached ? "reached" : "timed out", h.QuiesceReached ? null : "warn");
        Card(sb, "shutdown drain", h.DrainCompleted ? "completed" : "not completed", h.DrainCompleted ? null : "warn");
        sb.Append("</div>");

        if (s.Scenario.Correctness is { } c)
        {
            sb.Append("<p>Correctness: ").Append(c.Rows.Count(x => x.Status == CorrectnessStatus.Match)).Append(" of ").Append(c.Rows.Count).Append(" cells match, ")
              .Append(c.ExplainedByDrops).Append(" explained by drops, ").Append(c.ExplainedByAbandonedExports).Append(" by abandoned exports, <b class=\"").Append(c.Mismatches > 0 ? "bad" : "").Append("\">").Append(c.Mismatches).Append(" mismatched</b>. Backdated records: ")
              .Append(E(c.Backdated.Outcome)).Append(" (").Append(c.Backdated.RowsRemaining).Append(" rows remain)");
            if (c.PendingMergeDuplicates is { } p) sb.Append("; ").Append(p).Append(" span rows awaiting merge");
            sb.Append(".</p>");
            var bad = c.Rows.Where(x => x.Status == CorrectnessStatus.Mismatch).ToList();
            if (bad.Count > 0)
            {
                sb.Append("<table><thead><tr><th class=\"l\">Tenant</th><th class=\"l\">Table</th><th>Expected</th><th>Actual</th><th>Delta</th></tr></thead><tbody>");
                foreach (var x in bad) sb.Append("<tr><td class=\"l\">").Append(x.TenantId).Append("</td><td class=\"l\">").Append(E(x.Table)).Append("</td><td>").Append(N(x.Expected)).Append("</td><td>").Append(N(x.Actual)).Append("</td><td>").Append(x.Delta.ToString("+#;-#;0")).Append("</td></tr>");
                sb.Append("</tbody></table>");
            }
        }

        if (s.Scenario.Ramp is { } ramp)
        {
            sb.Append("<p>").Append(ramp.TrippedStep is { } t
                ? $"Ramp: last sustained step {(ramp.LastSustainedStep is { } l ? $"{l} (x{ramp.Steps[l].Scale:0.##})" : "none")}; step {t} (x{ramp.Steps[t].Scale:0.##}) tripped <b>{E(string.Join(", ", ramp.TrippedCriteria))}</b>. This is the breaking point on this machine."
                : $"Ramp: reached the step limit ({ramp.Steps.Count} steps, up to x{ramp.Steps[^1].Scale:0.##}) with no stop criterion tripped; no breaking point found.").Append("</p>");
            sb.Append("<p class=\"keyline\">Lag columns are the mean over the step's probes, which is what the lag criteria judge.</p>");
            sb.Append("<table><thead><tr><th>Step</th><th>Scale</th><th>Offered/s</th><th>Acked/s</th><th>Export p99 (ms)</th><th>Gate wait p95 (ms)</th><th>Commit lag p95 (ms)</th><th>Dropped</th><th>Log lag (ms)</th><th>Trace lag (ms)</th><th class=\"l\">Tripped</th></tr></thead><tbody>");
            foreach (var st in ramp.Steps)
                sb.Append("<tr><td>").Append(st.Step).Append("</td><td>x").Append(N(st.Scale, "0.##")).Append("</td><td>").Append(N(st.Windows.Sum(w => w.OfferedPerSecond))).Append("</td><td>").Append(N(st.Windows.Sum(w => w.AckedPerSecond)))
                  .Append("</td><td>").Append(N(st.Windows.Max(w => w.Latency.P99Ms))).Append("</td><td>").Append(N(st.GateWaitP95Ms, "N1")).Append("</td><td>").Append(N(st.CommitLagP95Ms, "N1")).Append("</td><td>").Append(N(st.RecordsDropped))
                  .Append("</td><td>").Append(MeanLag(st.LogLagsMs)).Append("</td><td>").Append(MeanLag(st.TraceLagsMs))
                  .Append("</td><td class=\"l\">").Append(E(string.Join(", ", st.Tripped))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        foreach (var check in s.Scenario.Database?.Locks.Checks ?? [])
            sb.Append("<p>").Append(E(check.Name)).Append(": <b class=\"").Append(check.Outcome == CheckOutcome.Failed ? "bad" : check.Outcome == CheckOutcome.NotChecked ? "warn" : "").Append("\">").Append(E(check.Label)).Append("</b> <span class=\"muted\">").Append(E(check.Detail)).Append("</span></p>");
        if (s.Scenario.BrowserError is not null) sb.Append("<p class=\"warn\">Browsers unavailable: ").Append(E(s.Scenario.BrowserError)).Append("</p>");
    }

    private static void WriteSideTables(StringBuilder sb, ScenarioReport s)
    {
        sb.Append("<h3>Write side</h3><table><thead><tr><th class=\"l\">Signal</th><th>Offered/s</th><th>Acked/s</th><th>Not sent</th><th>Failed exports</th><th>Export p50 (ms)</th><th>p95</th><th>p99</th></tr></thead><tbody>");
        foreach (var x in s.Headline.Signals)
            sb.Append("<tr><td class=\"l\">").Append(E(x.Signal)).Append("</td><td>").Append(N(x.OfferedPerSecond)).Append("</td><td>").Append(N(x.AckedPerSecond)).Append("</td><td>").Append(N(x.NotSentRecords)).Append("</td><td>").Append(N(x.FailedExports))
              .Append("</td><td>").Append(N(x.ExportP50Ms, "N1")).Append("</td><td>").Append(N(x.ExportP95Ms, "N1")).Append("</td><td>").Append(N(x.ExportP99Ms, "N1")).Append("</td></tr>");
        sb.Append("</tbody></table>");
        if (s.WriteSide.Count > 0)
        {
            sb.Append("<p class=\"keyline\">Server-side, measured window. Percentiles are the count-weighted mean of per-second quantiles (approximate). gate_wait and flush_duration are milliseconds; flush_batch_size is records.</p>");
            sb.Append("<table><thead><tr><th class=\"l\">Instrument</th><th class=\"l\">Signal</th><th>Count</th><th>Mean</th><th>p50</th><th>p95</th><th>p99</th></tr></thead><tbody>");
            foreach (var w in s.WriteSide)
                sb.Append("<tr><td class=\"l\">").Append(E(w.Name)).Append("</td><td class=\"l\">").Append(E(w.Signal)).Append("</td><td>").Append(N(w.Count)).Append("</td><td>").Append(N(w.MeanMs, "N2")).Append("</td><td>").Append(N(w.P50Ms, "N2"))
                  .Append("</td><td>").Append(N(w.P95Ms, "N2")).Append("</td><td>").Append(N(w.P99Ms, "N2")).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        var lost = s.Scenario.Hosts.SelectMany(x => x.Log.FlushRetries).Count();
        sb.Append("<p class=\"keyline\">Flush retries logged: ").Append(lost).Append("; dropped batches logged: ").Append(s.Scenario.Hosts.SelectMany(x => x.Log.BatchesDropped).Count()).Append(".</p>");
    }

    /// <summary>Every API route's server-side duration, from the host's own measurement, apart from the write instruments above.</summary>
    private static void ApiRoutes(StringBuilder sb, ScenarioReport s)
    {
        if (s.ApiRoutes is not { Count: > 0 } routes) return;
        sb.Append("<p class=\"keyline\">API routes, server-side (<code>http.server.request.duration</code>, measured window; approximate percentiles, in ms). Every caller counts: the browser tour and the marker probes.</p>");
        sb.Append("<table><thead><tr><th class=\"l\">Route</th><th>Calls</th><th>5xx</th><th>p50</th><th>p95</th><th>p99</th></tr></thead><tbody>");
        foreach (var r in routes)
            sb.Append("<tr><td class=\"l\">").Append(E(r.Route)).Append("</td><td>").Append(N(r.Calls)).Append("</td><td>").Append(N(r.Errors5xx)).Append("</td><td>").Append(N(r.P50Ms, "N1"))
              .Append("</td><td>").Append(N(r.P95Ms, "N1")).Append("</td><td>").Append(N(r.P99Ms, "N1")).Append("</td></tr>");
        sb.Append("</tbody></table>");
    }

    private static void ReadSide(StringBuilder sb, ScenarioReport s)
    {
        sb.Append("<h3>Read side</h3>");
        ApiRoutes(sb, s);
        if (s.Pages.Count == 0) { sb.Append("<p class=\"nodata\">No browser tour ran.</p>"); return; }
        sb.Append("<table><thead><tr><th class=\"l\">Page step</th><th>Runs</th><th>Timeouts</th><th>Errors</th><th>Ready p50 (ms)</th><th>p95</th><th>p99</th><th>max</th></tr></thead><tbody>");
        foreach (var p in s.Pages)
            sb.Append("<tr><td class=\"l\">").Append(E(p.Step)).Append("</td><td>").Append(p.Runs).Append("</td><td>").Append(p.Timeouts).Append("</td><td>").Append(p.Errors).Append("</td><td>").Append(N(p.P50Ms)).Append("</td><td>").Append(N(p.P95Ms)).Append("</td><td>").Append(N(p.P99Ms)).Append("</td><td>").Append(N(p.MaxMs)).Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<p class=\"keyline\">API endpoints. Client = the browser's request timing; server = the host's <code>http.server.request.duration</code> (approximate percentiles). The gap is network and browser time, but the server count also includes every other caller of the route, such as the marker probe's polling. A <code>400</code> is the documented answer to a search outside the standard tier's raw-search window, not an error.</p>");
        sb.Append("<table><thead><tr><th class=\"l\">Endpoint</th><th>Calls</th><th>Errors</th><th>400s</th><th>Client p50</th><th>p95</th><th>p99</th><th>Server p50</th><th>p95</th><th>p99</th><th>rollup / raw</th></tr></thead><tbody>");
        foreach (var e in s.Endpoints)
            sb.Append("<tr><td class=\"l\">").Append(E(e.Template)).Append("</td><td>").Append(e.Calls).Append("</td><td>").Append(e.Errors).Append("</td><td>").Append(e.Status400).Append("</td><td>").Append(N(e.ClientP50Ms)).Append("</td><td>").Append(N(e.ClientP95Ms)).Append("</td><td>").Append(N(e.ClientP99Ms))
              .Append("</td><td>").Append(N(e.ServerP50Ms)).Append("</td><td>").Append(N(e.ServerP95Ms)).Append("</td><td>").Append(N(e.ServerP99Ms)).Append("</td><td>").Append(e.Rollup + e.Raw == 0 ? "" : $"{e.Rollup} / {e.Raw}").Append("</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append("<h3>Slowest page loads</h3><table><thead><tr><th class=\"l\">Step</th><th class=\"l\">Window</th><th>User</th><th>Ready (ms)</th><th class=\"l\">Result</th></tr></thead><tbody>");
        foreach (var p in s.SlowestLoads)
        {
            sb.Append("<tr><td class=\"l\">").Append(E(p.Step)).Append("</td><td class=\"l\">").Append(E(p.Window)).Append("</td><td>").Append(p.User).Append("</td><td>").Append(N(p.ReadyMs)).Append("</td><td class=\"l\">");
            sb.Append(p.TimedOut ? "timed out" : p.Error is not null ? E(p.Error) : "ok");
            if (p.Screenshot is not null) sb.Append(" <a href=\"").Append(E(s.Id)).Append('/').Append(E(p.Screenshot)).Append("\">screenshot</a>");
            sb.Append("</td></tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static string Short(string? q, int max = 260)
    {
        if (string.IsNullOrWhiteSpace(q)) return "(idle or unknown)";
        var one = string.Join(' ', q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= max ? one : one[..max] + "...";
    }

    private static void DatabaseSection(StringBuilder sb, ScenarioReport s)
    {
        var db = s.Scenario.Database;
        sb.Append("<h3>Database</h3>");
        if (db is null) { sb.Append("<p class=\"nodata\">No database observations.</p>"); return; }

        sb.Append("<p>Counter deltas over the run: ").Append(E(string.Join(", ", db.Locks.CounterDeltas.Select(kv => $"{kv.Key} = {kv.Value:0}")))).Append(".</p>");

        if (s.BlockingChains.Count > 0)
        {
            sb.Append("<h3>Top blocking chains</h3><table><thead><tr><th class=\"l\">Blocker</th><th class=\"l\">Blocked</th><th class=\"l\">Resource</th><th>Samples</th><th>Max wait (ms)</th></tr></thead><tbody>");
            foreach (var c in s.BlockingChains)
                sb.Append("<tr><td class=\"q\">").Append(E(Short(c.BlockingQuery))).Append("</td><td class=\"q\">").Append(E(Short(c.BlockedQuery))).Append("</td><td class=\"l\">").Append(E(c.Resource)).Append("</td><td>").Append(c.Samples).Append("</td><td>").Append(N(c.MaxWaitMs)).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }
        else sb.Append("<p class=\"nodata\">No lock waits were sampled.</p>");

        sb.Append("<h3>Deadlocks</h3>");
        if (db.Locks.Deadlocks == 0) sb.Append("<p>None.</p>");
        else
        {
            sb.Append("<p><b class=\"bad\">").Append(db.Locks.Deadlocks).Append("</b> deadlock(s).");
            foreach (var name in db.Locks.Artifacts.Keys) sb.Append(" <a href=\"").Append(E(s.Id)).Append('/').Append(E(name)).Append("\">").Append(E(name)).Append("</a>");
            sb.Append("</p>");
            foreach (var d in db.Locks.DeadlockDetails.Take(5)) sb.Append("<pre>").Append(E(d.Length > 4000 ? d[..4000] + "\n..." : d)).Append("</pre>");
        }

        Statements(sb, "Top statements by total time", db.Statements.ByTotal, db.Statements.Source);
        Statements(sb, "Top statements by mean time (5 or more calls)", db.Statements.ByMean, db.Statements.Source);

        foreach (var d in db.Diagnostics ?? [])
        {
            sb.Append("<h3>").Append(E(d.Name)).Append("</h3>");
            if (d.Note is not null) sb.Append("<p class=\"keyline\">").Append(E(d.Note)).Append("</p>");
            if (d.Error is not null) { sb.Append("<p class=\"warn\">Not collected: ").Append(E(d.Error)).Append("</p>"); continue; }
            if (d.Rows.Count == 0) { sb.Append("<p class=\"nodata\">None.</p>"); continue; }
            sb.Append("<table><thead><tr>").Append(string.Concat(d.Columns.Select((c, i) => $"<th{(i == 0 ? " class=\"l\"" : "")}>{E(c)}</th>"))).Append("</tr></thead><tbody>");
            foreach (var row in d.Rows)
                sb.Append("<tr>").Append(string.Concat(row.Select((c, i) => i == 0 ? $"<td class=\"q\">{E(Short(c))}</td>" : $"<td>{E(c ?? "")}</td>"))).Append("</tr>");
            sb.Append("</tbody></table>");
        }

        if (db.Settings is { Count: > 0 } settings)
        {
            sb.Append("<h3>Effective server settings</h3><table><thead><tr><th class=\"l\">Setting</th><th class=\"l\">Value</th></tr></thead><tbody>");
            foreach (var x in settings)
                sb.Append("<tr><td class=\"l\">").Append(E(x.Name)).Append("</td><td class=\"q\">").Append(E(Short(x.Value))).Append("</td></tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h3>Tables at the end of the run</h3><table><thead><tr><th class=\"l\">Table</th><th>Rows</th><th>Size (MB)</th></tr></thead><tbody>");
        foreach (var t in db.Tables.OrderByDescending(t => t.Bytes ?? 0))
            sb.Append("<tr><td class=\"l\">").Append(E(t.Table)).Append("</td><td>").Append(t.Rows is null ? "n/a" : (t.RowsApproximate ? "~" : "") + N(t.Rows.Value)).Append("</td><td>").Append(N((t.Bytes ?? 0) / 1048576.0, "N1")).Append("</td></tr>");
        sb.Append("</tbody></table>");
    }

    private static string MeanLag(IReadOnlyList<double?> lags) =>
        lags.Any(l => l is not null) ? N(lags.Where(l => l is not null).Average(l => l!.Value)) : "n/a";

    private static void Statements(StringBuilder sb, string title, IReadOnlyList<StatementStat> stats, string source)
    {
        sb.Append("<h3>").Append(E(title)).Append(" <span class=\"muted\">(").Append(E(source)).Append(")</span></h3>");
        if (stats.Count == 0) { sb.Append("<p class=\"nodata\">None.</p>"); return; }
        sb.Append("<table><thead><tr><th class=\"l\">Statement</th><th>Calls</th><th>Total (ms)</th><th>Mean (ms)</th><th>Max (ms)</th><th>Rows</th></tr></thead><tbody>");
        foreach (var x in stats)
            sb.Append("<tr><td class=\"q\">").Append(E(Short(x.Query))).Append("</td><td>").Append(N(x.Calls)).Append("</td><td>").Append(N(x.TotalMs)).Append("</td><td>").Append(N(x.MeanMs, "N2")).Append("</td><td>").Append(N(x.MaxMs, "N1")).Append("</td><td>").Append(N(x.Rows)).Append("</td></tr>");
        sb.Append("</tbody></table>");
    }

    private static void Artifacts(StringBuilder sb, ScenarioReport s)
    {
        sb.Append("<h3>Artifacts</h3><p>");
        var links = new List<string> { "scenario.json", "container.log" };
        links.AddRange(s.Scenario.Hosts.Select(h => h.LogFile));
        links.AddRange(s.Scenario.Hosts.Where(h => h.MetricsFile is not null).Select(h => h.MetricsFile!));
        links.AddRange(s.Scenario.Database?.Locks.Artifacts.Keys ?? []);
        sb.Append(string.Join(" &middot; ", links.Distinct().Select(l => $"<a href=\"{E(s.Id)}/{E(l)}\">{E(l)}</a>")));
        sb.Append("</p>");
    }
}
