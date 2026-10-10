using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grpc.Net.Client;
using Keryhe.Telemetry.StressTests.Observers.Process;

namespace Keryhe.Telemetry.StressTests.Orchestration;

public enum HostTopology { Split }

/// <param name="Provider">The <c>Database:Provider</c> value.</param>
/// <param name="CollectorConnectionString">Becomes <c>ConnectionStrings:Collector</c> (ingestion side).</param>
/// <param name="ApiConnectionString">Becomes <c>ConnectionStrings:Api</c>.</param>
/// <param name="ControlPlaneProvider">The <c>ControlPlane:Provider</c> value (PostgreSQL for a ClickHouse run, the telemetry provider otherwise).</param>
/// <param name="ControlPlaneConnectionString">Becomes <c>ConnectionStrings:ControlPlane</c> on both hosts.</param>
/// <param name="RunDirectory">Where each host's console output is written (<c>host-&lt;role&gt;.log</c>).</param>
/// <param name="RetentionIntervalSeconds">Short, so a retention sweep lands inside the measured window (Decision 11). Every other worker setting stays at its default.</param>
public sealed record HostLaunchOptions(
    string Provider,
    string CollectorConnectionString,
    string ApiConnectionString,
    string ControlPlaneProvider,
    string ControlPlaneConnectionString,
    HostTopology Topology,
    string RunDirectory,
    int RetentionIntervalSeconds = 60,
    TimeSpan? ReadyTimeout = null,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null);

/// <summary>How a host's graceful shutdown went.</summary>
/// <param name="DrainCompleted">Exited on its own, cleanly, with nothing left unpersisted: the ingestion queue drained inside the host's <c>ShutdownTimeout</c>.</param>
/// <param name="DeadlineReached">The worker logged that the shutdown deadline hit before a queue drained.</param>
/// <param name="Unpersisted">Records the worker reported as not persisted at the deadline.</param>
/// <param name="RecordsDropped">Total of the <c>records_dropped</c> counter over the run, as sampled over EventPipe.</param>
public sealed record ShutdownResult(
    HostRole Role, int? ExitCode, TimeSpan Elapsed, bool Killed, bool DrainCompleted,
    bool DeadlineReached, long Unpersisted, double RecordsDropped);

/// <summary>One running host process, its captured output, its scanner and its EventPipe listener.</summary>
public sealed class LaunchedHost
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly object _logGate = new();
    private readonly Queue<string> _tail = new();
    private readonly Task _stdout, _stderr;
    private bool _stopped;

    internal LaunchedHost(HostRole role, Process process, string logPath, Uri? grpcUri, Uri? apiUri, string uiBasePath, HostLogScanner scanner)
    {
        Role = role;
        _process = process;
        LogPath = logPath;
        GrpcUri = grpcUri;
        ApiUri = apiUri;
        UiUri = apiUri is null ? null : new Uri(apiUri, uiBasePath.TrimEnd('/') + "/");
        Scanner = scanner;
        _log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        _stdout = Pump(process.StandardOutput);
        _stderr = Pump(process.StandardError);
    }

    public HostRole Role { get; }
    public int Pid => _process.Id;
    public string LogPath { get; }
    public Uri? GrpcUri { get; }
    public Uri? ApiUri { get; }
    public Uri? UiUri { get; }
    public HostLogScanner Scanner { get; }
    public ProcessMetricsListener? Metrics { get; internal set; }
    public bool HasExited => _process.HasExited;

    private async Task Pump(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_logGate)
            {
                _log.WriteLine(line);
                _tail.Enqueue(line);
                if (_tail.Count > 40) _tail.Dequeue();
            }
            Scanner.OnLine(line, now);
        }
    }

    /// <summary>The last lines of output, for a failure message.</summary>
    public string Tail() { lock (_logGate) return string.Join('\n', _tail); }

    /// <summary>
    /// Graceful shutdown: SIGTERM, then wait up to <paramref name="grace"/> (a little beyond the host's
    /// 30s <c>ShutdownTimeout</c>) before killing. Records whether the drain finished — see
    /// <see cref="ShutdownResult"/>.
    /// </summary>
    public async Task<ShutdownResult> StopAsync(TimeSpan grace)
    {
        if (_stopped) throw new InvalidOperationException("Already stopped.");
        _stopped = true;

        var started = Stopwatch.GetTimestamp();
        var killed = false;
        if (!_process.HasExited)
        {
            if (!PosixSignals.TryTerminate(_process.Id))
                _process.Kill();
            try { await _process.WaitForExitAsync().WaitAsync(grace); }
            catch (TimeoutException)
            {
                killed = true;
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_logGate) _log.Dispose();

        if (Metrics is not null) await Metrics.DisposeAsync();

        var deadlines = Scanner.Summarize().ShutdownDeadlines;
        var unpersisted = deadlines.Sum(d => d.Unpersisted);
        var dropped = Metrics?.Store.Total("keryhe.telemetry.ingestion.records_dropped") ?? 0;
        var code = _process.HasExited ? _process.ExitCode : (int?)null;
        return new ShutdownResult(Role, code, elapsed, killed,
            DrainCompleted: !killed && code == 0 && deadlines.Count == 0,
            DeadlineReached: deadlines.Count > 0, unpersisted, dropped);
    }
}

/// <summary>The hosts of one scenario and where to reach them.</summary>
public sealed class HostSet : IAsyncDisposable
{
    public HostSet(HostTopology topology, IReadOnlyList<LaunchedHost> hosts)
    {
        Topology = topology;
        Hosts = hosts;
    }

    public HostTopology Topology { get; }
    public IReadOnlyList<LaunchedHost> Hosts { get; }

    /// <summary>The OTLP gRPC endpoint (h2c).</summary>
    public Uri GrpcUri => Hosts.First(h => h.GrpcUri is not null).GrpcUri!;
    /// <summary>The REST API endpoint.</summary>
    public Uri ApiUri => Hosts.First(h => h.ApiUri is not null).ApiUri!;
    /// <summary>Where the UI is served (API host plus <c>TelemetryUi:BasePath</c>).</summary>
    public Uri UiUri => Hosts.First(h => h.UiUri is not null).UiUri!;

    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(45);

    /// <summary>Stops every host (concurrently) and reports each shutdown.</summary>
    public async Task<IReadOnlyList<ShutdownResult>> StopAsync(TimeSpan? grace = null)
    {
        var results = await Task.WhenAll(Hosts.Select(h => h.StopAsync(grace ?? DefaultShutdownGrace)));
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Hosts)
        {
            try { if (!host.HasExited) await host.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Launches the published hosts as separate child processes (stress-test plan, Phase 3), configured
/// entirely by environment variables, with output captured and scanned, readiness confirmed, and an
/// EventPipe listener attached to each PID.
/// </summary>
public static class HostLauncher
{
    private static readonly HttpClient Http = new();

    public static async Task<HostSet> LaunchAsync(PublishedHosts published, HostLaunchOptions options, CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.RunDirectory);
        HostRole[] roles = [HostRole.Collector, HostRole.Api];

        var launched = new List<LaunchedHost>();
        try
        {
            foreach (var role in roles)
                launched.Add(await LaunchOneAsync(published, options, role, ct));
        }
        catch
        {
            foreach (var host in launched)
                try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { /* already reporting the original failure */ }
            throw;
        }
        return new HostSet(options.Topology, launched);
    }

    private static async Task<LaunchedHost> LaunchOneAsync(PublishedHosts published, HostLaunchOptions options, HostRole role, CancellationToken ct)
    {
        var dir = published.Directory(role);
        var (env, grpcUri, apiUri) = BuildEnvironment(options, role);

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(published.DllPath(role));
        // Not inherited: ASPNETCORE_URLS would override Kestrel:Endpoints wholesale and collapse the
        // per-endpoint protocols (breaking h2c gRPC), and a stray environment name would change config.
        foreach (var inherited in new[] { "ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
            psi.Environment.Remove(inherited);
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + PublishedHosts.ProjectName(role));
        var logPath = Path.Combine(options.RunDirectory, $"host-{role.ToString().ToLowerInvariant()}.log");
        var host = new LaunchedHost(role, process, logPath, grpcUri, apiUri, ReadUiBasePath(dir), new HostLogScanner());

        try
        {
            await WaitUntilReadyAsync(host, options.ReadyTimeout ?? TimeSpan.FromSeconds(90), ct);
            host.Metrics = new ProcessMetricsListener(host.Pid);
            return host;
        }
        catch
        {
            try { await host.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
            throw;
        }
    }

    /// <summary>The environment for one host, and the plaintext endpoints it will listen on. Public so the wiring can be unit-tested.</summary>
    public static (Dictionary<string, string> Env, Uri? Grpc, Uri? Api) BuildEnvironment(HostLaunchOptions options, HostRole role)
    {
        var env = new Dictionary<string, string>
        {
            ["Database__Provider"] = options.Provider,
            ["ControlPlane__Provider"] = options.ControlPlaneProvider,
            ["ConnectionStrings__ControlPlane"] = options.ControlPlaneConnectionString,
            ["Telemetry__Retention__IntervalSeconds"] = options.RetentionIntervalSeconds.ToString(),
            // One line per entry with a UTC timestamp, which HostLogScanner parses.
            ["Logging__Console__FormatterName"] = "simple",
            ["Logging__Console__FormatterOptions__SingleLine"] = "true",
            ["Logging__Console__FormatterOptions__TimestampFormat"] = "yyyy-MM-ddTHH:mm:ss.fffZ ",
            ["Logging__Console__FormatterOptions__UseUtcTimestamp"] = "true",
            ["DOTNET_gcServer"] = "0"
        };

        Uri? grpc = null, api = null;
        // Named Kestrel endpoints, never ASPNETCORE_URLS. The collector's TLS endpoint has no certificate
        // outside Development, so it is repointed at a plaintext port (its appsettings.json endpoint
        // is named Https), and the plaintext-transport guard is told that is intended.
        void Endpoint(string name, int port, string protocols)
        {
            env[$"Kestrel__Endpoints__{name}__Url"] = $"http://127.0.0.1:{port}";
            env[$"Kestrel__Endpoints__{name}__Protocols"] = protocols;
        }

        switch (role)
        {
            case HostRole.Collector:
            {
                var ports = PortFinder.GetFreePorts(2);
                var port = ports[0];
                Endpoint("Https", port, "Http2");
                // The shipped appsettings add a loopback management endpoint (health probes) on a fixed port; a free one avoids
                // a clash with a collector the developer already has running.
                Endpoint("Management", ports[1], "Http1");
                env["Telemetry__Collector__AllowInsecureTransport"] = "true";
                grpc = new Uri($"http://127.0.0.1:{port}");
                env["ConnectionStrings__Collector"] = options.CollectorConnectionString;
                break;
            }
            default:
            {
                var port = PortFinder.GetFreePorts(1)[0];
                Endpoint("Api", port, "Http1");
                api = new Uri($"http://127.0.0.1:{port}");
                // ClickHouse 25.x caches a predicate's matching granules, so a repeated read looks free in a measurement (Phase 0, spike 4):
                // the harness's API host reads with the cache off. Production queries do not.
                env["ConnectionStrings__Api"] = options.Provider == "ClickHouse"
                    ? options.ApiConnectionString + ";set_use_query_condition_cache=0"
                    : options.ApiConnectionString;
                // The harness calls the API at the option default /api (the readiness probe, ScenarioRunner, TourDiscovery,
                // MarkerProbe, DetailProbe; ApiRequestNormalizer assumes it), but the shipped Api.Server appsettings mount it
                // at /telemetry/api. Without this every readiness probe is a 404. The UI follows (TelemetryUi:ApiBasePath unset).
                env["Telemetry__Api__BasePath"] = "/api";
                // Pinned to the option default: the hosts are published from the working tree, so a developer's local
                // appsettings would otherwise set the budget a run measures under. A profile can still override it.
                env["Telemetry__Query__SummaryTimeoutSeconds"] = "5";
                break;
            }
        }

        foreach (var (k, v) in options.ExtraEnvironment ?? new Dictionary<string, string>())
            env[k] = v;
        return (env, grpc, api);
    }

    private static string ReadUiBasePath(string hostDirectory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(hostDirectory, "appsettings.json")));
            if (doc.RootElement.TryGetProperty("TelemetryUi", out var ui) && ui.TryGetProperty("BasePath", out var bp))
                return bp.GetString() ?? "/";
        }
        catch { /* no appsettings, or none for this host: the UI is at the root */ }
        return "/";
    }

    private static async Task WaitUntilReadyAsync(LaunchedHost host, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        Exception? last = null;
        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (host.HasExited)
                throw new InvalidOperationException($"{host.Role} host exited during startup. Last output:\n{host.Tail()}");

            try
            {
                if (host.ApiUri is not null)
                {
                    using var response = await Http.GetAsync(new Uri(host.ApiUri, "api/capabilities"), ct);
                    response.EnsureSuccessStatusCode();
                }
                if (host.GrpcUri is not null)
                {
                    using var channel = GrpcChannel.ForAddress(host.GrpcUri);
                    using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await channel.ConnectAsync(connectTimeout.Token);
                }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or Grpc.Core.RpcException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(250, ct);
            }
        }
        throw new TimeoutException($"{host.Role} host was not ready after {timeout.TotalSeconds:F0}s ({last?.Message}). Last output:\n{host.Tail()}");
    }
}

/// <summary>Sends SIGTERM, the signal .NET's generic host treats as a graceful stop. Not available on Windows.</summary>
internal static class PosixSignals
{
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    public static bool TryTerminate(int pid)
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return Kill(pid, 15) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }
}
