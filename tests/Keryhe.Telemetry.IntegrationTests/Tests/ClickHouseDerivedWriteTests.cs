using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ClickHouse.Client.ADO;
using Dapper;
using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.CollectorAuth;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The derived tables the writer fills after its raw insert (plans/clickhouse-redesign phase 3): <c>trace_index</c>,
/// the catalog and series tables, retries, and a failed derived insert. The rollups are covered by
/// <c>ClickHouseRollupWriteTests</c> over the shared <c>RollupWriteTestsBase</c>.
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseDerivedWriteTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime Midnight = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private async Task<List<dynamic>> QueryAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<dynamic>(sql)).ToList();
    }

    private async Task<long> CountAsync(string table) => Convert.ToInt64((await QueryAsync($"SELECT count() AS c FROM {table}"))[0].c);

    private ClickHouseBulkWriter NewWriter(TimeProvider? time = null, IngestionMetrics? metrics = null, int maxRetries = 1)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Collector"] = fixture.DatabaseConnectionString })
            .Build();
        return new ClickHouseBulkWriter(configuration, NullLogger<ClickHouseBulkWriter>.Instance,
            Options.Create(new ClickHouseIngestionOptions()),
            Options.Create(new TelemetryIngestionOptions { MaxFlushRetries = maxRetries, RetryBaseDelayMilliseconds = 5, RetryMaxDelayMilliseconds = 10 }),
            metrics, time);
    }

    private SpanModel Span(string trace, string span, string service, DateTime start, int ms, SpanStatusCode status = SpanStatusCode.OK, SpanKind kind = SpanKind.CLIENT) => new()
    {
        TraceIdHex = trace.PadLeft(32, '0'), SpanIdHex = span.PadLeft(16, '0'), Name = "op", Kind = kind,
        StartTimeUnixNano = SeededDataBuilder.ToUnixNano(start), EndTimeUnixNano = SeededDataBuilder.ToUnixNano(start.AddMilliseconds(ms)),
        StatusCode = status, Resource = SeededDataBuilder.Resource(fixture.TenantId, service), InstrumentationScope = SeededDataBuilder.Scope()
    };

    private List<SpanModel> AcrossMidnight() =>
    [
        Span("1", "1", "svc-a", Midnight.AddSeconds(-3), 100),                                   // trace 1: svc-a, the day before
        Span("1", "2", "svc-a", Midnight.AddSeconds(-1), 50, SpanStatusCode.ERROR),
        Span("1", "3", "svc-b", Midnight.AddSeconds(1), 400),                                    // ... and svc-b, after midnight
        Span("2", "4", "svc-b", Midnight.AddHours(5), 10, kind: SpanKind.SERVER),                 // trace 2: one inbound span
        Span("3", "5", "svc-a", Midnight.AddHours(6), 10),
        Span("3", "6", "svc-a", Midnight.AddHours(6).AddMilliseconds(5), 20),
    ];

    [Fact]
    public async Task TraceIndex_MatchesALinqRestatement_PerServicePerDay_AcrossMidnight()
    {
        var spans = AcrossMidnight();
        await fixture.Services.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        // 100 ns truncation, like the stored timestamps
        static long Trunc(long nano) => nano / 100 * 100;
        var expected = spans
            .GroupBy(s => (s.TraceIdHex, Service: (string)s.Resource!.Attributes["service.name"], Day: DateOnly.FromDateTime(DateTime.UnixEpoch.AddTicks(s.StartTimeUnixNano / 100))))
            .Select(g => (g.Key.TraceIdHex, g.Key.Service, g.Key.Day, Min: Trunc(g.Min(s => s.StartTimeUnixNano)), Max: Trunc(g.Max(s => s.EndTimeUnixNano)),
                Count: g.Count(), Error: g.Any(s => s.StatusCode == SpanStatusCode.ERROR)))
            .OrderBy(r => r.TraceIdHex).ThenBy(r => r.Service).ThenBy(r => r.Day).ToList();

        var rows = await QueryAsync(
            "SELECT trace_id, service_name, toString(day) AS day, toUnixTimestamp64Nano(min(start_min)) AS mn, toUnixTimestamp64Nano(max(end_max)) AS mx, " +
            "sum(span_count) AS n, max(has_error) AS err FROM trace_index GROUP BY trace_id, service_name, day");
        var actual = rows
            .Select(r => (TraceIdHex: ClickHouseIds.GuidToTraceIdHex((Guid)r.trace_id), Service: (string)r.service_name, Day: DateOnly.Parse((string)r.day),
                Min: (long)r.mn, Max: (long)r.mx, Count: (int)(ulong)r.n, Error: (byte)r.err == 1))
            .OrderBy(r => r.TraceIdHex).ThenBy(r => r.Service).ThenBy(r => r.Day).ToList();

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(4, actual.Count); // trace 1: svc-a (day before) and svc-b (after midnight); traces 2 and 3: one service each
        Assert.Equal(expected, actual);
        Assert.Contains(actual, r => r.Error && r.Service == "svc-a");
    }

    [Fact]
    public async Task ARetriedFlush_WithTheSameToken_LeavesEveryDerivedTableUnchanged()
    {
        await using var writer = NewWriter();
        var spans = AcrossMidnight();
        var logs = SeededDataBuilder.BasicLogWindow(fixture.TenantId, Midnight.AddHours(1), count: 30);
        var metrics = SeededDataBuilder.MultiInstanceGauge(fixture.TenantId, Midnight.AddHours(2), instanceCount: 2, pointsPerInstance: 5);

        string[] tables = ["spans", "trace_index", "request_rollup_minute", "log_records", "log_rollup_minute", "gauge_points", "metric_catalog", "metric_series"];
        async Task<List<long>> Counts() { var c = new List<long>(); foreach (var t in tables) c.Add(await CountAsync(t)); return c; }

        await writer.FlushTracesAsync(spans, "t1");
        await writer.FlushLogsAsync(logs, "t1");
        await writer.FlushMetricsAsync(metrics, "t1");
        var first = await Counts();
        Assert.All(first, n => Assert.True(n > 0));

        await writer.FlushTracesAsync(spans, "t1");
        await writer.FlushLogsAsync(logs, "t1");
        await writer.FlushMetricsAsync(metrics, "t1");
        Assert.Equal(first, await Counts());
    }

    [Fact]
    public async Task AFailedDerivedInsert_KeepsTheRawRows_DropsTheDerivedRows_AndCountsThem()
    {
        using var metrics = new IngestionMetrics();
        var dropped = new ConcurrentBag<(string Table, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name.EndsWith("derived_rows_dropped")) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "table") dropped.Add(((string)t.Value!, v));
        });
        listener.Start();

        await using var writer = NewWriter(metrics: metrics, maxRetries: 1);
        var spans = AcrossMidnight();

        await QueryAsync("RENAME TABLE trace_index TO trace_index_away");
        try
        {
            await writer.FlushTracesAsync(spans, "fail");
        }
        finally
        {
            await QueryAsync("RENAME TABLE trace_index_away TO trace_index");
        }

        Assert.Equal(spans.Count, await CountAsync("spans"));
        Assert.True(await CountAsync("request_rollup_minute") > 0, "the other derived tables are unaffected");
        Assert.Equal(0, await CountAsync("trace_index"));
        Assert.Contains(dropped, d => d.Table == "trace_index" && d.Value == 4);
    }

    [Fact]
    public async Task TheCatalogAndSeriesAreWrittenOnce_AgainAfterTheRefreshInterval_AndNotBetween()
    {
        var clock = new CollectorHost.TestClock(new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero));
        await using var writer = NewWriter(time: clock);
        var metrics = SeededDataBuilder.MultiInstanceGauge(fixture.TenantId, Midnight.AddHours(2), instanceCount: 2, pointsPerInstance: 5);

        await writer.FlushMetricsAsync(metrics, "a");
        Assert.Equal(1, await CountAsync("metric_catalog"));
        Assert.Equal(2, await CountAsync("metric_series"));

        clock.Advance(TimeSpan.FromSeconds(200));
        await writer.FlushMetricsAsync(metrics, "b");
        Assert.Equal(1, await CountAsync("metric_catalog"));
        Assert.Equal(2, await CountAsync("metric_series"));

        clock.Advance(TimeSpan.FromSeconds(200)); // 400 s since the write, past the 300 s refresh
        await writer.FlushMetricsAsync(metrics, "c");
        Assert.Equal(2, await CountAsync("metric_catalog"));
        Assert.Equal(4, await CountAsync("metric_series"));

        var series = await QueryAsync(
            "SELECT attributes['k8s.pod.name'] AS pod, resource_attributes['service.instance.id'] AS inst, toUnixTimestamp64Nano(last_seen) AS ls " +
            "FROM metric_series FINAL ORDER BY inst");
        Assert.Equal(["pod-0", "pod-1"], series.Select(r => (string)r.inst).ToArray());
        Assert.Equal(SeededDataBuilder.ToUnixNano(Midnight.AddHours(2).AddSeconds(40)), (long)series[0].ls);
    }
}
