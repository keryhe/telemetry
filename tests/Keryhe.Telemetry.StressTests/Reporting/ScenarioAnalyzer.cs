using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Observers.Process;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Scenarios;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>Turns one scenario's raw results and its hosts' metric samples into the summaries, series and markers the report shows. Pure, so it is tested without a run.</summary>
public static class ScenarioAnalyzer
{
    private const string Ingestion = "keryhe.telemetry.ingestion.";
    private static readonly string[] Signals = ["traces", "logs", "metrics"];
    private static readonly char[] Sep = ['/'];

    public static ScenarioReport Analyze(string id, ScenarioResult r, IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hostSamples)
    {
        var t0 = r.Phases.WarmupStart ?? r.StartedAt;
        var from = r.Phases.MeasuredStart ?? t0;
        var to = r.Phases.MeasuredEnd ?? r.FinishedAt;
        double T(DateTimeOffset at) => (at - t0).TotalSeconds;

        var end = r.Phases.QuiesceEnd ?? r.Phases.LoadStopped ?? r.FinishedAt;
        var timelineSeconds = Math.Max(1, T(end));

        var series = BuildSeries(r, hostSamples, T);
        var markers = BuildMarkers(r, T);
        var write = WriteSide(hostSamples, from, to);
        var pages = PageStats(r.Tour);
        var endpoints = Endpoints(r.Tour, hostSamples, from, to);
        var chains = BlockingChains(r.Database);

        var headline = BuildHeadline(r, hostSamples, from, to, write, pages);
        return new ScenarioReport(id, r, headline, write, pages, endpoints, SlowestLoads(r.Tour), chains, series, markers, timelineSeconds);
    }

    // ---- headline -------------------------------------------------------------------------------------------------

    private static Headline BuildHeadline(
        ScenarioResult r, IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hostSamples,
        DateTimeOffset from, DateTimeOffset to, IReadOnlyList<HistogramStat> write, IReadOnlyList<PageStat> pages)
    {
        // A ramp has no single measured window: its headline is the last step that sustained (else the step that tripped).
        var windows = r.MeasuredWindow;
        if (r.Ramp is { Steps.Count: > 0 } ramp)
            windows = ramp.Steps[ramp.LastSustainedStep ?? ramp.TrippedStep ?? ramp.Steps.Count - 1].Windows;
        var signals = windows.Select(w => new SignalHeadline(w.Signal, w.OfferedPerSecond, w.AckedPerSecond, w.NotSentRecords, w.ExportsFailed,
            w.Latency.P50Ms, w.Latency.P95Ms, w.Latency.P99Ms)).ToList();

        var waits = r.Database?.LockSamples.SelectMany(s => s.Waits).ToList() ?? [];
        var sweeps = r.Hosts.SelectMany(h => h.Log.RetentionSweeps).ToList();
        var lagLog = Percentile(r.Markers.Where(m => m.LogLagMs is not null).Select(m => m.LogLagMs!.Value), 0.5);
        var lagTrace = Percentile(r.Markers.Where(m => m.TraceLagMs is not null).Select(m => m.TraceLagMs!.Value), 0.5);
        var ready = r.Tour?.Pages.Where(p => p.ReadyMs is not null).Select(p => p.ReadyMs!.Value).ToList() ?? [];

        // Sampled once a second, so each observed wait stands for about a second of waiting.
        var lockWaitSeconds = waits.Count * 1.0;

        return new Headline(
            signals, r.Hosts.Sum(h => h.Shutdown.RecordsDropped), r.Database?.Locks.Deadlocks ?? 0, lockWaitSeconds, waits.Count,
            r.Correctness?.Mismatches, r.Correctness?.Backdated.Outcome,
            new RetentionSummary(sweeps.Count, sweeps.Sum(s => s.SpanRows), sweeps.Sum(s => s.DataPointRows), sweeps.Sum(s => s.LogRows), sweeps.Count == 0 ? 0 : sweeps.Max(s => s.ElapsedMs)),
            r.Ramp?.LastSustainedStep, r.Ramp?.LastSustainedStep is { } s0 ? r.Ramp.Steps[s0].Scale : null,
            r.Ramp?.TrippedCriteria ?? [], r.Ramp?.ReachedMaxSteps ?? false,
            lagLog, lagTrace, r.Markers.Count(m => m.LogLagMs is null || m.TraceLagMs is null),
            write.Where(w => w.Name == "gate_wait").Select(w => w.P95Ms).DefaultIfEmpty(0).Max(),
            ready.Count == 0 ? null : Percentile(ready, 0.95), r.Tour?.Pages.Count(p => p.TimedOut) ?? 0,
            r.Database?.ContainerStats.Select(c => c.CpuCores).DefaultIfEmpty(0).Max() ?? 0,
            (r.Database?.ContainerStats.Select(c => c.MemoryBytes).DefaultIfEmpty(0).Max() ?? 0) / 1048576.0,
            r.Quiesce?.Reached ?? false, r.Hosts.Count > 0 && r.Hosts.All(h => h.Shutdown.DrainCompleted));
    }

    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    // ---- write side -----------------------------------------------------------------------------------------------

    private static IEnumerable<MetricSample> InWindow(IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hosts, string instrument, DateTimeOffset from, DateTimeOffset to) =>
        hosts.Values.SelectMany(s => s).Where(s => s.Instrument == instrument && s.At >= from && s.At <= to);

    public static IReadOnlyList<HistogramStat> WriteSide(IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hosts, DateTimeOffset from, DateTimeOffset to)
    {
        var stats = new List<HistogramStat>();
        foreach (var name in new[] { "gate_wait", "flush_duration", "flush_batch_size" })
            foreach (var signal in Signals)
            {
                var samples = InWindow(hosts, Ingestion + name, from, to)
                    .Where(s => s.Tags.GetValueOrDefault("signal") == signal && s.Count is > 0 && s.P95 is not null).ToList();
                if (samples.Count == 0) continue;
                stats.Add(Weighted(name, signal, samples));
            }
        return stats;
    }

    /// <summary>Count-weighted mean of the per-interval mean and quantiles. Exact for the mean, approximate for the percentiles.</summary>
    public static HistogramStat Weighted(string name, string signal, IReadOnlyList<MetricSample> samples)
    {
        var n = samples.Sum(s => s.Count!.Value);
        double W(Func<MetricSample, double?> f) => samples.Sum(s => (f(s) ?? 0) * s.Count!.Value) / n;
        return new HistogramStat(name, signal, (long)n, W(s => s.Value), W(s => s.P50), W(s => s.P95), W(s => s.P99));
    }

    // ---- read side ------------------------------------------------------------------------------------------------

    public static IReadOnlyList<PageStat> PageStats(TourResults? tour)
    {
        if (tour is null) return [];
        return tour.Pages.GroupBy(p => p.Step).Select(g =>
        {
            var ready = g.Where(p => p.ReadyMs is not null).Select(p => p.ReadyMs!.Value).OrderBy(v => v).ToList();
            double? Q(double q) => ready.Count == 0 ? null : TourSummary.Percentile(ready, q);
            return new PageStat(g.Key, g.First().Page, g.Count(), g.Count(p => p.TimedOut), g.Count(p => p.Error is not null), Q(0.5), Q(0.95), Q(0.99), ready.Count == 0 ? null : ready[^1]);
        }).OrderByDescending(p => p.P95Ms ?? double.MaxValue).ToList();
    }

    /// <summary><c>api/Traces/{traceId}/spans</c> and the browser's <c>traces/{id}/spans</c> become the same key.</summary>
    public static string EndpointKey(string route)
    {
        var path = route.Split('?')[0].Trim('/');
        if (path.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        return string.Join('/', path.Split(Sep, StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => seg.StartsWith('{') && seg.EndsWith('}') ? "{}" : seg.ToLowerInvariant()));
    }

    public static IReadOnlyList<EndpointStat> Endpoints(
        TourResults? tour, IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hosts, DateTimeOffset from, DateTimeOffset to)
    {
        if (tour is null) return [];
        var server = InWindow(hosts, "http.server.request.duration", from, to)
            .Where(s => s.Count is > 0 && s.P95 is not null && s.Tags.GetValueOrDefault("http.route") is { } route && route.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => EndpointKey(s.Tags["http.route"])).ToDictionary(g => g.Key, g => g.ToList());

        return tour.Pages.SelectMany(p => p.Requests).GroupBy(q => q.Template).Select(g =>
        {
            var done = g.Where(q => !q.Failed).Select(q => q.DurationMs).OrderBy(v => v).ToList();
            double Q(double p) => done.Count == 0 ? 0 : TourSummary.Percentile(done, p);
            server.TryGetValue(EndpointKey(g.Key), out var sv);
            var w = sv is null ? null : Weighted("server", "", sv);
            return new EndpointStat(g.Key, g.Count(), g.Count(q => q.Failed || q.Status >= 500), g.Count(q => q.Status == 400),
                Q(0.5), Q(0.95), Q(0.99),
                w is null ? null : w.P50Ms * 1000, w is null ? null : w.P95Ms * 1000, w is null ? null : w.P99Ms * 1000, w?.Count ?? 0,
                g.Count(q => q.Source == "rollup"), g.Count(q => q.Source == "raw"));
        }).OrderByDescending(e => e.ClientP95Ms).ToList();
    }

    public static IReadOnlyList<SlowPageLoad> SlowestLoads(TourResults? tour, int take = 20) =>
        tour?.Pages.OrderByDescending(p => p.ReadyMs is null ? double.MaxValue : p.ReadyMs.Value).Take(take)
            .Select(p => new SlowPageLoad(p.Step, p.Window, p.User, p.ReadyMs, p.TimedOut, p.Error, RelativeScreenshot(p.Screenshot))).ToList() ?? [];

    /// <summary>Screenshots were written by absolute path; the report links them relative to the scenario folder.</summary>
    public static string? RelativeScreenshot(string? path)
    {
        if (path is null) return null;
        var at = path.Replace('\\', '/').LastIndexOf("/screenshots/", StringComparison.Ordinal);
        return at < 0 ? Path.GetFileName(path) : path.Replace('\\', '/')[(at + 1)..];
    }

    // ---- database -------------------------------------------------------------------------------------------------

    public static IReadOnlyList<BlockingChain> BlockingChains(DatabaseObservation? db) =>
        db is null ? [] : db.LockSamples.SelectMany(s => s.Waits)
            .GroupBy(w => (w.BlockingQuery, w.BlockedQuery, w.Resource))
            .Select(g => new BlockingChain(g.Key.BlockingQuery, g.Key.BlockedQuery, g.Key.Resource, g.Count(), g.Max(w => w.WaitMs)))
            .OrderByDescending(c => c.MaxWaitMs).Take(10).ToList();

    // ---- series and markers ---------------------------------------------------------------------------------------

    private static IReadOnlyList<TimelineMarker> BuildMarkers(ScenarioResult r, Func<DateTimeOffset, double> t)
    {
        var markers = new List<TimelineMarker>();
        if (r.Phases.MeasuredStart is { } ms) markers.Add(new TimelineMarker(t(ms), "phase", "warm-up ends"));
        if (r.Phases.MeasuredEnd is { } me) markers.Add(new TimelineMarker(t(me), "phase", "measured window ends"));
        foreach (var sweep in r.Hosts.SelectMany(h => h.Log.RetentionSweeps))
            markers.Add(new TimelineMarker(t(sweep.At), "sweep", $"retention sweep: {sweep.SpanRows + sweep.DataPointRows + sweep.LogRows} rows in {sweep.ElapsedMs} ms"));
        if (r.Ramp is { } ramp)
            foreach (var step in ramp.Steps)
                markers.Add(new TimelineMarker(t(step.Start), "step", $"ramp step {step.Step} (x{step.Scale:0.##})"));
        return markers.OrderBy(m => m.T).ToList();
    }

    private static IReadOnlyList<NamedSeries> BuildSeries(
        ScenarioResult r, IReadOnlyDictionary<HostRole, IReadOnlyList<MetricSample>> hosts, Func<DateTimeOffset, double> t)
    {
        var all = new List<NamedSeries>();
        var t0 = r.Phases.WarmupStart ?? r.StartedAt;

        // Client side: one point per second per signal, offset to the scenario's clock (the load generator starts a moment after warm-up begins).
        if (r.Load is { } load)
            foreach (var s in load.Signals)
            {
                all.Add(new("client.throughput", $"{s.Signal} offered", "records/s", s.Timeline.Select(x => new SeriesPoint(x.Second, x.OfferedRecords)).ToList()));
                all.Add(new("client.throughput", $"{s.Signal} acked", "records/s", s.Timeline.Select(x => new SeriesPoint(x.Second, x.AckedRecords)).ToList()));
                all.Add(new("client.latency", $"{s.Signal} mean", "ms", s.Timeline.Where(x => x.Exports > 0).Select(x => new SeriesPoint(x.Second, x.LatencyMeanMs)).ToList()));
                all.Add(new("client.latency", $"{s.Signal} max", "ms", s.Timeline.Where(x => x.Exports > 0).Select(x => new SeriesPoint(x.Second, x.LatencyMaxMs)).ToList()));
            }

        all.Add(new("lag", "log", "ms", r.Markers.Where(m => m.LogLagMs is not null).Select(m => new SeriesPoint(t(m.SentAt), m.LogLagMs!.Value)).ToList()));
        all.Add(new("lag", "trace", "ms", r.Markers.Where(m => m.TraceLagMs is not null).Select(m => new SeriesPoint(t(m.SentAt), m.TraceLagMs!.Value)).ToList()));

        foreach (var (role, samples) in hosts)
        {
            var host = role.ToString();
            foreach (var signal in Signals)
            {
                var sig = samples.Where(s => s.Tags.GetValueOrDefault("signal") == signal).ToList();
                Add(all, "write.gate_wait", $"{signal}", "ms", sig.Where(s => s.Instrument == Ingestion + "gate_wait" && s.P95 is not null && s.Count is > 0).Select(s => new SeriesPoint(t(s.At), s.P95!.Value)));
                Add(all, "write.flush_duration", $"{signal}", "ms", sig.Where(s => s.Instrument == Ingestion + "flush_duration" && s.Tags.GetValueOrDefault("outcome") == "ok" && s.P95 is not null && s.Count is > 0).Select(s => new SeriesPoint(t(s.At), s.P95!.Value)));
                Add(all, "write.batch_size", $"{signal}", "records", sig.Where(s => s.Instrument == Ingestion + "flush_batch_size" && s.P95 is not null && s.Count is > 0).Select(s => new SeriesPoint(t(s.At), s.P95!.Value)));
                Add(all, "write.resident", $"{signal}", "records", sig.Where(s => s.Instrument == Ingestion + "resident_records" && Finite(s.Value)).Select(s => new SeriesPoint(t(s.At), s.Value)));
            }
            Add(all, "write.retries", $"{host} flush retries", "per s", Sum(samples, Ingestion + "flush_retries", t));
            Add(all, "write.retries", $"{host} records dropped", "per s", Sum(samples, Ingestion + "records_dropped", t));

            Add(all, "host.cpu", host, "cores", Sum(samples, "dotnet.process.cpu.time", t));
            Add(all, "host.memory", host, "MB", samples.Where(s => s.Instrument == "dotnet.process.memory.working_set" && Finite(s.Value)).Select(s => new SeriesPoint(t(s.At), s.Value / 1048576.0)));
            Add(all, "host.gc_pause", host, "ms per s", Sum(samples, "dotnet.gc.pause.time", t, 1000));
            Add(all, "host.threadpool_queue", host, "items", samples.Where(s => s.Instrument == "dotnet.thread_pool.queue.length" && Finite(s.Value)).Select(s => new SeriesPoint(t(s.At), s.Value)));
        }

        if (r.Database is { } db)
        {
            all.Add(new("db.lock_waits", "waits sampled", "count", db.LockSamples.Where(s => s.Error is null).Select(s => new SeriesPoint(t(s.At), s.Waits.Count)).ToList()));
            all.Add(new("db.container_cpu", "container", "cores", db.ContainerStats.Select(c => new SeriesPoint(t(c.At), c.CpuCores)).ToList()));
            all.Add(new("db.container_memory", "container", "MB", db.ContainerStats.Select(c => new SeriesPoint(t(c.At), c.MemoryBytes / 1048576.0)).ToList()));
            foreach (var (group, name, unit, gauge, agg) in new (string, string, string, string, string)[]
            {
                ("db.parts", "active parts", "parts", "parts_active", "sum"),
                ("db.parts", "max per partition", "parts", "parts_max_per_partition", "max"),
                ("db.merges", "merges running", "merges", "merges_running", "sum"),
                ("db.mutations", "mutations pending", "mutations", "mutations_pending", "sum"),
                ("db.chunks", "chunks", "chunks", "chunks", "sum"),
            })
            {
                var points = db.LockSamples.Where(s => s.Error is null && s.Gauges.Any(g => g.Name == gauge))
                    .Select(s => new SeriesPoint(t(s.At), agg == "max" ? s.Gauges.Where(g => g.Name == gauge).Max(g => g.Value) : s.Gauges.Where(g => g.Name == gauge).Sum(g => g.Value))).ToList();
                if (points.Count > 0) all.Add(new(group, name, unit, points));
            }
        }
        return all.Where(s => s.Points.Count > 0).ToList();
    }

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    private static void Add(List<NamedSeries> into, string group, string name, string unit, IEnumerable<SeriesPoint> points)
    {
        var list = points.OrderBy(p => p.T).ToList();
        if (list.Count > 0) into.Add(new NamedSeries(group, name, unit, list));
    }

    /// <summary>Sums a counter's per-interval increases across all its tags into one point per timestamp, scaled by <paramref name="scale"/>.</summary>
    private static IEnumerable<SeriesPoint> Sum(IReadOnlyList<MetricSample> samples, string instrument, Func<DateTimeOffset, double> t, double scale = 1) =>
        samples.Where(s => s.Instrument == instrument && s.Kind == "counter" && Finite(s.Value))
            .GroupBy(s => Math.Round(t(s.At))).Select(g => new SeriesPoint(g.Key, g.Sum(s => s.Value) * scale));
}
