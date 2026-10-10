using System.Diagnostics.Metrics;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Threading;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Collector improvements phase 1, no database: the gate's bounded wait, the batch bisector, and the shared worker's
/// handling of a permanently failing record (a fake bulk writer and classifier).
/// </summary>
[Trait("Suite", "Backpressure")]
[Collection(IngestionMeterCollection.Name)]
public class BackpressureUnitTests
{
    // ---- RecordCountGate.TryAcquireAsync ----

    [Fact]
    public async Task TryAcquire_admits_when_room_appears_within_the_wait()
    {
        var gate = new RecordCountGate(10);
        await gate.AcquireAsync(10, CancellationToken.None);
        _ = Task.Run(async () => { await Task.Delay(100); gate.Release(10); });

        Assert.True(await gate.TryAcquireAsync(5, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(5, gate.Resident);
    }

    [Fact]
    public async Task TryAcquire_refuses_after_the_wait_and_reserves_nothing()
    {
        var gate = new RecordCountGate(10);
        await gate.AcquireAsync(10, CancellationToken.None);

        var started = DateTime.UtcNow;
        Assert.False(await gate.TryAcquireAsync(1, TimeSpan.FromMilliseconds(200), CancellationToken.None));
        var elapsed = DateTime.UtcNow - started;

        Assert.InRange(elapsed.TotalMilliseconds, 150, 1500);
        Assert.Equal(10, gate.Resident);
    }

    [Fact]
    public async Task TryAcquire_with_a_zero_wait_refuses_at_once_when_full_and_admits_when_not()
    {
        var gate = new RecordCountGate(10);
        Assert.True(await gate.TryAcquireAsync(10, TimeSpan.Zero, CancellationToken.None));
        Assert.False(await gate.TryAcquireAsync(1, TimeSpan.Zero, CancellationToken.None));
        gate.Release(10);
        Assert.True(await gate.TryAcquireAsync(1, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task TryAcquire_still_admits_an_oversized_batch_into_an_empty_gate()
    {
        var gate = new RecordCountGate(10);
        Assert.True(await gate.TryAcquireAsync(50, TimeSpan.Zero, CancellationToken.None));
        // ...and holds everything else back until it is released.
        Assert.False(await gate.TryAcquireAsync(1, TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task TryAcquire_honours_cancellation()
    {
        var gate = new RecordCountGate(1);
        await gate.AcquireAsync(1, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.TryAcquireAsync(1, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task SaturatedFor_starts_at_the_first_refusal_and_clears_when_the_gate_has_room_again()
    {
        var gate = new RecordCountGate(2);
        await gate.AcquireAsync(2, CancellationToken.None);
        Assert.True(gate.IsSaturated);
        Assert.Equal(TimeSpan.Zero, gate.SaturatedFor);          // full, but nobody has been refused yet

        Assert.False(await gate.TryAcquireAsync(1, TimeSpan.Zero, CancellationToken.None));
        await Task.Delay(30);
        Assert.True(gate.SaturatedFor > TimeSpan.Zero);

        gate.Release(1);
        Assert.False(gate.IsSaturated);
        Assert.Equal(TimeSpan.Zero, gate.SaturatedFor);
    }

    [Fact]
    public async Task Channel_precheck_refuses_only_after_the_gate_has_refused_for_the_whole_wait()
    {
        var options = Options.Create(new TelemetryIngestionOptions { MaxQueuedSpans = 2, MaxGateWaitMilliseconds = 100, RejectRetryDelayMilliseconds = 1000 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(options, metrics);

        await channel.TraceGate.AcquireAsync(2, CancellationToken.None);
        channel.ThrowIfSaturated("traces", channel.TraceGate);   // full, but only just: the next flush may free room within the wait

        var ex = await Assert.ThrowsAsync<IngestionRejectedException>(() => channel.AcquireOrRejectAsync("traces", channel.TraceGate, 1, CancellationToken.None));
        Assert.Equal(RefusalReasons.Throttled, ex.Reason);
        Assert.InRange(ex.RetryAfter.TotalMilliseconds, 1000, 1500);   // base delay plus up to 50% jitter

        await Task.Delay(120);
        Assert.Throws<IngestionRejectedException>(() => channel.ThrowIfSaturated("traces", channel.TraceGate));

        channel.TraceGate.Release(2);
        channel.ThrowIfSaturated("traces", channel.TraceGate);   // room again
    }

    [Fact]
    public async Task A_negative_wait_keeps_the_unbounded_behaviour()
    {
        var options = Options.Create(new TelemetryIngestionOptions { MaxQueuedSpans = 1, MaxGateWaitMilliseconds = -1 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(options, metrics);
        await channel.TraceGate.AcquireAsync(1, CancellationToken.None);

        var waiting = channel.AcquireOrRejectAsync("traces", channel.TraceGate, 1, CancellationToken.None);
        await Task.Delay(400);
        Assert.False(waiting.IsCompleted);
        channel.TraceGate.Release(1);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- BatchBisector ----

    private static Task<FlushOutcome> FlushUnless(List<int> piece, Func<int, bool> poison) =>
        Task.FromResult(piece.Any(poison) ? FlushOutcome.Permanent : FlushOutcome.Ok);

    [Fact]
    public async Task Bisector_drops_only_the_poison_records_and_stores_every_other_exactly_once()
    {
        var batch = Enumerable.Range(0, 1000).ToList();
        var poison = new HashSet<int> { 17, 63, 999 };

        var result = await BatchBisector.RunAsync(batch, (p, _) => FlushUnless(p, poison.Contains), 64, CancellationToken.None);

        Assert.Equal(poison.OrderBy(x => x), result.Permanent.OrderBy(x => x));
        var stored = result.Flushed.SelectMany(p => p).ToList();
        Assert.Equal(997, stored.Count);
        Assert.Equal(stored.Count, stored.Distinct().Count());
        Assert.DoesNotContain(stored, poison.Contains);
        Assert.Equal(0, result.CapDropped);
        Assert.Equal(0, result.Exhausted);
    }

    [Fact]
    public async Task Bisector_stops_at_the_cap_and_counts_the_remainder_as_dropped()
    {
        var batch = Enumerable.Range(0, 1000).ToList();
        var result = await BatchBisector.RunAsync(batch, (p, _) => FlushUnless(p, _ => true), 10, CancellationToken.None);

        Assert.Equal(10, result.Flushes);
        Assert.True(result.CapDropped > 0);
        Assert.Equal(1000, result.Permanent.Count + result.Exhausted + result.CapDropped + result.Flushed.Sum(p => p.Count));
    }

    [Fact]
    public async Task Bisector_counts_a_piece_whose_retries_run_out_as_exhausted()
    {
        var batch = Enumerable.Range(0, 8).ToList();
        var result = await BatchBisector.RunAsync(batch,
            (p, _) => Task.FromResult(p.Count == 8 ? FlushOutcome.Permanent : p.Contains(0) ? FlushOutcome.Exhausted : FlushOutcome.Ok), 64, CancellationToken.None);

        Assert.Equal(4, result.Exhausted);
        Assert.Equal(4, result.Flushed.Sum(p => p.Count));
    }

    // ---- the shared worker ----

    private sealed class PoisonException : Exception;

    private sealed class PoisonClassifier : IFlushErrorClassifier
    {
        public FlushErrorKind Classify(Exception exception) => exception is PoisonException ? FlushErrorKind.Permanent : FlushErrorKind.Transient;
    }

    private sealed class SpanWriter : ITelemetryBulkWriter
    {
        public readonly List<string> Stored = [];
        public int Calls;
        public int TransientFailuresLeft;

        public Task FlushTracesAsync(List<SpanModel> spans, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (Interlocked.Decrement(ref TransientFailuresLeft) >= 0) throw new InvalidOperationException("transient");
            if (spans.Any(s => s.Name == "poison")) throw new PoisonException();
            lock (Stored) Stored.AddRange(spans.Select(s => s.SpanIdHex));
            return Task.CompletedTask;
        }
        public Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Counters : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _sums = [];
        public Counters()
        {
            _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Keryhe.Telemetry.Ingestion") l.EnableMeasurementEvents(i); };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
            {
                var reason = tags.ToArray().FirstOrDefault(t => t.Key == "reason").Value as string;
                var key = i.Name.Replace("keryhe.telemetry.ingestion.", "") + (reason is null ? "" : ":" + reason);
                lock (_sums) _sums[key] = _sums.GetValueOrDefault(key) + v;
            });
            _listener.Start();
        }
        public long Of(string key) { lock (_sums) return _sums.GetValueOrDefault(key); }
        public void Dispose() => _listener.Dispose();
    }

    private static SpanModel Span(int i, string? name = null) => new()
    {
        TraceIdHex = new string('a', 32), SpanIdHex = i.ToString("x16"), Name = name ?? "op",
        StartTimeUnixNano = 1, EndTimeUnixNano = 2, Resource = new ResourceModel { TenantId = 1 }
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        return condition();
    }

    [Fact]
    public async Task Worker_stores_every_record_but_the_poison_one_and_releases_the_gate()
    {
        using var counters = new Counters();
        var writer = new SpanWriter();
        var opts = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 1, RetryMaxDelayMilliseconds = 2, MaxTraceFlushSpanBatchSize = 500 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(),
            NullLogger<TelemetryIngestionWorker>.Instance, new PoisonClassifier());

        // One merged batch: 200 spans from two exports, one of them poison.
        var a = Enumerable.Range(0, 100).Select(i => Span(i)).ToList();
        var b = Enumerable.Range(100, 100).Select(i => Span(i, i == 150 ? "poison" : null)).ToList();
        await channel.TraceGate.AcquireAsync(200, CancellationToken.None);
        await channel.Traces.Writer.WriteAsync(a);
        await channel.Traces.Writer.WriteAsync(b);
        await worker.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => counters.Of("records_flushed") + counters.Of("records_dropped:permanent") == 200));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(199, writer.Stored.Count);
        Assert.Equal(199, writer.Stored.Distinct().Count());
        Assert.DoesNotContain((150).ToString("x16"), writer.Stored);
        Assert.Equal(1, counters.Of("records_dropped:permanent"));
        Assert.Equal(0, counters.Of("records_dropped:retries_exhausted"));
        Assert.Equal(0, counters.Of("flush_retries"));       // a permanent error is never retried
        Assert.Equal(0, channel.TraceGate.Resident);
    }

    [Fact]
    public async Task Worker_still_retries_a_transient_error_and_does_not_split_it()
    {
        using var counters = new Counters();
        var writer = new SpanWriter { TransientFailuresLeft = 2 };
        var opts = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 1, RetryMaxDelayMilliseconds = 2 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(),
            NullLogger<TelemetryIngestionWorker>.Instance, new PoisonClassifier());

        await channel.TraceGate.AcquireAsync(50, CancellationToken.None);
        await channel.Traces.Writer.WriteAsync(Enumerable.Range(0, 50).Select(i => Span(i)).ToList());
        await worker.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => counters.Of("records_flushed") == 50));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, counters.Of("flush_retries"));
        Assert.Equal(3, writer.Calls);                         // two failures, one success: one batch, never split
        Assert.Equal(0, counters.Of("records_dropped:permanent"));
    }

    [Fact]
    public async Task Without_a_classifier_a_poison_batch_is_retried_then_dropped_whole_as_before()
    {
        using var counters = new Counters();
        var writer = new SpanWriter();
        var opts = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 1, RetryMaxDelayMilliseconds = 2, MaxFlushRetries = 2 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance);

        await channel.TraceGate.AcquireAsync(10, CancellationToken.None);
        await channel.Traces.Writer.WriteAsync(Enumerable.Range(0, 10).Select(i => Span(i, i == 3 ? "poison" : null)).ToList());
        await worker.StartAsync(CancellationToken.None);

        Assert.True(await WaitUntilAsync(() => counters.Of("records_dropped:retries_exhausted") == 10));
        await worker.StopAsync(CancellationToken.None);
        Assert.Empty(writer.Stored);
        Assert.Equal(0, channel.TraceGate.Resident);
    }

    [Fact]
    public async Task Worker_releases_the_bytes_of_every_merged_export_with_their_records()
    {
        var writer = new SpanWriter();
        var opts = Options.Create(new TelemetryIngestionOptions { MaxTraceFlushSpanBatchSize = 500 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance);

        foreach (var bytes in new long[] { 1_000, 2_500, 400 })
        {
            var export = Enumerable.Range(0, 10).Select(i => Span(i)).ToList();
            await channel.TraceGate.AcquireAsync(export.Count, bytes, CancellationToken.None);
            channel.MarkEnqueued(export, bytes);
            await channel.Traces.Writer.WriteAsync(export);
        }
        Assert.Equal(3_900, channel.TraceGate.ResidentBytes);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => channel.TraceGate.Resident == 0));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, channel.TraceGate.ResidentBytes);
    }

    [Fact]
    public async Task Worker_gives_each_tenant_its_share_back_when_exports_of_several_tenants_are_merged()
    {
        var writer = new SpanWriter();
        var opts = Options.Create(new TelemetryIngestionOptions { MaxTraceFlushSpanBatchSize = 500 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance);

        foreach (var (tenant, count) in new[] { (1L, 10), (2L, 20), (1L, 5) })
        {
            var export = Enumerable.Range(0, count).Select(i => { var s = Span(i); s.Resource = new ResourceModel { TenantId = tenant }; return s; }).ToList();
            await channel.AcquireOrRejectAsync("traces", channel.TraceGate, count, CancellationToken.None, bytes: 100, tenantId: tenant);
            channel.MarkEnqueued(export, 100);
            await channel.Traces.Writer.WriteAsync(export);
        }
        Assert.Equal(new Dictionary<long, int> { [1] = 15, [2] = 20 }, channel.TraceGate.TenantResident());

        await worker.StartAsync(CancellationToken.None);
        Assert.True(await WaitUntilAsync(() => channel.TraceGate.Resident == 0));
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(channel.TraceGate.TenantResident());
        Assert.Equal(0, channel.TraceGate.ResidentBytes);
    }
}
