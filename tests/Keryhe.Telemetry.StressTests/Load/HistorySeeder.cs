using System.Diagnostics;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>A trace the seed step sent, remembered so trace detail can be requested for it later (old, or large) with and without a start hint.</summary>
public sealed record SeededTrace(int TenantIndex, long TenantId, string TraceIdHex, long StartUnixNano, long EndUnixNano, int Spans, string Kind);

/// <summary>What the seed step did: the parameters (so a seeded run is never compared with an unseeded one) and the traces to probe.</summary>
public sealed record HistorySeedResult(int Days, int SpansPerDay, long SpansSent, long SpansFailed, double Seconds, IReadOnlyList<SeededTrace> Traces);

/// <summary>
/// Sends days of backdated traces, and a few large ones, through the real OTLP path before the warm-up (trace-list-detail-performance plan,
/// Phase 0). The records are ledgered as current (the ledger entries the shaper builds), so the correctness check still balances; they
/// are older than anything the measured window reads, but they make the spans table hold many ClickHouse partitions.
/// Everything is deterministic for a profile seed.
/// </summary>
public static class HistorySeeder
{
    /// <summary>Seeded history ends this long before now, so it never overlaps the warm-up and measured windows.</summary>
    private static readonly TimeSpan GapFromNow = TimeSpan.FromHours(2);

    private const int ParallelExports = 8;
    private const int LargeTraceChunkSpans = 500;

    public static async Task<HistorySeedResult> RunAsync(
        OtlpExporter exporter, Topology topology, LoadProfile load, int days, int spansPerDay, IReadOnlyList<int> largeTraceSizes,
        Action<string> log, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var rng = new Random(load.Seed * 7919 + 13);
        var shaper = new TraceShaper(load, topology, rng);
        var nowNanos = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var endNanos = nowNanos - (long)GapFromNow.TotalMilliseconds * 1_000_000L;
        var startNanos = endNanos - days * 86_400_000_000_000L;

        var exportsNeeded = (int)Math.Ceiling(days * (double)spansPerDay / Math.Max(1, load.Transport.RecordsPerExport));
        log($"seeding {days} day(s) of history: about {days * (long)spansPerDay:N0} spans in about {exportsNeeded:N0} exports, plus large traces {string.Join("/", largeTraceSizes)}");

        var payloads = new List<(Payload<ExportTraceServiceRequest> Payload, long Base)>(exportsNeeded);
        var sampled = new List<SeededTrace>();
        for (var i = 0; i < exportsNeeded; i++)
        {
            // Spread evenly across the whole span of history, with jitter inside each export's slot.
            var slot = (endNanos - startNanos) / exportsNeeded;
            var baseNanos = startNanos + slot * i + (long)(rng.NextDouble() * slot);
            var built = shaper.NextAt(baseNanos, i % Math.Max(1, exportsNeeded / 40) == 0 ? sampled : null);
            payloads.Add((built, baseNanos));
        }

        long sent = 0, failed = 0;
        async Task Send(Payload<ExportTraceServiceRequest> p)
        {
            var result = await exporter.ExportTracesAsync(p, redelivery: false, ct);
            if (result.Outcome == ExportOutcome.Ok) Interlocked.Add(ref sent, p.Records);
            else Interlocked.Add(ref failed, p.Records);
        }
        await Parallel.ForEachAsync(payloads, new ParallelOptions { MaxDegreeOfParallelism = ParallelExports, CancellationToken = ct },
            async (item, _) => await Send(item.Payload));

        // Large traces, for the first tenant: one trace of N spans, sent in chunks (a single export of 20,000 spans is over the gRPC message limit).
        var traces = new List<SeededTrace>(sampled);
        var largeBase = endNanos - 3_600_000_000_000L;
        foreach (var size in largeTraceSizes)
        {
            var (chunks, trace) = shaper.BuildLargeTrace(0, largeBase, size, LargeTraceChunkSpans);
            foreach (var chunk in chunks) await Send(chunk);
            traces.Add(trace);
            largeBase += 60_000_000_000L;
        }

        watch.Stop();
        log($"seeded {sent:N0} spans ({failed:N0} failed) in {watch.Elapsed.TotalSeconds:N0} s");
        return new HistorySeedResult(days, spansPerDay, sent, failed, watch.Elapsed.TotalSeconds, traces);
    }
}
