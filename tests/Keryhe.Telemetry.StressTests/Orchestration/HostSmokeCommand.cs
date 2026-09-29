using System.Text.Json;
using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Keryhe.Telemetry.TestInfrastructure.Seeding;

namespace Keryhe.Telemetry.StressTests.Orchestration;

/// <summary>
/// <c>host-smoke</c>: starts one database container, publishes and launches the hosts for one topology,
/// drives a short OTLP load, then shuts down gracefully and prints what the orchestration captured
/// (log scanner, EventPipe metrics, shutdown result). It is the Phase 3 end-to-end check; the full
/// scenario runner replaces it in Phase 6.
/// </summary>
public static class HostSmokeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var provider = "PostgreSQL";
        var topology = HostTopology.AllInOne;
        var duration = 20;
        var retentionSeconds = 10;
        var browsers = 0;
        var warmup = 70;
        var exports = false;
        var browserTimeout = 60;
        string? outDir = null, reusePublish = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => args[++i];
            switch (args[i])
            {
                case "--provider": provider = Next(); break;
                case "--topology": topology = Enum.Parse<HostTopology>(Next(), ignoreCase: true); break;
                case "--duration": duration = int.Parse(Next()); break;
                case "--retention-interval": retentionSeconds = int.Parse(Next()); break;
                case "--browsers": browsers = int.Parse(Next()); break;
                case "--browser-warmup": warmup = int.Parse(Next()); break;
                case "--browser-export": exports = true; break;
                case "--browser-timeout": browserTimeout = int.Parse(Next()); break;
                case "--out": outDir = Next(); break;
                case "--reuse-publish": reusePublish = Next(); break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
            }
        }

        var repo = RepoLocator.FindRoot();
        outDir ??= Path.Combine(repo, "stress-results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-smoke");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Output: {outDir}");

        var roles = topology == HostTopology.AllInOne ? new[] { HostRole.AllInOne } : [HostRole.Collector, HostRole.Api];
        Console.WriteLine(reusePublish is null ? "Publishing hosts (Release)..." : $"Reusing published hosts in {reusePublish}");
        var published = await HostPublisher.PublishAsync(repo, reusePublish ?? Path.Combine(outDir, "publish"), roles, skipIfPresent: reusePublish is not null);

        Console.WriteLine($"Starting {provider} container...");
        await using var db = ProviderContainerFactory.Create(provider);
        await db.StartAsync(new ContainerOptions(Diagnostics: true, CpuLimit: 4, MemoryLimitBytes: 8 * ContainerOptions.Gigabyte));
        var tenants = await TenantSeeder.SeedAsync(db, 2);
        await using var observers = await DatabaseObserverSession.StartAsync(provider, db);

        Console.WriteLine($"Launching {topology}...");
        await using var hosts = await HostLauncher.LaunchAsync(published,
            new HostLaunchOptions(provider, db.ConnectionString, db.ConnectionString, topology, outDir, retentionSeconds));
        foreach (var h in hosts.Hosts)
            Console.WriteLine($"  {h.Role}: pid {h.Pid}, grpc {h.GrpcUri?.ToString() ?? "-"}, api {h.ApiUri?.ToString() ?? "-"}, ui {h.UiUri?.ToString() ?? "-"}");

        var profile = new LoadProfile { Time = new TimeShaping { BackdatedFraction = 0.05, RedeliveryFraction = 0.05 } };
        var loadTenants = tenants.Select(t => new LoadTenant(t.Id, t.Name, t.ApiKey)).ToList();
        await using var generator = new OtlpLoadGenerator(profile, loadTenants, hosts.GrpcUri);
        using var http = new HttpClient { BaseAddress = hosts.ApiUri };
        var probe = new MarkerProbe(generator.Exporter, generator.Topology, 0, http, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30));

        Console.WriteLine($"Sending load for {duration}s...");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
        var tourTask = browsers > 0 ? RunTourAsync(hosts, tenants, browsers, warmup, exports, browserTimeout, outDir, cts.Token) : Task.FromResult<TourResults?>(null);
        await Task.WhenAll(generator.RunAsync(cts.Token), probe.RunAsync(cts.Token));
        var tour = await tourTask;
        foreach (var s in generator.Snapshot().Signals)
            Console.WriteLine($"  {s.Signal,-8} offered {s.OfferedPerSecond:F0}/s acked {s.AckedPerSecond:F0}/s failed {s.ExportsFailed} p99 {s.Latency.P99Ms:F0}ms");

        Console.WriteLine("Metrics captured over EventPipe (per host):");
        foreach (var h in hosts.Hosts)
            PrintMetrics(h);

        Console.WriteLine("Shutting down...");
        var shutdowns = await hosts.StopAsync();

        foreach (var h in hosts.Hosts)
        {
            var log = h.Scanner.Summarize();
            Console.WriteLine($"{h.Role} log: {log.Warnings} warnings, {log.Errors} errors, {log.FlushRetries.Count} flush retries, " +
                              $"{log.BatchesDropped.Count} dropped batches, {log.ProviderErrors.Count} provider lock errors, {log.RetentionSweeps.Count} retention sweeps");
            foreach (var sweep in log.RetentionSweeps)
                Console.WriteLine($"    sweep at {sweep.At:HH:mm:ss}: {sweep.SpanRows} span, {sweep.DataPointRows} data-point, {sweep.LogRows} log rows in {sweep.ElapsedMs} ms");
        }
        foreach (var r in shutdowns)
            Console.WriteLine($"{r.Role} shutdown: exit {r.ExitCode}, {r.Elapsed.TotalSeconds:F1}s, killed {r.Killed}, drain completed {r.DrainCompleted}, " +
                              $"unpersisted {r.Unpersisted}, records_dropped {r.RecordsDropped}");

        if (tour is not null) await WriteTourAsync(tour, outDir);
        var observation = await observers.StopAsync();
        PrintObservation(observation);
        foreach (var (name, content) in observation.Locks.Artifacts)
            await File.WriteAllTextAsync(Path.Combine(outDir, name), content);
        await File.WriteAllTextAsync(Path.Combine(outDir, "database-observation.json"),
            JsonSerializer.Serialize(observation, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }

    /// <summary>Waits <paramref name="warmup"/> seconds so data exists, then runs the tour until the load ends.</summary>
    private static async Task<TourResults?> RunTourAsync(HostSet hosts, IReadOnlyList<SeededTenant> tenants, int users, int warmup, bool exports, int browserTimeout, string outDir, CancellationToken loadEnds)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(warmup), loadEnds); } catch (OperationCanceledException) { return null; }
        Console.WriteLine($"Starting browser tour ({users} user(s))...");
        await using var tour = await PlaywrightTour.StartAsync(hosts.UiUri, hosts.ApiUri, tenants,
            new TourOptions { Users = users, Export = exports, ReadyTimeout = TimeSpan.FromSeconds(browserTimeout) }, outDir);
        tour.Start();
        try { await Task.Delay(Timeout.Infinite, loadEnds); } catch (OperationCanceledException) { }
        return await tour.StopAsync();
    }

    private static async Task WriteTourAsync(TourResults tour, string outDir)
    {
        Console.WriteLine($"Browser tour: {tour.Iterations} full loop(s), {tour.Pages.Count} steps, {tour.Pages.Count(p => p.TimedOut)} timeouts, " +
                          $"{tour.Pages.Count(p => p.Error is not null)} errors, {tour.Pages.Sum(p => p.ConsoleErrors.Count)} console errors");
        foreach (var (d, i) in tour.Discovery.Select((d, i) => (d, i)))
            Console.WriteLine($"  user {i} data: service {d.Service ?? "none"}, histogram {d.HistogramMetric ?? "none"}, sum {d.SumMetric ?? "none"}");
        foreach (var s in TourSummary.Steps(tour.Pages))
            Console.WriteLine($"  {s.Step,-34} runs {s.Runs,3} p50 {s.P50Ms,7:F0} ms p95 {s.P95Ms,7:F0} ms max {s.MaxMs,7:F0} ms" +
                              (s.Timeouts + s.Errors > 0 ? $"  timeouts {s.Timeouts} errors {s.Errors}" : ""));
        Console.WriteLine("  slowest API calls (client-side):");
        foreach (var r in TourSummary.Requests(tour.Pages).Take(6))
            Console.WriteLine($"    {r.Template,-30} {r.Calls,4} calls p50 {r.P50Ms,6:F0} p95 {r.P95Ms,6:F0} max {r.MaxMs,6:F0} ms" +
                              (r.Status400 > 0 ? $" ({r.Status400} x 400)" : "") + (r.Sources is null ? "" : $" [{r.Sources}]"));
        await File.WriteAllTextAsync(Path.Combine(outDir, "browser-tour.json"),
            JsonSerializer.Serialize(tour, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static void PrintObservation(DatabaseObservation o)
    {
        var waits = o.LockSamples.SelectMany(s => s.Waits).ToList();
        Console.WriteLine($"Database ({o.Provider}): {o.LockSamples.Count} lock samples ({o.LockSamples.Count(s => s.Error is not null)} failed), " +
                          $"{waits.Count} lock waits seen, {o.Locks.Deadlocks} deadlocks, {o.ContainerStats.Count} container stat samples");
        Console.WriteLine("  counter deltas: " + string.Join(", ", o.Locks.CounterDeltas.Select(kv => $"{kv.Key}={kv.Value:F0}")));
        foreach (var check in o.Locks.Checks)
            Console.WriteLine($"  check '{check.Name}': {check.Label} — {check.Detail}");
        if (o.ContainerStats.Count > 0)
            Console.WriteLine($"  container: peak {o.ContainerStats.Max(c => c.CpuCores):F2} cores, peak memory {o.ContainerStats.Max(c => c.MemoryBytes) / 1048576} MB");
        Console.WriteLine($"  slowest SQL by total time ({o.Statements.Source}):");
        foreach (var st in o.Statements.ByTotal.Take(5))
            Console.WriteLine($"    {st.TotalMs,9:F0} ms / {st.Calls,7} calls (mean {st.MeanMs:F2}, max {st.MaxMs:F1})  {st.Query.ReplaceLineEndings(" ")[..Math.Min(90, st.Query.Length)]}");
        Console.WriteLine("  tables: " + string.Join(", ", o.Tables.OrderByDescending(t => t.Bytes).Take(4).Select(t => $"{t.Table} {t.Rows}{(t.RowsApproximate ? "~" : "")} rows {t.Bytes / 1048576} MB")));
    }

    private static void PrintMetrics(LaunchedHost host)
    {
        if (host.Metrics is null) return;
        var store = host.Metrics.Store;
        Console.WriteLine($"  {host.Role}: {store.All().Count} samples" + (host.Metrics.Error is null ? "" : $", listener error: {host.Metrics.Error.Message}"));
        foreach (var name in new[] { "records_flushed", "records_dropped", "flush_retries" })
            foreach (var signal in new[] { "logs", "traces", "metrics" })
                if (store.Series($"keryhe.telemetry.ingestion.{name}", "signal", signal).Count > 0)
                    Console.WriteLine($"    {name}[{signal}] total {store.Total($"keryhe.telemetry.ingestion.{name}", "signal", signal):F0}");
        foreach (var name in new[] { "gate_wait", "flush_duration", "flush_batch_size" })
        {
            var series = store.Series($"keryhe.telemetry.ingestion.{name}", "signal", "traces");
            if (series.Count > 0)
                Console.WriteLine($"    {name}[traces]: {series.Count} intervals, last p95 {series[^1].P95:F1}, count {series.Sum(s => s.Count):F0}");
        }
        var resident = store.Latest("keryhe.telemetry.ingestion.resident_records", "signal", "traces");
        Console.WriteLine($"    resident_records[traces] last {resident?.Value}");
        if (Environment.GetEnvironmentVariable("STRESS_DUMP_INSTRUMENTS") == "1")
            foreach (var g in store.All().GroupBy(x => (x.Meter, x.Instrument, x.Kind)).OrderBy(g => g.Key.Meter).ThenBy(g => g.Key.Instrument))
                Console.WriteLine($"      [{g.Key.Meter}] {g.Key.Instrument} ({g.Key.Kind}) n={g.Count()} last={g.OrderBy(x => x.At).Last().Value} tags={string.Join(',', g.OrderBy(x => x.At).Last().Tags.Select(t => t.Key + "=" + t.Value))}");
        var request = store.Series("http.server.request.duration");
        Console.WriteLine($"    http.server.request.duration: {request.Count} series-intervals, {request.Sum(s => s.Count ?? 0):F0} requests");
        Console.WriteLine($"    cpu s/s (user) last {store.Latest("dotnet.process.cpu.time", "cpu.mode", "user")?.Value:F2}, working set MB last {(store.Latest("dotnet.process.memory.working_set")?.Value ?? 0) / 1048576:F0}, gc pause s total {store.Total("dotnet.gc.pause.time"):F3}");
    }
}
