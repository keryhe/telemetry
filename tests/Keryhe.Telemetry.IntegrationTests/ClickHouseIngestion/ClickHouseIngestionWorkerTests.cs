using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.ClickHouseIngestion;

/// <summary>
/// <see cref="ClickHouseIngestionWorker"/> against a fake writer (no Docker): day buffers, linger, tokens across
/// retries, the late-buffer cap, the retention filter, gate release and shutdown. Lingers are real time, so they are
/// short.
/// </summary>
[Trait("Suite", "ClickHouseIngestion")]
public sealed class ClickHouseIngestionWorkerTests
{
    private sealed record Call(string Signal, string Token, int Count, HashSet<DateOnly> Days);

    private sealed class FakeWriter : IClickHouseTokenWriter
    {
        public ConcurrentQueue<Call> Calls { get; } = new();
        public int FailAttempts;      // the next N attempts (across calls) throw

        private Task Record(string signal, string token, IEnumerable<long> nanos, int count)
        {
            Calls.Enqueue(new Call(signal, token, count, nanos.Select(DayOf).ToHashSet()));
            if (Interlocked.Decrement(ref FailAttempts) >= 0) throw new InvalidOperationException("injected");
            return Task.CompletedTask;
        }

        public Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default) =>
            Record("logs", token, records.Select(r => r.TimeUnixNano!.Value), records.Count);
        public Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default) =>
            Record("traces", token, spans.Select(s => s.StartTimeUnixNano), spans.Count);
        public Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default) =>
            Record("metrics", token, metrics.Select(m => m.GaugeDataPoints![0].TimeUnixNano), metrics.Count);
    }

    private sealed class FakeRetention(int? traceDays = null, int? metricDays = null) : IRetentionWindows
    {
        public DateTime? OldestAllowedUtc(string signal) =>
            signal == "traces" && traceDays is { } d ? DateTime.UtcNow.AddDays(-d)
            : signal == "metrics" && metricDays is { } m ? DateTime.UtcNow.AddDays(-m) : null;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public int RefreshSeconds => 3600;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeWriter Writer { get; } = new();
        public TelemetryIngestionChannel Channel { get; }
        public ClickHouseIngestionWorker Worker { get; }
        public IngestionMetrics Metrics { get; } = new();
        private readonly MeterListener _listener = new();
        public ConcurrentBag<(string Name, double Value, string? Reason)> Measurements { get; } = new();

        public Harness(ClickHouseIngestionOptions? options = null, int? traceRetentionDays = null, int maxRetries = 5, int? metricRetentionDays = null)
        {
            options ??= new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 60_000 };
            var shared = Options.Create(new TelemetryIngestionOptions { MaxFlushRetries = maxRetries, RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 });
            Channel = new TelemetryIngestionChannel(shared, Metrics);
            Worker = new ClickHouseIngestionWorker(Writer, Channel, Options.Create(options), shared, Metrics,
                new FakeRetention(traceRetentionDays, metricRetentionDays), TimeProvider.System, NullLogger<ClickHouseIngestionWorker>.Instance);

            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Keryhe.Telemetry.Ingestion") l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Measurements.Add((i.Name, v, Reason(tags))));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Measurements.Add((i.Name, v, Reason(tags))));
            _listener.Start();
        }

        private static string? Reason(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var t in tags) if (t.Key == "reason") return t.Value as string;
            return null;
        }

        public double Sum(string suffix, string? reason = null) =>
            Measurements.Where(m => m.Name.EndsWith(suffix) && (reason is null || m.Reason == reason)).Sum(m => m.Value);

        public int Count(string suffix) => Measurements.Count(m => m.Name.EndsWith(suffix));

        public Task StartAsync() => Worker.StartAsync(CancellationToken.None);
        public Task StopAsync() => Worker.StopAsync(CancellationToken.None);

        public async Task EnqueueSpansAsync(params SpanModel[] spans)
        {
            var list = spans.ToList();
            await Channel.TraceGate.AcquireAsync(list.Count, CancellationToken.None);
            Channel.MarkEnqueued(list);
            await Channel.Traces.Writer.WriteAsync(list);
        }

        public async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            Worker.Dispose();
            Metrics.Dispose();
            await Task.CompletedTask;
        }
    }

    private static DateOnly DayOf(long nano) => DateOnly.FromDateTime(DateTime.UnixEpoch.AddTicks(nano / 100));

    private static long DaysAgo(int days, int offsetSeconds = 0) =>
        (DateTime.UtcNow.AddDays(-days).AddSeconds(offsetSeconds) - DateTime.UnixEpoch).Ticks * 100;

    // Mid-day-safe: "today" may be a few seconds from midnight, so tests that need a current record use now - 1 s only
    // when that is still today; otherwise they are retried by the day boundary being rare. Spans use now.
    private static SpanModel Span(long startNano) => new() { TraceIdHex = new string('a', 32), SpanIdHex = new string('b', 16), Name = "s", StartTimeUnixNano = startNano, EndTimeUnixNano = startNano + 1000 };

    [Fact]
    public async Task ThreeDays_InOneDrain_AreThreeInserts_WithThreeTokens_AndTheGateIsReleased()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 150 });
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(1)), Span(DaysAgo(1)), Span(DaysAgo(2)));
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 3));
        await h.StopAsync();

        var calls = h.Writer.Calls.ToList();
        Assert.Equal(3, calls.Count);
        Assert.Equal(3, calls.Select(c => c.Token).Distinct().Count());
        Assert.All(calls, c => Assert.Single(c.Days));
        Assert.Equal([1, 1, 2], calls.Select(c => c.Count).Order());
        Assert.Equal(0, h.Channel.TraceGate.Resident);
        Assert.Equal(4, h.Sum("records_flushed"));
    }

    [Fact]
    public async Task ARetryReusesItsToken_AndTheBatchIsFlushedOnce()
    {
        await using var h = new Harness();
        h.Writer.FailAttempts = 2;
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)));
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 3));
        await h.StopAsync();

        Assert.Single(h.Writer.Calls.Select(c => c.Token).Distinct());
        Assert.Equal(2, h.Count("flush_retries"));
        Assert.Equal(1, h.Sum("records_flushed"));
        Assert.Equal(0, h.Channel.TraceGate.Resident);
    }

    [Fact]
    public async Task ExhaustedRetries_DropTheBatchAndReleaseTheGate()
    {
        await using var h = new Harness(maxRetries: 1);
        h.Writer.FailAttempts = 100;
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(0)));
        Assert.True(await h.WaitAsync(() => h.Channel.TraceGate.Resident == 0 && h.Sum("records_dropped") == 2));
        await h.StopAsync();

        Assert.Equal(2, h.Sum("records_dropped", "retries_exhausted"));
        Assert.Equal(2, h.Writer.Calls.Count); // first attempt + 1 retry
    }

    [Fact]
    public async Task CurrentDay_FlushesOnLinger_LateDay_OnLateLinger_Not_Before()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 700 });
        await h.StartAsync();
        var start = DateTime.UtcNow;
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(3)));

        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 1, 2000));
        Assert.True((DateTime.UtcNow - start).TotalMilliseconds < 650, "the current day flushed on its own linger");
        Assert.Equal(DayOf(DaysAgo(0)), h.Writer.Calls.Single().Days.Single());

        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 2, 3000));
        Assert.True((DateTime.UtcNow - start).TotalMilliseconds >= 650, "the late day waited for the late linger");
        await h.StopAsync();
    }

    [Fact]
    public async Task MoreLateDayBuffersThanTheCap_FlushTheOldestEarly()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 60_000, MaxLateDayBuffers = 2 });
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(1)), Span(DaysAgo(2)), Span(DaysAgo(3)), Span(DaysAgo(4)));
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 2));
        var flushed = h.Writer.Calls.SelectMany(c => c.Days).Order().ToList();
        Assert.Equal([DayOf(DaysAgo(4)), DayOf(DaysAgo(3))], flushed);

        await Task.Delay(200);
        Assert.Equal(2, h.Writer.Calls.Count); // the two newest stay buffered until shutdown
        await h.StopAsync();
        Assert.Equal(4, h.Writer.Calls.Count);
        Assert.Equal(0, h.Channel.TraceGate.Resident);
    }

    [Fact]
    public async Task OutOfRetentionRecords_AreDropped_Counted_AndReleased()
    {
        await using var h = new Harness(traceRetentionDays: 7);
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(30)), Span(DaysAgo(40)));
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 1 && h.Channel.TraceGate.Resident == 0));
        await h.StopAsync();

        Assert.Equal(1, h.Writer.Calls.Single().Count);
        Assert.Equal(2, h.Sum("records_dropped", "out_of_retention"));
    }

    [Fact]
    public async Task AMetricIsDroppedOnlyWhenEveryPointIsOutOfRetention()
    {
        await using var h = new Harness(metricRetentionDays: 7);
        await h.StartAsync();
        MetricModel Metric(params int[] daysAgo) => new()
        {
            Name = "m", Type = MetricType.GAUGE,
            GaugeDataPoints = daysAgo.Select(d => new GaugeDataPointModel { TimeUnixNano = DaysAgo(d) }).ToList()
        };
        // An old point first, then a current one: the metric's current point must survive. All points old: dropped.
        var list = new List<MetricModel> { Metric(40, 0), Metric(40, 30) };
        await h.Channel.MetricGate.AcquireAsync(list.Count, CancellationToken.None);
        h.Channel.MarkEnqueued(list);
        await h.Channel.Metrics.Writer.WriteAsync(list);
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 1 && h.Channel.MetricGate.Resident == 0));
        await h.StopAsync();

        Assert.Equal(1, h.Writer.Calls.Single().Count);
        Assert.Equal(1, h.Sum("records_dropped", "out_of_retention"));
    }

    [Fact]
    public async Task CommitLag_IsRecordedOncePerExport()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 150 });
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(2))); // one export spanning two days
        await h.EnqueueSpansAsync(Span(DaysAgo(0)));
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 2 && h.Count("commit_lag") >= 2));
        await h.StopAsync();
        Assert.Equal(2, h.Count("commit_lag"));
    }

    [Fact]
    public async Task Shutdown_FlushesEverythingBufferedRegardlessOfLinger()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 60_000, LateLingerMilliseconds = 60_000 });
        await h.StartAsync();
        await h.EnqueueSpansAsync(Span(DaysAgo(0)), Span(DaysAgo(1)));
        await Task.Delay(200);
        Assert.Empty(h.Writer.Calls);

        await h.StopAsync();
        Assert.Equal(2, h.Writer.Calls.Count);
        Assert.Equal(0, h.Channel.TraceGate.Resident);
        Assert.Equal(0, h.Sum("records_dropped"));
    }

    [Fact]
    public async Task ABufferAtMaxBatchRecords_FlushesAtOnce_AndAnOversizeBufferIsCutIntoPiecesWithNewTokens()
    {
        await using var h = new Harness(new ClickHouseIngestionOptions { LingerMilliseconds = 60_000, LateLingerMilliseconds = 60_000, MaxSpanBatchRecords = 3 });
        await h.StartAsync();
        await h.EnqueueSpansAsync(Enumerable.Range(0, 7).Select(_ => Span(DaysAgo(0))).ToArray());
        Assert.True(await h.WaitAsync(() => h.Writer.Calls.Count == 3));
        await h.StopAsync();

        Assert.Equal([3, 3, 1], h.Writer.Calls.Select(c => c.Count).Order().Reverse());
        Assert.Equal(3, h.Writer.Calls.Select(c => c.Token).Distinct().Count());
    }
}
