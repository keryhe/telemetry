using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Keryhe.Telemetry.StressTests.Load;

public sealed record LoadSnapshot(
    IReadOnlyList<SignalSummary> Signals,
    IReadOnlyList<LedgerCell> Ledger);

/// <summary>
/// The OTLP load tool (stress-test plan, Phase 2). Three open-loop senders, one per signal, each
/// building pre-shaped protobuf batches from the profile and firing them on a fixed schedule through
/// <see cref="OtlpExporter"/>. Seeded per signal, so the same profile and seed yield the same data
/// shape at the same rate schedule; only absolute timestamps differ.
/// </summary>
public sealed class OtlpLoadGenerator : IAsyncDisposable
{
    private readonly LoadProfile _profile;
    private readonly OtlpExporter _exporter;
    private readonly Topology _topology;

    private readonly SignalStats _traceStats = new("traces");
    private readonly SignalStats _logStats = new("logs");
    private readonly SignalStats _metricStats = new("metrics");
    private readonly RateController _traceRate;
    private readonly RateController _logRate;
    private readonly RateController _metricRate;
    private double _rateScale = 1;

    public SentLedger Ledger { get; } = new();
    public Topology Topology => _topology;
    public OtlpExporter Exporter => _exporter;

    public OtlpLoadGenerator(LoadProfile profile, IReadOnlyList<LoadTenant> tenants, Uri grpcTarget)
    {
        if (tenants.Count == 0) throw new ArgumentException("At least one tenant is required.", nameof(tenants));
        _profile = profile;
        _topology = new Topology(tenants, profile);
        _exporter = new OtlpExporter(grpcTarget, _topology, profile.Transport, Ledger);

        var perExport = Math.Max(1, profile.Transport.RecordsPerExport);
        _traceRate = new RateController(profile.Traces.SpansPerSecond / perExport);
        _logRate = new RateController(profile.Logs.RecordsPerSecond / perExport);
        _metricRate = new RateController(profile.Metrics.DataPointsPerSecond / perExport);
    }

    /// <summary>Multiplies every signal's configured rate (the ramp scenario's step). 1 = the profile's rate.</summary>
    public void SetRateScale(double scale)
    {
        _rateScale = scale;
        var perExport = Math.Max(1, _profile.Transport.RecordsPerExport);
        _traceRate.Rate = _profile.Traces.SpansPerSecond * scale / perExport;
        _logRate.Rate = _profile.Logs.RecordsPerSecond * scale / perExport;
        _metricRate.Rate = _profile.Metrics.DataPointsPerSecond * scale / perExport;
    }

    public double RateScale => _rateScale;

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled, then waits for in-flight exports to finish.
    /// Sends themselves are not cancelled by <paramref name="ct"/> (each has its own deadline), so an
    /// export in flight at the stop is still classified and ledgered rather than left uncertain.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _traceStats.Start(); _logStats.Start(); _metricStats.Start();

        var tasks = new List<Task>();
        if (_profile.Traces.SpansPerSecond > 0)
        {
            var shaper = new TraceShaper(_profile, _topology, new Random(HashCode.Combine(_profile.Seed, 1)));
            tasks.Add(LoopAsync(_traceRate, _traceStats, shaper.Next, (p, re) => _exporter.ExportTracesAsync(p, re, CancellationToken.None), ct));
        }
        if (_profile.Logs.RecordsPerSecond > 0)
        {
            var shaper = new LogShaper(_profile, _topology, new Random(HashCode.Combine(_profile.Seed, 2)));
            tasks.Add(LoopAsync(_logRate, _logStats, shaper.Next, (p, re) => _exporter.ExportLogsAsync(p, re, CancellationToken.None), ct));
        }
        if (_profile.Metrics.DataPointsPerSecond > 0)
        {
            var shaper = new MetricShaper(_profile, _topology, new Random(HashCode.Combine(_profile.Seed, 3)));
            tasks.Add(LoopAsync(_metricRate, _metricStats, shaper.Next, (p, re) => _exporter.ExportMetricsAsync(p, re, CancellationToken.None), ct));
        }

        await Task.WhenAll(tasks);
    }

    private async Task LoopAsync<TReq>(
        RateController rate, SignalStats stats, Func<Payload<TReq>> next,
        Func<Payload<TReq>, bool, Task<ExportResult>> send, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, _profile.Transport.MaxInFlightExports));
        var running = new List<Task>();

        // Payload generation runs on the scheduler thread (keeping the seeded stream deterministic);
        // only the send is handed off.
        await Task.Run(() => rate.RunAsync((_, lag) =>
        {
            var payload = next();
            stats.Offered(payload.Records, lag);
            if (!slots.Wait(0))
            {
                stats.NotSent(payload.Records);
                return;
            }
            var task = Task.Run(async () =>
            {
                try
                {
                    var first = await send(payload, false);
                    Record(stats, payload.Records, first);
                    // A redelivery is only meaningful once the original was accepted.
                    if (payload.Redeliver && first.Outcome == ExportOutcome.Ok)
                        Record(stats, 0, await send(payload, true));
                }
                finally { slots.Release(); }
            });
            running.RemoveAll(t => t.IsCompleted);
            running.Add(task);
        }, ct));

        await Task.WhenAll(running);
    }

    // Redeliveries add rows but were not scheduled: pass 0 records so offered/acked stay per-original.
    private static void Record(SignalStats stats, int records, ExportResult r)
    {
        if (r.Status == "CLIENT_CANCELLED") return;
        stats.Completed(records, r.LatencyMs, r.Outcome, r.Status);
    }

    /// <summary>Starts a fresh measuring window on every signal (the measured window, or one ramp step).</summary>
    public void BeginWindow() { _traceStats.BeginWindow(); _logStats.BeginWindow(); _metricStats.BeginWindow(); }

    /// <summary>Ends the window and returns what each signal did in it (traces, logs, metrics).</summary>
    public IReadOnlyList<WindowSummary> EndWindow() => [_traceStats.EndWindow(), _logStats.EndWindow(), _metricStats.EndWindow()];

    public LoadSnapshot Snapshot()
    {
        var perExport = Math.Max(1, _profile.Transport.RecordsPerExport);
        return new LoadSnapshot(
        [
            _traceStats.Summarize(_traceRate.SkippedTicks, perExport),
            _logStats.Summarize(_logRate.SkippedTicks, perExport),
            _metricStats.Summarize(_metricRate.SkippedTicks, perExport)
        ], Ledger.Snapshot());
    }

    public ValueTask DisposeAsync() => _exporter.DisposeAsync();
}
