using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Pure-logic tests for the summary rollups (plans/summary-rollups.md): bands, percentiles, accumulator, worker. No Docker.</summary>
public class RollupUnitTests
{
    private const long Minute = 60_000_000_000L;
    private static readonly long T0 = 1_800_000_000L * 1_000_000_000L / Minute * Minute; // a minute boundary

    // ---- DurationBands ----

    [Fact]
    public void Bands_EveryEdgeAndItsNeighbours()
    {
        Assert.Equal(0, DurationBands.IndexOf(0));
        Assert.Equal(0, DurationBands.IndexOf(DurationBands.FirstEdgeNanos - 1));
        for (var band = 1; band < DurationBands.Count; band++)
        {
            var lower = DurationBands.LowerEdgeNanos(band);
            Assert.Equal(band, DurationBands.IndexOf(lower));
            Assert.Equal(band - 1, DurationBands.IndexOf(lower - 1));
            Assert.Equal(band, DurationBands.IndexOf(lower + 1));
            if (band < DurationBands.Count - 1)
                Assert.Equal(band, DurationBands.IndexOf(DurationBands.UpperEdgeNanos(band) - 1));
        }
    }

    [Fact]
    public void Bands_NegativeCountsAsZero_AndHugeIsTheOpenBand()
    {
        Assert.Equal(0, DurationBands.IndexOf(-5_000_000));
        Assert.Equal(23, DurationBands.IndexOf(1_048_576_000_000L));
        Assert.Equal(22, DurationBands.IndexOf(1_048_575_999_999L));
        Assert.Equal(23, DurationBands.IndexOf(long.MaxValue));
        Assert.Equal(1_048_576_000_000L, DurationBands.LowerEdgeNanos(23));
    }

    [Fact]
    public void Percentile_EmptyIsZero_SingleBandInterpolatesGeometrically_AndClampsToMax()
    {
        var empty = new long[DurationBands.Count];
        Assert.Equal(0, DurationBands.Percentile(empty, 0.5, 0));

        var one = new long[DurationBands.Count];
        one[3] = 10; // [1 ms, 2 ms)
        var p0 = DurationBands.Percentile(one, 0, 0);
        var p50 = DurationBands.Percentile(one, 0.5, 0);
        var p100 = DurationBands.Percentile(one, 1, 0);
        Assert.InRange(p50, 1_000_000, 2_000_000);
        Assert.Equal(1_000_000 * Math.Sqrt(2), p50, 1);
        Assert.True(p0 <= p50 && p50 <= p100);
        Assert.Equal(1_200_000, DurationBands.Percentile(one, 1, 1_200_000));

        var band0 = new long[DurationBands.Count];
        band0[0] = 4;
        Assert.Equal(125_000, DurationBands.Percentile(band0, 0.5, 0), 1); // linear from 0 in band 0

        var open = new long[DurationBands.Count];
        open[23] = 2;
        Assert.InRange(DurationBands.Percentile(open, 0.5, 0), 1_048_576_000_000d, 2_097_152_000_000d);
        Assert.Equal(2_000_000_000_000d, DurationBands.Percentile(open, 1, 2_000_000_000_000L));
    }

    // ---- RollupAccumulator ----

    private static SpanModel Span(long tenant, string? service, SpanKind kind, long start, long durationNanos, bool error = false, ResourceModel? resource = null) => new()
    {
        TraceIdHex = new string('a', 32), SpanIdHex = new string('b', 16), Name = "op", Kind = kind,
        StartTimeUnixNano = start, EndTimeUnixNano = start + durationNanos,
        StatusCode = error ? SpanStatusCode.ERROR : SpanStatusCode.OK,
        Resource = resource ?? (service == null
            ? new ResourceModel { TenantId = tenant, Attributes = new() }
            : SeededDataBuilder.Resource(tenant, service))
    };

    private static LogRecordModel Log(long tenant, string service, long time, int? severity) => new()
    {
        TimeUnixNano = time, SeverityNumber = severity, Resource = SeededDataBuilder.Resource(tenant, service)
    };

    [Fact]
    public void Accumulator_CountsOnlyInboundSpans_WithErrorsDurationsAndBands()
    {
        var acc = new RollupAccumulator();
        acc.AddSpans([
            Span(1, "web", SpanKind.SERVER, T0 + 1, 2_000_000),
            Span(1, "web", SpanKind.CONSUMER, T0 + 2, 300_000, error: true),
            Span(1, "web", SpanKind.CLIENT, T0 + 3, 9_000_000),
            Span(1, "web", SpanKind.INTERNAL, T0 + 4, 9_000_000),
            Span(1, "web", SpanKind.PRODUCER, T0 + 5, 9_000_000),
            Span(1, "web", SpanKind.UNSPECIFIED, T0 + 6, 9_000_000),
            Span(1, "web", SpanKind.SERVER, T0 + 7, -1_000_000), // clock skew counts as 0
            Span(1, "api", SpanKind.SERVER, T0 + Minute + 1, 5_000_000),
            Span(2, "web", SpanKind.SERVER, T0 + 8, 1_000_000)
        ]);

        var (requests, logs) = acc.DrainAll();
        Assert.Empty(logs);
        Assert.Equal(3, requests.Count);

        var web = requests.Single(r => r.TenantId == 1 && r.ServiceName == "web" && r.BucketStartUnixNano == T0);
        Assert.Equal(3, web.RequestCount);
        Assert.Equal(1, web.ErrorCount);
        Assert.Equal(2_300_000, web.SumDurationNanos);
        Assert.Equal(2_000_000, web.MaxDurationNanos);
        Assert.Equal(1, web.Bands[0]);   // the skewed span counts as 0
        Assert.Equal(1, web.Bands[1]);   // 0.3 ms
        Assert.Equal(1, web.Bands[4]);   // 2 ms
        Assert.Equal(3, web.Bands.Sum());

        // Sorted by tenant, minute, service.
        Assert.Equal([(1L, T0, "web"), (1L, T0 + Minute, "api"), (2L, T0, "web")],
            requests.Select(r => (r.TenantId, r.BucketStartUnixNano, r.ServiceName)).ToArray());
    }

    [Fact]
    public void Accumulator_ServiceWithoutName_FilesUnderEmptyString()
    {
        var acc = new RollupAccumulator();
        acc.AddSpans([Span(1, null, SpanKind.SERVER, T0, 1_000_000)]);
        var (requests, _) = acc.DrainAll();
        Assert.Equal("", Assert.Single(requests).ServiceName);
    }

    [Fact]
    public void Accumulator_LogSeverity_NullBecomesMinusOne()
    {
        var acc = new RollupAccumulator();
        acc.AddLogs([Log(1, "web", T0, null), Log(1, "web", T0 + 1, null), Log(1, "web", T0 + 2, 17), Log(1, "web", T0 + Minute, 17)]);
        var (_, logs) = acc.DrainAll();
        Assert.Equal(3, logs.Count);
        Assert.Equal(2, logs.Single(l => l.SeverityNumber == -1).RecordCount);
        Assert.Equal(1, logs.Single(l => l.SeverityNumber == 17 && l.BucketStartUnixNano == T0).RecordCount);
        Assert.Equal(-1, logs[0].SeverityNumber); // severity is the last sort key within a minute
    }

    [Fact]
    public void Accumulator_DrainClosed_LeavesOpenMinutes_AndALateSpanComesOutAsASecondRow()
    {
        var acc = new RollupAccumulator();
        acc.AddSpans([Span(1, "web", SpanKind.SERVER, T0 + 5, 1_000_000), Span(1, "web", SpanKind.SERVER, T0 + Minute + 5, 1_000_000)]);

        // Cutoff is exactly the end of the first minute: it closes, the second stays open.
        var (first, _) = acc.DrainClosed(T0 + Minute);
        Assert.Equal(T0, Assert.Single(first).BucketStartUnixNano);
        Assert.Equal(1, acc.HeldRows);

        // A late span for the already-written minute starts it again and is written as another row.
        acc.AddSpans([Span(1, "web", SpanKind.SERVER, T0 + 9, 1_000_000)]);
        var (second, _) = acc.DrainClosed(T0 + Minute);
        var late = Assert.Single(second);
        Assert.Equal(T0, late.BucketStartUnixNano);
        Assert.Equal(1, late.RequestCount);

        var (rest, _) = acc.DrainClosed(T0 + Minute + 59_999_999_999L);
        Assert.Empty(rest);
        var (all, _) = acc.DrainAll();
        Assert.Equal(T0 + Minute, Assert.Single(all).BucketStartUnixNano);
        Assert.Equal(0, acc.HeldRows);
    }

    [Fact]
    public void Accumulator_Disabled_DoesNotHoldAnything()
    {
        var acc = new RollupAccumulator { Enabled = false };
        acc.AddSpans([Span(1, "web", SpanKind.SERVER, T0, 1)]);
        acc.AddLogs([Log(1, "web", T0, 9)]);
        Assert.Equal(0, acc.HeldRows);
    }

    [Fact]
    public async Task Accumulator_ConcurrentAddsAndDrains_LoseNothing()
    {
        var acc = new RollupAccumulator();
        const int threads = 8, batches = 400, perBatch = 25;
        var drained = new List<RequestRollupRow>();
        var drainedLogs = new List<LogRollupRow>();
        var stop = false;

        var drainer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var (r, l) = acc.DrainAll(); // includes open minutes: the harshest racing case
                lock (drained) { drained.AddRange(r); drainedLogs.AddRange(l); }
            }
        });

        var producers = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var b = 0; b < batches; b++)
            {
                var spans = new List<SpanModel>();
                var logs = new List<LogRecordModel>();
                for (var i = 0; i < perBatch; i++)
                {
                    spans.Add(Span(1, "web", SpanKind.SERVER, T0 + (i % 3) * Minute + t, 1_000_000 * (i + 1), error: i % 5 == 0));
                    logs.Add(Log(1, "web", T0 + (i % 3) * Minute, 9 + i % 2));
                }
                acc.AddSpans(spans);
                acc.AddLogs(logs);
            }
        })).ToArray();
        await Task.WhenAll(producers);
        Volatile.Write(ref stop, true);
        await drainer;

        var (restR, restL) = acc.DrainAll();
        drained.AddRange(restR);
        drainedLogs.AddRange(restL);

        long expected = threads * batches * perBatch;
        Assert.Equal(expected, drained.Sum(r => r.RequestCount));
        Assert.Equal(expected, drained.Sum(r => r.Bands.Sum()));
        Assert.Equal(threads * batches * (perBatch / 5), drained.Sum(r => r.ErrorCount));
        Assert.Equal(expected, drainedLogs.Sum(l => l.RecordCount));
        Assert.Equal(threads * batches * Enumerable.Range(0, perBatch).Sum(i => 1_000_000L * (i + 1)),
            drained.Sum(r => r.SumDurationNanos));
    }

    // ---- RollupWorker ----

    private sealed class FakeStore : IRollupStore
    {
        public bool FedByViews { get; init; }
        public bool Fail { get; set; }
        public readonly List<RequestRollupRow> Requests = [];
        public readonly List<LogRollupRow> Logs = [];
        public readonly List<int> RequestCalls = [];

        public Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("down");
            RequestCalls.Add(rows.Count);
            Requests.AddRange(rows);
            return Task.CompletedTask;
        }

        public Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("down");
            Logs.AddRange(rows);
            return Task.CompletedTask;
        }
    }

    private static (RollupWorker Worker, RollupAccumulator Acc, FakeStore Store, IngestionMetrics Metrics) BuildWorker(
        FakeStore store, RollupOptions? options = null)
    {
        var services = new ServiceCollection().AddSingleton<IRollupStore>(store).BuildServiceProvider();
        var acc = new RollupAccumulator();
        var metrics = new IngestionMetrics();
        var worker = new RollupWorker(services.GetRequiredService<IServiceScopeFactory>(), acc,
            Options.Create(options ?? new RollupOptions { FlushIntervalSeconds = 3600, MaxBatchSize = 2 }),
            metrics, NullLogger<RollupWorker>.Instance);
        return (worker, acc, store, metrics);
    }

    [Fact]
    public async Task Worker_Shutdown_WritesEverythingIncludingOpenMinutes_InCappedBatches()
    {
        var (worker, acc, store, metrics) = BuildWorker(new FakeStore());
        using var _ = metrics;
        await worker.StartAsync(CancellationToken.None);

        var now = DateTime.UtcNow;
        var start = SeededDataBuilder.ToUnixNano(now);
        acc.AddSpans(Enumerable.Range(0, 5).Select(i => Span(1, $"svc{i}", SpanKind.SERVER, start, 1_000_000)).ToList());
        acc.AddLogs([Log(1, "svc0", start, 9)]);

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(5, store.Requests.Count);
        Assert.Equal([2, 2, 1], store.RequestCalls); // MaxBatchSize 2
        Assert.Single(store.Logs);
        Assert.Equal(0, acc.HeldRows);
    }

    [Fact]
    public async Task Worker_FedByViews_StaysIdle_AndDisablesTheAccumulator()
    {
        var (worker, acc, store, metrics) = BuildWorker(new FakeStore { FedByViews = true });
        using var _ = metrics;
        await worker.StartAsync(CancellationToken.None);
        Assert.False(acc.Enabled);

        acc.AddSpans([Span(1, "web", SpanKind.SERVER, T0, 1)]);
        await worker.StopAsync(CancellationToken.None);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task Worker_FailedAppend_KeepsRowsAndRetries_AndTheBufferIsBounded()
    {
        var store = new FakeStore { Fail = true };
        var (worker, acc, _, metrics) = BuildWorker(store, new RollupOptions { FlushIntervalSeconds = 3600, MaxBatchSize = 10, MaxBufferedRows = 3 });
        using var _ = metrics;
        // Not started: the background loop's own first cycle would race the explicit flushes below.

        var closed = new List<SpanModel>();
        for (var i = 0; i < 5; i++)
            closed.Add(Span(1, "web", SpanKind.SERVER, T0 + i * Minute, 1_000_000));
        acc.AddSpans(closed);

        // One cycle with the store down: five rows drained, three kept (the newest).
        var (requests, logs) = acc.DrainAll();
        await worker.FlushAsync(requests, logs, CancellationToken.None);
        Assert.Empty(store.Requests);

        store.Fail = false;
        await worker.FlushAsync([], [], CancellationToken.None);
        Assert.Equal(3, store.Requests.Count);
        Assert.Equal([T0 + 2 * Minute, T0 + 3 * Minute, T0 + 4 * Minute], store.Requests.Select(r => r.BucketStartUnixNano).ToArray());
    }
}
