using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

public class IngestionWorkerTests
{
    private static long Now(TimeSpan? ago = null)
        => (DateTime.UtcNow - (ago ?? TimeSpan.Zero) - DateTime.UnixEpoch).Ticks * 100;

    /// <summary>Wraps the real writer so a test can fail an attempt after it wrote (to prove a retry stores once) or refuse a record.</summary>
    private sealed class FaultyWriter(IParquetTokenWriter inner) : IParquetTokenWriter
    {
        public ConcurrentQueue<(string Token, int Count)> Calls { get; } = new();
        public int FailAfterWriteAttempts;
        public bool RefuseBadSpans;

        public async Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default)
        {
            Calls.Enqueue((token, spans.Count));
            if (RefuseBadSpans && spans.Any(s => s.Name == "bad")) throw new NotSupportedException("bad span");
            await inner.FlushTracesAsync(spans, token, ct);
            if (Interlocked.Decrement(ref FailAfterWriteAttempts) >= 0) throw new IOException("injected after the write");
        }

        public Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default) => inner.FlushLogsAsync(records, token, ct);
        public Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default) => inner.FlushMetricsAsync(metrics, token, ct);
    }

    private sealed class FakeRetention(int? traceDays = null) : IRetentionWindows
    {
        public DateTime? OldestAllowedUtc(string signal) => signal == "traces" && traceDays is { } d ? DateTime.UtcNow.AddDays(-d) : null;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public int RefreshSeconds => 3600;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public ParquetFixture Fx { get; } = new();
        public FaultyWriter Writer { get; }
        public TelemetryIngestionChannel Channel { get; }
        public ParquetIngestionWorker Worker { get; }
        public IngestionMetrics Metrics { get; } = new();
        private readonly MeterListener _listener = new();
        public ConcurrentBag<(string Name, double Value, string? Reason)> Measurements { get; } = new();

        public Harness(ParquetOptions? options = null, int? retentionDays = null, int maxRetries = 5)
        {
            options ??= new ParquetOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 60_000 };
            options.RootPath = Fx.Root;
            Writer = new FaultyWriter(Fx.Writer);
            var shared = Options.Create(new TelemetryIngestionOptions { MaxFlushRetries = maxRetries, RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 });
            Channel = new TelemetryIngestionChannel(shared, Metrics);
            Worker = new ParquetIngestionWorker(Writer, Channel, Options.Create(options), shared, Metrics, new FakeRetention(retentionDays),
                TimeProvider.System, NullLogger<ParquetIngestionWorker>.Instance, new ParquetFlushErrorClassifier());

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

        public double Sum(string suffix, string? reason = null)
            => Measurements.Where(m => m.Name.EndsWith(suffix) && (reason is null || m.Reason == reason)).Sum(m => m.Value);

        public Task StartAsync() => Worker.StartAsync(CancellationToken.None);
        public Task StopAsync() => Worker.StopAsync(CancellationToken.None);

        public async Task EnqueueAsync(params SpanModel[] spans)
        {
            var list = spans.ToList();
            await Channel.TraceGate.AcquireAsync(list.Count, CancellationToken.None);
            Channel.MarkEnqueued(list);
            await Channel.Traces.Writer.WriteAsync(list);
        }

        public static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }
            return condition();
        }

        public long StoredRows() => Fx.AllParquetFiles("spans") is { Length: > 0 } f ? ParquetFixture.Scalar<long>(f, "count(*)") : 0;

        public async ValueTask DisposeAsync()
        {
            _listener.Dispose();
            Worker.Dispose();
            Metrics.Dispose();
            Fx.Dispose();
            await Task.CompletedTask;
        }
    }

    private static SpanModel S(long startNano, long tenant = 1, string name = "op") => Span(Resource(tenant, "svc"), startNano, name);

    [Fact]
    public async Task HoursAndTenants_AreSeparateFiles_AndTheGateIsReleased()
    {
        await using var h = new Harness(new ParquetOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 150 });
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now(), tenant: 2), S(Now(TimeSpan.FromHours(3))), S(Now(TimeSpan.FromHours(3))));
        Assert.True(await Harness.WaitAsync(() => h.StoredRows() == 4));
        await h.StopAsync();

        // The worker buffers by hour across tenants; the writer cuts each buffer into (tenant, hour) files.
        var partitions = h.Fx.Store.EnumeratePartitions("spans", null, DateOnly.MinValue, DateOnly.MaxValue).ToList();
        Assert.Equal(3, partitions.Count);
        Assert.Equal(3, h.Fx.AllParquetFiles("spans").Length);
        Assert.Equal(2, h.Writer.Calls.Count);
        Assert.Equal(0, h.Channel.TraceGate.Resident);
        Assert.Equal(4, h.Sum("records_flushed"));
    }

    [Fact]
    public async Task CurrentHourFlushesOnLinger_AnEarlierHourWaitsForTheLateLinger_UntilShutdownDrainsIt()
    {
        await using var h = new Harness(new ParquetOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 60_000 });
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now(TimeSpan.FromHours(5))));
        Assert.True(await Harness.WaitAsync(() => h.StoredRows() == 1));
        await Task.Delay(300);
        Assert.Equal(1, h.StoredRows());               // the late buffer is still waiting
        Assert.Equal(1, h.Channel.TraceGate.Resident); // and still holds the gate

        await h.StopAsync();
        Assert.Equal(2, h.StoredRows());               // shutdown commits every buffer regardless of linger
        Assert.Equal(0, h.Channel.TraceGate.Resident);
    }

    [Fact]
    public async Task ABufferSealsOnSize()
    {
        await using var h = new Harness(new ParquetOptions { LingerMilliseconds = 60_000, LateLingerMilliseconds = 60_000, MaxBufferRecords = 3 });
        await h.StartAsync();
        await h.EnqueueAsync(Enumerable.Range(0, 7).Select(i => S(Now())).ToArray());
        // An oversize buffer is cut into pieces (3, 3, 1) that are all written at once, long before the 60 s linger.
        Assert.True(await Harness.WaitAsync(() => h.StoredRows() == 7 && h.Channel.TraceGate.Resident == 0));
        Assert.Equal([1, 3, 3], h.Writer.Calls.Select(c => c.Count).Order());
        Assert.Equal(3, h.Writer.Calls.Select(c => c.Token).Distinct().Count());
        await h.StopAsync();
    }

    [Fact]
    public async Task ARetryAfterTheFileWasWritten_StoresEachRowOnce_UnderOneToken()
    {
        await using var h = new Harness();
        h.Writer.FailAfterWriteAttempts = 2;
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now()));
        Assert.True(await Harness.WaitAsync(() => h.Writer.Calls.Count == 3));
        await h.StopAsync();

        Assert.Single(h.Writer.Calls.Select(c => c.Token).Distinct());
        Assert.Single(h.Fx.AllParquetFiles("spans"));
        Assert.Equal(2, h.StoredRows());
        Assert.Equal(2, h.Sum("flush_retries"));
        Assert.Equal(2, h.Sum("records_flushed"));
        Assert.Equal(0, h.Channel.TraceGate.Resident);
    }

    [Fact]
    public async Task APermanentError_BisectsToTheBadRecord_AndTheRestIsStored()
    {
        await using var h = new Harness();
        h.Writer.RefuseBadSpans = true;
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now(), name: "bad"), S(Now()), S(Now()));
        Assert.True(await Harness.WaitAsync(() => h.StoredRows() == 3 && h.Channel.TraceGate.Resident == 0));
        await h.StopAsync();

        Assert.Equal(1, h.Sum("records_dropped", "permanent"));
        Assert.Equal(3, h.Sum("records_flushed"));
        Assert.Equal(0, ParquetFixture.Scalar<long>(h.Fx.AllParquetFiles("spans"), "count(*)", "span_name = 'bad'"));
    }

    [Fact]
    public async Task RecordsOlderThanRetention_AreDroppedAndCounted()
    {
        await using var h = new Harness(retentionDays: 7);
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now(TimeSpan.FromDays(30))), S(Now(TimeSpan.FromDays(31))));
        Assert.True(await Harness.WaitAsync(() => h.StoredRows() == 1 && h.Channel.TraceGate.Resident == 0));
        await h.StopAsync();
        Assert.Equal(2, h.Sum("records_dropped", "out_of_retention"));
    }

    [Fact]
    public async Task ExhaustedRetries_DropTheBatchAndReleaseTheGate()
    {
        await using var h = new Harness(maxRetries: 1);
        h.Writer.FailAfterWriteAttempts = 100;
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()), S(Now()));
        Assert.True(await Harness.WaitAsync(() => h.Channel.TraceGate.Resident == 0 && h.Sum("records_dropped") == 2));
        await h.StopAsync();
        Assert.Equal(2, h.Sum("records_dropped", "retries_exhausted"));
    }

    [Fact]
    public async Task CommitLagIsRecordedWhenTheFilesAreCommitted()
    {
        await using var h = new Harness();
        await h.StartAsync();
        await h.EnqueueAsync(S(Now()));
        Assert.True(await Harness.WaitAsync(() => h.Measurements.Any(m => m.Name.EndsWith("commit_lag"))));
        await h.StopAsync();
        // 100 ms linger: the lag includes the wait, and a record is readable only after its manifest lists it.
        Assert.Equal(1, h.StoredRows());
    }
}

public class FlushErrorClassifierTests
{
    private readonly ParquetFlushErrorClassifier _c = new();

    [Fact]
    public void DiskAndPermissionErrorsAreTransient()
    {
        Assert.Equal(FlushErrorKind.Transient, _c.Classify(new IOException("No space left on device")));
        Assert.Equal(FlushErrorKind.Transient, _c.Classify(new UnauthorizedAccessException()));
        Assert.Equal(FlushErrorKind.Transient, _c.Classify(new DirectoryNotFoundException()));
        Assert.Equal(FlushErrorKind.Transient, _c.Classify(new InvalidOperationException("who knows")));
    }

    [Fact]
    public void AnUnsupportedFeatureIsPermanent()
        => Assert.Equal(FlushErrorKind.Permanent, _c.Classify(new NotSupportedException()));

    [Fact]
    public void ATransientIoErrorWrappedInAnUnsupportedWrapperStaysTransient()
        => Assert.Equal(FlushErrorKind.Transient, _c.Classify(new IOException("disk full", new NotSupportedException())));
}
