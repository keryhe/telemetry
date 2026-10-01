using System.Text.Json;
using Keryhe.Telemetry.StressTests.Browser;
using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Keryhe.Telemetry.StressTests.Verification;
using Keryhe.Telemetry.TestInfrastructure.Seeding;

namespace Keryhe.Telemetry.StressTests.Scenarios;

/// <summary>One provider by one topology by one profile.</summary>
public sealed record ScenarioSpec(string Provider, HostTopology Topology, ScenarioProfile Profile)
{
    public string Id => $"{Provider}-{Topology}-{Profile.Name}".ToLowerInvariant();
}

/// <summary>
/// Runs one scenario end to end (stress-test plan, Phase 6): fresh container with diagnostics, seeded tenants, hosts and observers,
/// a warm-up of ingestion alone, the measured window (ingestion, browser tour and observers together, or a rate ramp), quiesce,
/// then shutdown. A scenario that fails part-way still returns a result carrying what was measured and the error, so a matrix run goes on.
/// </summary>
public static class ScenarioRunner
{
    public static async Task<ScenarioResult> RunAsync(ScenarioSpec spec, PublishedHosts published, string directory, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var profile = spec.Profile;
        var startedAt = DateTimeOffset.UtcNow;
        var phases = new PhaseMarkers(null, null, null, null, null);

        LoadSnapshot? load = null;
        IReadOnlyList<WindowSummary> measured = [];
        IReadOnlyList<MarkerResult> markers = [];
        RampResult? ramp = null;
        QuiesceResult? quiesce = null;
        TourResults? tourResults = null;
        string? browserError = null, error = null;
        DatabaseObservation? database = null;
        CorrectnessResult? correctness = null;
        double? logPinOffsetMs = null;
        var hostResults = new List<HostResult>();

        try
        {
            // Spans collapse a re-delivery only on a 2.x schema; the ledger must expect what the schema under test does.
            var schemaVersion = LedgerSemantics.TargetSchemaVersion(RepoLocator.FindRoot());
            profile.Load.Traces.RedeliveryCollapses = LedgerSemantics.SpansCollapseRedelivery(schemaVersion);
            log($"[{spec.Id}] schema {schemaVersion ?? "unknown"}: re-delivered spans {(profile.Load.Traces.RedeliveryCollapses ? "collapse" : "are stored again")}");

            log($"[{spec.Id}] starting {spec.Provider} container");
            await using var db = ProviderContainerFactory.Create(spec.Provider);
            await db.StartAsync(new ContainerOptions(Diagnostics: true, CpuLimit: profile.ContainerCpus,
                MemoryLimitBytes: (long)(profile.ContainerMemoryGb * ContainerOptions.Gigabyte), CpusetCpus: profile.DatabaseCpuset), ct);
            var tenants = await TenantSeeder.SeedAsync(db, profile.Tenants);

            await using var observers = await DatabaseObserverSession.StartAsync(spec.Provider, db, cancellationToken: ct);
            log($"[{spec.Id}] launching {spec.Topology}");
            await using var hosts = await HostLauncher.LaunchAsync(published,
                new HostLaunchOptions(spec.Provider, db.ConnectionString, db.ConnectionString, spec.Topology, directory, profile.RetentionIntervalSeconds), ct);

            await using var generator = new OtlpLoadGenerator(profile.Load,
                tenants.Select(t => new LoadTenant(t.Id, t.Name, t.ApiKey)).ToList(), hosts.GrpcUri);
            generator.SetRateScale(profile.Ramp?.StartScale ?? 1);
            using var http = new HttpClient { BaseAddress = new Uri(hosts.ApiUri.AbsoluteUri.TrimEnd('/') + "/") };
            logPinOffsetMs = await ReadAsOfBackoffMsAsync(http, tenants[0].Id, ct);
            log($"[{spec.Id}] asOf pin offset {(logPinOffsetMs is { } pin ? $"{pin:F0} ms" : "not reported (treated as 0)")}");
            var probe = new MarkerProbe(generator.Exporter, generator.Topology, 0, http,
                TimeSpan.FromSeconds(profile.MarkerIntervalSeconds), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60));
            // A write-only run reads nothing: no marker probe (its polling is a read) and, via Validate, no browsers.
            if (profile.WriteOnly) log($"[{spec.Id}] write-only: no browsers, no marker probes");

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var loadTask = generator.RunAsync(runCts.Token);
            var probeTask = profile.WriteOnly ? Task.CompletedTask : probe.RunAsync(runCts.Token);

            // Warm-up: ingestion only. The tour's discovery and browser launch run alongside it, so the browsers can start the moment it ends.
            phases = phases with { WarmupStart = DateTimeOffset.UtcNow };
            log($"[{spec.Id}] warm-up {profile.WarmupSeconds}s");
            var tourStart = profile.Browsers.Users > 0 ? StartTourAsync(hosts, tenants, profile, directory, ct) : null;
            await Task.Delay(TimeSpan.FromSeconds(profile.WarmupSeconds), ct);

            PlaywrightTour? tour = null;
            if (tourStart is not null)
            {
                try { tour = await tourStart; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    browserError = ex.Message;
                    log($"[{spec.Id}] browsers unavailable, continuing without them: {ex.Message}");
                }
            }
            await using var tourOwner = tour;

            phases = phases with { MeasuredStart = DateTimeOffset.UtcNow };
            await observers.ResetStatementStatsAsync(ct);
            tour?.Start();

            try
            {
                if (profile.Ramp is { } rampProfile)
                {
                    log($"[{spec.Id}] ramp: {rampProfile.StepSeconds}s steps from x{rampProfile.StartScale} by x{rampProfile.StepScale}, up to {rampProfile.MaxSteps}");
                    ramp = await RunRampAsync(rampProfile, generator, probe, hosts, logPinOffsetMs ?? 0, profile.WriteOnly, spec.Id, log, ct);
                }
                else
                {
                    log($"[{spec.Id}] measured window {profile.MeasuredSeconds}s");
                    generator.BeginWindow();
                    await Task.Delay(TimeSpan.FromSeconds(profile.MeasuredSeconds), ct);
                    measured = generator.EndWindow();
                }
            }
            finally
            {
                phases = phases with { MeasuredEnd = DateTimeOffset.UtcNow };
                if (tour is not null)
                {
                    // The browser tour is a bystander: losing it must not cost the database, correctness and host results.
                    try { tourResults = await tour.StopAsync(); }
                    catch (Exception ex) { browserError = $"Tour stop failed: {ex.Message}"; log($"[{spec.Id}] {browserError}"); }
                }
            }

            // Quiesce: stop the load, then wait for the ingestion queue to drain and flushing to stop.
            runCts.Cancel();
            try { await Task.WhenAll(loadTask, probeTask); } catch (OperationCanceledException) { }
            phases = phases with { LoadStopped = DateTimeOffset.UtcNow };
            load = generator.Snapshot();
            markers = probe.Results;

            log($"[{spec.Id}] quiescing (up to {profile.QuiesceTimeoutSeconds}s)");
            quiesce = await HostMetricsQuery.WaitForQuiescenceAsync(hosts,
                TimeSpan.FromSeconds(profile.QuiesceStableSeconds), TimeSpan.FromSeconds(profile.QuiesceTimeoutSeconds), ct);
            phases = phases with { QuiesceEnd = DateTimeOffset.UtcNow };
            log($"[{spec.Id}] quiesce {(quiesce.Reached ? "reached" : "TIMED OUT")} after {quiesce.WaitedSeconds:F0}s");

            // Correctness (Phase 7): the sent ledger against the database, while the hosts are still up and the sweep can still run.
            correctness = await CorrectnessRunner.RunAsync(observers, load.Ledger, hosts, phases.QuiesceEnd!.Value, profile, m => log($"[{spec.Id}] {m}"), ct);
            log($"[{spec.Id}] correctness: {correctness.Mismatches} mismatched cell(s), backdated {correctness.Backdated.Outcome}");

            database = await observers.StopAsync(cancellationToken: ct);
            foreach (var (name, content) in database.Locks.Artifacts)
                await File.WriteAllTextAsync(Path.Combine(directory, name), content, ct);

            log($"[{spec.Id}] shutting hosts down");
            var shutdowns = await hosts.StopAsync();
            foreach (var host in hosts.Hosts)
            {
                var metricsFile = host.Metrics is null ? null : $"host-{host.Role.ToString().ToLowerInvariant()}-metrics.ndjson";
                if (metricsFile is not null) await WriteMetricsAsync(Path.Combine(directory, metricsFile), host);
                hostResults.Add(new HostResult(host.Role, host.Pid, host.Scanner.Summarize(),
                    shutdowns.First(s => s.Role == host.Role), Path.GetFileName(host.LogPath), metricsFile));
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "container.log"), await db.GetLogsAsync(startedAt.UtcDateTime.AddSeconds(-5), CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex)
        {
            error = ex is OperationCanceledException ? "Cancelled." : ex.ToString();
            log($"[{spec.Id}] FAILED: {ex.Message}");
        }

        var result = new ScenarioResult(ScenarioResult.CurrentSchemaVersion, spec.Provider, spec.Topology.ToString(), profile.Name,
            profile.IsRamp ? "ramp" : "fixed", startedAt, DateTimeOffset.UtcNow, error, profile, phases,
            load, measured, markers, ramp, quiesce, tourResults, browserError, database, correctness, hostResults, logPinOffsetMs);
        await File.WriteAllTextAsync(Path.Combine(directory, "scenario.json"), JsonSerializer.Serialize(result, ResultJson.Options), CancellationToken.None);
        return result;
    }

    private static Task<PlaywrightTour> StartTourAsync(HostSet hosts, IReadOnlyList<SeededTenant> tenants, ScenarioProfile profile, string directory, CancellationToken ct) =>
        PlaywrightTour.StartAsync(hosts.UiUri, hosts.ApiUri, tenants, profile.Browsers.ToTourOptions(), directory, ct);

    /// <summary>
    /// The provider's declared <c>asOf</c> back-off, in ms, from <c>GET /api/capabilities</c> (read from the provider rather than hard-coded
    /// per provider here). Null when the endpoint does not answer or predates the field; the criteria then treat it as 0.
    /// </summary>
    private static async Task<double?> ReadAsOfBackoffMsAsync(HttpClient api, long tenantId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/capabilities");
            request.Headers.Add("X-Tenant-Id", tenantId.ToString());
            using var response = await api.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("asOfBackoffSeconds", out var seconds) && seconds.TryGetDouble(out var value) ? value * 1000 : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<RampResult> RunRampAsync(
        RampProfile ramp, OtlpLoadGenerator generator, MarkerProbe probe, HostSet hosts, double logPinOffsetMs, bool writeOnly, string id, Action<string> log, CancellationToken ct)
    {
        var steps = new List<RampStepResult>();
        IReadOnlyList<string> tripped = [];
        int? trippedStep = null;
        for (var i = 0; i < ramp.MaxSteps; i++)
        {
            var scale = ramp.StartScale + i * ramp.StepScale;
            generator.SetRateScale(scale);
            generator.BeginWindow();
            var start = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(ramp.StepSeconds), ct);
            var end = DateTimeOffset.UtcNow;
            var windows = generator.EndWindow();

            var (traceLags, logLags) = StepLags(probe.Results, probe.Pending, start, end, ramp.Criteria, logPinOffsetMs);
            var m = new StepMeasurements(windows, HostMetricsQuery.Dropped(hosts, start, end), HostMetricsQuery.GateWaitP95Ms(hosts, start, end),
                traceLags, logLags, logPinOffsetMs, HostMetricsQuery.CommitLagP95Ms(hosts, start, end));
            tripped = RampEvaluator.Tripped(m, ramp.Criteria);
            steps.Add(new RampStepResult(i, scale, start, end, windows, m.RecordsDropped, m.GateWaitP95Ms, m.TraceLagsMs, m.LogLagsMs, tripped, m.CommitLagP95Ms));

            log($"[{id}] step {i} x{scale:0.##}: acked {windows.Sum(w => w.AckedPerSecond):F0}/s of {windows.Sum(w => w.OfferedPerSecond):F0}/s offered, " +
                $"export p99 {windows.Max(w => w.Latency.P99Ms):F0} ms, gate wait p95 {m.GateWaitP95Ms:F0} ms, commit lag p95 {m.CommitLagP95Ms:F0} ms, dropped {m.RecordsDropped:F0}, " +
                (writeOnly ? "" : $"lag log (pin-adjusted) / trace {MeanLag(RampEvaluator.AdjustForPin(m.LogLagsMs, logPinOffsetMs))} / {MeanLag(m.TraceLagsMs)} ms") +
                (tripped.Count > 0 ? $"  TRIPPED: {string.Join(", ", tripped)}" : ""));
            if (tripped.Count > 0) { trippedStep = i; break; }
        }
        var lastSustained = trippedStep is null ? steps.Count - 1 : trippedStep.Value - 1;
        return new RampResult(steps, lastSustained < 0 ? null : lastSustained, trippedStep, tripped, ReachedMaxSteps: trippedStep is null);
    }

    /// <summary>
    /// The step's lag series, in send order: every probe sent during the step that completed by its end, plus any still polling whose elapsed time
    /// (a lower bound on its lag) already exceeds <see cref="RampCriteria.MaxLagSeconds"/> after the pin offset. Leaving those out would blind the
    /// lag criteria exactly when lag is worst, since a step's slowest probes are the ones still polling when it ends. Younger pending probes
    /// are left out as before: their lower bound says nothing yet.
    /// </summary>
    public static (List<double?> Trace, List<double?> Log) StepLags(
        IReadOnlyList<MarkerResult> results, IReadOnlyList<PendingMarker> pendingProbes, DateTimeOffset start, DateTimeOffset end, RampCriteria criteria, double logPinOffsetMs)
    {
        var completed = results.Where(r => r.SentAt >= start && r.SentAt < end).ToList();
        var pending = criteria.MaxLagSeconds > 0 ? pendingProbes.Where(p => p.SentAt >= start && p.SentAt < end).ToList() : [];
        var threshold = criteria.MaxLagSeconds * 1000;

        // Built per signal, so a probe pending on one signal never puts a "never visible" null into the other's series.
        List<double?> Series(Func<MarkerResult, double?> lag, Func<PendingMarker, bool> isPending, Func<PendingMarker, double?> knownLag, double offsetMs) =>
            completed.Select(r => (r.SentAt, Lag: lag(r)))
                .Concat(pending.Select(p => (p.SentAt, Pending: isPending(p), Known: knownLag(p), Elapsed: (end - p.SentAt).TotalMilliseconds))
                    .Where(p => p.Pending ? p.Elapsed - offsetMs > threshold : true)
                    .Select(p => (p.SentAt, Lag: p.Pending ? p.Elapsed : p.Known)))
                .OrderBy(x => x.SentAt).Select(x => x.Lag).ToList();

        return (Series(r => r.TraceLagMs, p => p.TracePending, p => p.TraceLagMs, 0),
                Series(r => r.LogLagMs, p => p.LogPending, p => p.LogLagMs, logPinOffsetMs));
    }

    private static string MeanLag(IReadOnlyList<double?> lags) =>
        lags.Any(l => l is not null) ? lags.Where(l => l is not null).Average(l => l!.Value).ToString("F0") : "n/a";

    private static async Task WriteMetricsAsync(string path, LaunchedHost host)
    {
        await using var writer = new StreamWriter(path);
        foreach (var sample in host.Metrics!.Store.All().OrderBy(s => s.At))
            await writer.WriteLineAsync(JsonSerializer.Serialize(sample, ResultJson.Compact));
    }
}

public static class ResultJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>The same settings on one line, for NDJSON.</summary>
    public static JsonSerializerOptions Compact { get; } = new(Options) { WriteIndented = false };
}
