using System.Text.Json;
using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Reporting;
using Keryhe.Telemetry.StressTests.Verification;
using Keryhe.Telemetry.TestInfrastructure.Containers;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>
/// <c>run</c>: the stress CLI (stress-test plan, Phase 6). One provider, topology and profile, or <c>all</c> for any of them; the resulting
/// matrix runs sequentially (concurrent scenarios on one machine would contaminate each other's numbers), publishing the hosts once.
/// </summary>
public static class RunCommand
{
    public const string Usage =
        "run [--provider <PostgreSQL|SqlServer|MySql|ClickHouse|all>] [--topology <split|all>]\n" +
        "    [--profile <smoke|standard|soak|ramp|all|path.json>] [--scenario <fixed|ramp>] [--out <dir>] [--reuse-publish <dir>]\n" +
        "    [--browsers <n>]   override the profile's browser users (0 = none)\n" +
        "    [--db-cpuset <cpus>]   pin the database container to these CPUs of the Docker VM (e.g. 0-3); recorded in the report\n" +
        "    [--retention-interval <stress|realistic|seconds>]   override the profile's retention interval (stress = 30 s, realistic = 3600 s)\n" +
        "    [--seed-days <n>] [--seed-spans-per-day <n>]   send n days (1-60) of backdated history, plus large traces, before the warm-up; recorded in the report\n" +
        "    [--host-env KEY=VALUE]...   extra environment variables for both hosts (e.g. Telemetry__Ingestion__FlushLingerMilliseconds=1000)\n" +
        "    [--db-sql <file>]   SQL run against the database after the schema, before the hosts start (ClickHouse only)";

    public static async Task<int> RunAsync(string[] args)
    {
        string provider = "PostgreSQL", topology = "split", profile = "", scenario = "fixed";
        string? outDir = null, reusePublish = null;
        int? browsers = null;
        string? retentionInterval = null, dbCpuset = null;
        int? seedDays = null, seedSpansPerDay = null;
        var hostEnvironment = new Dictionary<string, string>();
        string? databaseSqlFile = null;
        var scenarioGiven = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--provider": provider = Next(); break;
                case "--topology": topology = Next(); break;
                case "--profile": profile = Next(); break;
                case "--scenario": scenario = Next(); scenarioGiven = true; break;
                case "--out": outDir = Next(); break;
                case "--reuse-publish": reusePublish = Next(); break;
                case "--browsers": browsers = int.Parse(Next()); break;
                case "--retention-interval": retentionInterval = Next(); break;
                case "--db-cpuset": dbCpuset = Next(); break;
                case "--seed-days": seedDays = int.Parse(Next()); break;
                case "--seed-spans-per-day": seedSpansPerDay = int.Parse(Next()); break;
                case "--host-env":
                {
                    var pair = Next();
                    var eq = pair.IndexOf('=');
                    if (eq < 1) { Console.Error.WriteLine($"--host-env needs KEY=VALUE, got '{pair}'."); return 2; }
                    hostEnvironment[pair[..eq]] = pair[(eq + 1)..];
                    break;
                }
                case "--db-sql": databaseSqlFile = Next(); break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}\n{Usage}"); return 2;
            }
        }

        List<ScenarioSpec> specs;
        try
        {
            specs = BuildMatrix(provider, topology, profile, scenario, scenarioGiven, browsers, retentionInterval, dbCpuset);
            if (seedDays is { } days) foreach (var spec in specs) spec.Profile.ApplySeed(days, seedSpansPerDay);
            var databaseSql = databaseSqlFile is null ? null : File.ReadAllText(databaseSqlFile);
            foreach (var spec in specs)
            {
                spec.Profile.HostEnvironment = new Dictionary<string, string>(hostEnvironment);
                spec.Profile.DatabaseSetupSql = databaseSql;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var repo = RepoLocator.FindRoot();
        // Absolute, because the hosts are launched from the published folder under it, not from this process's working directory.
        outDir = Path.GetFullPath(outDir ?? Path.Combine(repo, "stress-results", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        if (reusePublish is not null) reusePublish = Path.GetFullPath(reusePublish);
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Output: {outDir}");
        Console.WriteLine($"Matrix: {specs.Count} scenario(s), run sequentially: {string.Join(", ", specs.Select(s => s.Id))}");

        HostRole[] roles = [HostRole.Collector, HostRole.Api];
        Console.WriteLine(reusePublish is null ? "Publishing hosts (Release)..." : $"Reusing published hosts in {reusePublish}");
        var published = await HostPublisher.PublishAsync(repo, reusePublish ?? Path.Combine(outDir, "publish"), roles, skipIfPresent: reusePublish is not null);

        await File.WriteAllTextAsync(Path.Combine(outDir, ReportBuilder.RunMetadataFile), JsonSerializer.Serialize(
            await RunMetadataCollector.CollectAsync(repo, specs.Select(s => s.Provider), "run " + string.Join(' ', args)), ResultJson.Options));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var results = new List<ScenarioResult>();
        foreach (var spec in specs)
        {
            if (cts.IsCancellationRequested) break;
            var result = await ScenarioRunner.RunAsync(spec, published, Path.Combine(outDir, spec.Id), Console.WriteLine, cts.Token);
            results.Add(result);
            Console.WriteLine(Summarize(result));
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "matrix.json"),
            JsonSerializer.Serialize(results.Select(r => new { id = $"{r.Provider}-{r.Topology}-{r.Profile}".ToLowerInvariant(), r.Provider, r.Topology, r.Profile, r.Kind, r.Error, folder = $"{r.Provider}-{r.Topology}-{r.Profile}".ToLowerInvariant() }), ResultJson.Options));
        foreach (var path in await ReportBuilder.BuildAsync(outDir))
            Console.WriteLine($"Wrote {path}");
        return results.Count == specs.Count && results.All(r => r.Error is null) ? 0 : 1;
    }

    /// <summary>Expands the CLI selections into the scenarios to run, provider-major so one container image stays warm across profiles. Pure apart from reading profile files.</summary>
    public static List<ScenarioSpec> BuildMatrix(string provider, string topology, string profile, string scenario, bool scenarioGiven, int? browsers, string? retentionInterval = null, string? databaseCpuset = null)
    {
        var providers = provider.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? ProviderContainerFactory.ProviderNames.ToList()
            : [ProviderContainerFactory.ProviderNames.FirstOrDefault(p => p.Equals(provider, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"Unknown provider '{provider}' (expected {string.Join(", ", ProviderContainerFactory.ProviderNames)} or all).")];

        var topologies = topology.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? [HostTopology.Split]
            : Enum.TryParse<HostTopology>(topology, ignoreCase: true, out var t) ? new[] { t }
            : throw new ArgumentException($"Unknown topology '{topology}' (expected split or all).");

        if (!scenario.Equals("fixed", StringComparison.OrdinalIgnoreCase) && !scenario.Equals("ramp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown scenario '{scenario}' (expected fixed or ramp).");
        var wantsRamp = scenario.Equals("ramp", StringComparison.OrdinalIgnoreCase);

        // With no --profile, --scenario picks the default: smoke for fixed, ramp for ramp.
        if (profile.Length == 0) profile = wantsRamp ? "ramp" : "smoke";
        var profileNames = profile.Equals("all", StringComparison.OrdinalIgnoreCase) ? ScenarioProfile.Builtin.Where(p => wantsRamp == ScenarioProfile.Resolve(p).IsRamp || !scenarioGiven).ToList() : [profile];

        foreach (var name in profileNames)
            if (scenarioGiven && ScenarioProfile.Resolve(name).IsRamp != wantsRamp)
                throw new ArgumentException($"Profile '{name}' is a {(wantsRamp ? "fixed-volume" : "ramp")} profile but --scenario {scenario} was given.");

        var specs = new List<ScenarioSpec>();
        foreach (var prov in providers)
            foreach (var top in topologies)
                foreach (var name in profileNames)
                {
                    // Resolved afresh per scenario, so an override on one never leaks into the next.
                    var p = ScenarioProfile.Resolve(name);
                    if (browsers is { } b) p.Browsers.Users = b;
                    if (retentionInterval is not null) p.OverrideRetentionInterval(retentionInterval);
                    if (databaseCpuset is not null) p.DatabaseCpuset = databaseCpuset;
                    p.Validate();
                    specs.Add(new ScenarioSpec(prov, top, p));
                }
        return specs;
    }

    public static string Summarize(ScenarioResult r)
    {
        var lines = new List<string> { $"=== {r.Provider} / {r.Topology} / {r.Profile} ({r.Kind}) — {(r.FinishedAt - r.StartedAt).TotalMinutes:F1} min ===" };
        if (r.Error is not null) lines.Add("  FAILED: " + r.Error.Split('\n')[0]);
        if (r.Outage is { } outage)
            lines.Add($"  DATABASE UNAVAILABLE after the run: {outage.Message} (container {outage.ContainerStatus}, exit {outage.ExitCode?.ToString() ?? "?"}, OOM-killed {outage.OomKilled?.ToString() ?? "?"})");

        foreach (var w in r.MeasuredWindow)
            lines.Add($"  {w.Signal,-8} offered {w.OfferedPerSecond,8:F0}/s acked {w.AckedPerSecond,8:F0}/s not-sent {w.NotSentRecords} failed exports {w.ExportsFailed} export p50/p95/p99 {w.Latency.P50Ms:F0}/{w.Latency.P95Ms:F0}/{w.Latency.P99Ms:F0} ms");
        if (r.Ramp is { } ramp)
        {
            lines.Add(ramp.TrippedStep is { } tripped
                ? $"  ramp: last sustained step {(ramp.LastSustainedStep is { } s ? $"{s} (x{ramp.Steps[s].Scale:0.##})" : "none")}; step {tripped} (x{ramp.Steps[tripped].Scale:0.##}) tripped: {string.Join(", ", ramp.TrippedCriteria)}"
                : $"  ramp: reached the step limit ({ramp.Steps.Count} steps, up to x{ramp.Steps[^1].Scale:0.##}) with no criterion tripped");
        }

        var lagLog = r.Markers.Where(m => m.LogLagMs is not null).Select(m => m.LogLagMs!.Value).OrderBy(v => v).ToList();
        var lagTrace = r.Markers.Where(m => m.TraceLagMs is not null).Select(m => m.TraceLagMs!.Value).OrderBy(v => v).ToList();
        if (r.Markers.Count > 0)
            lines.Add($"  ingest-to-queryable lag p50: log {(lagLog.Count == 0 ? "n/a" : $"{TourSummary.Percentile(lagLog, 0.5):F0} ms")} (includes a {r.LogPinOffsetMs ?? 0:F0} ms asOf pin), trace {(lagTrace.Count == 0 ? "n/a" : $"{TourSummary.Percentile(lagTrace, 0.5):F0} ms")}; {r.Markers.Count(m => m.LogLagMs is null || m.TraceLagMs is null)} timed out");

        if (r.Quiesce is { } q) lines.Add($"  quiesce: {(q.Reached ? "reached" : "TIMED OUT")} after {q.WaitedSeconds:F0}s");
        foreach (var h in r.Hosts)
            lines.Add($"  {h.Role}: {h.Log.Errors} log errors, {h.Log.FlushRetries.Count} flush retries, {h.Log.BatchesDropped.Count} dropped batches, {h.Log.RetentionSweeps.Count} retention sweeps, " +
                      $"drain {(h.Shutdown.DrainCompleted ? "completed" : "NOT completed")}, records_dropped {h.Shutdown.RecordsDropped:F0}");
        if (r.Database is { } d)
            lines.Add($"  database: {d.Locks.Deadlocks} deadlocks, {d.LockSamples.SelectMany(s => s.Waits).Count()} lock waits sampled" +
                      string.Concat(d.Locks.Checks.Select(c => $"; {c.Name}: {c.Label}")));
        if (r.Correctness is { } c)
        {
            lines.Add($"  correctness: {c.Rows.Count(x => x.Status == CorrectnessStatus.Match)} of {c.Rows.Count} cells match, {c.ExplainedByDrops} explained by drops, {c.ExplainedByAbandonedExports} by abandoned exports, {c.Mismatches} MISMATCHED; " +
                      $"backdated {c.Backdated.Outcome} ({c.Backdated.RowsRemaining} rows remain)" +
                      (c.PendingMergeDuplicates is { } p ? $"; {p} span rows awaiting merge" : ""));
            foreach (var x in c.Rows.Where(x => x.Status == CorrectnessStatus.Mismatch).Take(8))
                lines.Add($"    tenant {x.TenantId} {x.Table}: expected {x.Expected} actual {x.Actual} (delta {x.Delta:+#;-#;0})");
        }
        if (r.Tour is { } t)
            lines.Add($"  browsers: {t.Iterations} loops, {t.Pages.Count} steps, {t.Pages.Count(p => p.TimedOut)} timeouts, {t.Pages.Count(p => p.Error is not null)} errors");
        if (r.BrowserError is not null) lines.Add("  browsers unavailable: " + r.BrowserError);
        return string.Join('\n', lines);
    }
}
