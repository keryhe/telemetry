using System.Diagnostics.Metrics;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The ingestion instruments (stress-test plan, Phase 1), collected through a
/// <see cref="MeterListener"/> the way <c>dotnet-counters</c> would. Runs the real
/// <see cref="TelemetryIngestionWorker"/> against a fake bulk writer; needs no database.
/// </summary>
[Collection(IngestionMeterCollection.Name)]
public class IngestionMetricsTests
{
    private sealed record Sample(string Instrument, double Value, Dictionary<string, object?> Tags);

    private sealed class Collector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Sample> _samples = [];

        public Collector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Keryhe.Telemetry.Ingestion")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (_samples) _samples.Add(new Sample(instrument.Name, value, dict));
        }

        public void Observe() => _listener.RecordObservableInstruments();

        public List<Sample> Of(string suffix, string? signal = null)
        {
            lock (_samples)
                return _samples.Where(s => s.Instrument == "keryhe.telemetry.ingestion." + suffix
                                           && (signal is null || (string?)s.Tags.GetValueOrDefault("signal") == signal)).ToList();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class FakeWriter(int failFirstLogFlushes = 0) : ITelemetryBulkWriter
    {
        private int _logFailuresLeft = failFirstLogFlushes;
        public Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _logFailuresLeft) >= 0)
                throw new InvalidOperationException("simulated flush failure");
            return Task.CompletedTask;
        }
        public Task FlushTracesAsync(List<SpanModel> spans, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        return condition();
    }

    private static (TelemetryIngestionChannel Channel, TelemetryIngestionWorker Worker, IngestionMetrics Metrics) Build(ITelemetryBulkWriter writer, TelemetryIngestionOptions? options = null)
    {
        var opts = Options.Create(options ?? new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 });
        var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance);
        return (channel, worker, metrics);
    }

    [Fact]
    public async Task Successful_flush_records_flushed_batch_size_duration_and_gate_wait()
    {
        using var collector = new Collector();
        var (channel, worker, metrics) = Build(new FakeWriter());
        using var _ = metrics;
        await worker.StartAsync(CancellationToken.None);

        await channel.LogGate.AcquireAsync(7, CancellationToken.None);
        await channel.Logs.Writer.WriteAsync(Enumerable.Range(0, 7).Select(_ => new LogRecordModel()).ToList());

        Assert.True(await WaitUntilAsync(() => collector.Of("records_flushed", "logs").Sum(s => s.Value) == 7));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(7, Assert.Single(collector.Of("flush_batch_size", "logs")).Value);
        var duration = Assert.Single(collector.Of("flush_duration", "logs"));
        Assert.Equal("ok", duration.Tags["outcome"]);
        Assert.Single(collector.Of("gate_wait", "logs"));
        Assert.Empty(collector.Of("flush_retries"));
        Assert.Empty(collector.Of("records_dropped"));
    }

    [Fact]
    public async Task Failed_attempts_record_retries_and_failed_durations_then_succeed()
    {
        using var collector = new Collector();
        var (channel, worker, metrics) = Build(new FakeWriter(failFirstLogFlushes: 2));
        using var _ = metrics;
        await worker.StartAsync(CancellationToken.None);

        await channel.LogGate.AcquireAsync(3, CancellationToken.None);
        await channel.Logs.Writer.WriteAsync(Enumerable.Range(0, 3).Select(_ => new LogRecordModel()).ToList());

        Assert.True(await WaitUntilAsync(() => collector.Of("records_flushed", "logs").Sum(s => s.Value) == 3));
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, collector.Of("flush_retries", "logs").Sum(s => s.Value));
        var durations = collector.Of("flush_duration", "logs");
        Assert.Equal(2, durations.Count(d => (string?)d.Tags["outcome"] == "failed"));
        Assert.Equal(1, durations.Count(d => (string?)d.Tags["outcome"] == "ok"));
        // One merged batch, however many attempts it took.
        Assert.Single(collector.Of("flush_batch_size", "logs"));
    }

    [Fact]
    public async Task Exhausted_retries_count_as_dropped_not_flushed()
    {
        using var collector = new Collector();
        var (channel, worker, metrics) = Build(
            new FakeWriter(failFirstLogFlushes: int.MaxValue),
            new TelemetryIngestionOptions { MaxFlushRetries = 1, RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 });
        using var _ = metrics;
        await worker.StartAsync(CancellationToken.None);

        await channel.LogGate.AcquireAsync(4, CancellationToken.None);
        await channel.Logs.Writer.WriteAsync(Enumerable.Range(0, 4).Select(_ => new LogRecordModel()).ToList());

        Assert.True(await WaitUntilAsync(() => collector.Of("records_dropped", "logs").Sum(s => s.Value) == 4));
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(collector.Of("records_flushed"));
        Assert.Equal(1, collector.Of("flush_retries", "logs").Sum(s => s.Value));
    }

    [Fact]
    public async Task Resident_records_gauge_tracks_the_gate()
    {
        using var collector = new Collector();
        var (channel, _, metrics) = Build(new FakeWriter());
        using var _ = metrics;

        // Worker deliberately not started: nothing drains, so the count stays where we put it.
        await channel.TraceGate.AcquireAsync(5, CancellationToken.None);
        collector.Observe();
        Assert.Equal(5, collector.Of("resident_records", "traces").Last().Value);
        Assert.Equal(0, collector.Of("resident_records", "logs").Last().Value);

        channel.TraceGate.Release(5);
        collector.Observe();
        Assert.Equal(0, collector.Of("resident_records", "traces").Last().Value);
    }
}
