using ClickHouse.Client.ADO;
using Dapper;
using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// <see cref="ClickHouseIngestionWorker"/> over the real <see cref="ClickHouseBulkWriter"/> and a ClickHouse container
/// (the gRPC services are not in the path: the channel is fed directly, as the services do).
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseIngestionWorkerDbTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Writes through the real writer, then throws on the first attempt: the insert landed, the caller does not know.</summary>
    private sealed class FailAfterFirstWrite(IClickHouseTokenWriter inner) : IClickHouseTokenWriter
    {
        public int Attempts;
        public Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default) => inner.FlushLogsAsync(records, token, ct);
        public Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default) => inner.FlushMetricsAsync(metrics, token, ct);
        public async Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default)
        {
            await inner.FlushTracesAsync(spans, token, ct);
            if (Interlocked.Increment(ref Attempts) == 1) throw new InvalidOperationException("injected after the insert landed");
        }
    }

    private sealed class NoRetention : IRetentionWindows
    {
        public DateTime? OldestAllowedUtc(string signal) => null;
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
        public int RefreshSeconds => 3600;
    }

    private async Task<List<dynamic>> QueryAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<dynamic>(sql)).ToList();
    }

    [Fact]
    public async Task YesterdayAndToday_LandInTwoPartitions_AndAFailedFirstAttempt_StoresEachRowOnce()
    {
        var real = (IClickHouseTokenWriter)fixture.Services.GetRequiredService<ITelemetryBulkWriter>();
        var writer = new FailAfterFirstWrite(real);

        var shared = Options.Create(new TelemetryIngestionOptions { RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 });
        using var metrics = new IngestionMetrics();
        var channel = new TelemetryIngestionChannel(shared, metrics);
        using var worker = new ClickHouseIngestionWorker(writer, channel,
            Options.Create(new ClickHouseIngestionOptions { LingerMilliseconds = 100, LateLingerMilliseconds = 200 }),
            shared, metrics, new NoRetention(), TimeProvider.System, NullLogger<ClickHouseIngestionWorker>.Instance);

        var now = DateTime.UtcNow;
        var spans = SeededDataBuilder.BasicTraceWindow(fixture.TenantId, now.AddMinutes(-1), traceCount: 4);       // today (2 spans each)
        spans.AddRange(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, now.AddDays(-1), traceCount: 3, seedOffset: 1000));

        await worker.StartAsync(CancellationToken.None);
        await channel.TraceGate.AcquireAsync(spans.Count, CancellationToken.None);
        channel.MarkEnqueued(spans);
        await channel.Traces.Writer.WriteAsync(spans);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(writer.Attempts >= 3, "one failed first attempt, its retry, and the other day");
        var stored = await QueryAsync("SELECT toString(toDate(start_time)) AS day, count() AS n FROM spans GROUP BY day ORDER BY day");
        Assert.Equal(2, stored.Count);
        // today's first attempt landed and then failed; its retry reused the token, so nothing is doubled
        Assert.Equal(spans.Count, stored.Sum(r => (long)(ulong)r.n));
        Assert.Equal(6, (long)(ulong)stored[0].n);  // yesterday: 3 traces x 2 spans
        Assert.Equal(0, channel.TraceGate.Resident);
    }
}
