using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Collector improvements phase 1, against a real database: one span whose name is longer than the column (a permanent
/// error from the driver) sits in a merged batch of 2,000. The provider's own <see cref="IFlushErrorClassifier"/> must call the
/// driver's exception permanent, and the worker must then store the other 1,999 and drop only the bad one, without retrying.
/// </summary>
public abstract class PoisonRecordTestsBase(ProviderFixture fixture) : IAsyncLifetime
{
    protected abstract Task<DbConnection> OpenAsync();

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Delegates to the real writer and keeps every exception it threw, so the classifier can be checked against the real driver's.</summary>
    private sealed class RecordingWriter(ITelemetryBulkWriter inner) : ITelemetryBulkWriter
    {
        public readonly List<Exception> Thrown = [];
        public int Attempts;

        public Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default) => inner.FlushLogsAsync(records, ct);
        public Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default) => inner.FlushMetricsAsync(metrics, ct);
        public async Task FlushTracesAsync(List<SpanModel> spans, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Attempts);
            try { await inner.FlushTracesAsync(spans, ct); }
            catch (Exception ex) { lock (Thrown) Thrown.Add(ex); throw; }
        }
    }

    [Fact]
    public async Task One_overlong_span_name_costs_one_span_not_the_batch()
    {
        var classifier = fixture.Services.GetRequiredService<IFlushErrorClassifier>();
        var writer = new RecordingWriter(fixture.Services.GetRequiredService<ITelemetryBulkWriter>());
        var opts = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10, MaxTraceFlushSpanBatchSize = 5_000 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance, classifier);

        var resource = new ResourceModel { TenantId = fixture.TenantId, Attributes = { ["service.name"] = "poison-svc" } };
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        var spans = Enumerable.Range(0, 2000).Select(i => new SpanModel
        {
            TraceIdHex = (1000 + i).ToString("x32"), SpanIdHex = (1000 + i).ToString("x16"), Name = i == 1234 ? new string('x', 300) : $"op-{i}",
            StartTimeUnixNano = now, EndTimeUnixNano = now + 1000, Resource = resource
        }).ToList();
        await channel.TraceGate.AcquireAsync(spans.Count, CancellationToken.None);
        await channel.Traces.Writer.WriteAsync(spans);

        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 400 && channel.TraceGate.Resident > 0; i++) await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        await using var conn = await OpenAsync();
        var stored = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM spans");
        var poisonStored = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM spans WHERE span_id = @id", new { id = (1000 + 1234).ToString("x16") });

        Assert.NotEmpty(writer.Thrown);
        Assert.All(writer.Thrown, ex => Assert.Equal(FlushErrorKind.Permanent, classifier.Classify(ex)));
        Assert.Equal(1999, stored);
        Assert.Equal(0, poisonStored);
        Assert.Equal(0, channel.TraceGate.Resident);
        // Isolating one record in 2,000 by halving takes about 2 x log2(2000) flushes; a retry loop would have taken 6 on the first batch alone.
        Assert.InRange(writer.Attempts, 2, 40);
        Assert.Equal(writer.Thrown.Count, writer.Thrown.Count(ex => classifier.Classify(ex) == FlushErrorKind.Permanent));
    }

    [Fact]
    public async Task Without_the_oversized_name_the_same_batch_is_stored_whole()
    {
        // Negative control: nothing is wrong with the batch, so nothing is split and nothing is dropped.
        var classifier = fixture.Services.GetRequiredService<IFlushErrorClassifier>();
        var writer = new RecordingWriter(fixture.Services.GetRequiredService<ITelemetryBulkWriter>());
        var opts = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10, MaxTraceFlushSpanBatchSize = 5_000 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(opts, metrics);
        var worker = new TelemetryIngestionWorker(writer, channel, opts, metrics, new RollupAccumulator(), NullLogger<TelemetryIngestionWorker>.Instance, classifier);

        var resource = new ResourceModel { TenantId = fixture.TenantId, Attributes = { ["service.name"] = "poison-svc" } };
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        var spans = Enumerable.Range(0, 500).Select(i => new SpanModel
        {
            TraceIdHex = (5000 + i).ToString("x32"), SpanIdHex = (5000 + i).ToString("x16"), Name = new string('y', 255),
            StartTimeUnixNano = now, EndTimeUnixNano = now + 1000, Resource = resource
        }).ToList();
        await channel.TraceGate.AcquireAsync(spans.Count, CancellationToken.None);
        await channel.Traces.Writer.WriteAsync(spans);

        await worker.StartAsync(CancellationToken.None);
        for (var i = 0; i < 400 && channel.TraceGate.Resident > 0; i++) await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        await using var conn = await OpenAsync();
        Assert.Equal(500, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM spans"));
        Assert.Equal(1, writer.Attempts);
        Assert.Empty(writer.Thrown);
    }
}
