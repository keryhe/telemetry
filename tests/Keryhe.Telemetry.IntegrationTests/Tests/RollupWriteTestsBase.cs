using System.Data.Common;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The rollup write path on each provider (plans/summary-rollups.md): spans and logs go in through
/// <see cref="ITelemetryBulkWriter"/>, then the rollup tables are read back with raw SQL and
/// compared with a LINQ restatement over the seeded rows (counts, errors, duration sum and max, every
/// band). The relational providers feed the accumulator and append through their
/// <see cref="IRollupStore"/> in two halves (so the table holds partial rows that reads must sum);
/// ClickHouse is written by its materialized views alone, which also checks its integer band
/// expression against <see cref="DurationBands.IndexOf"/> at every band edge +-1 ns.
/// </summary>
public abstract class RollupWriteTestsBase : IAsyncLifetime
{
    private const long Minute = 60_000_000_000L;
    private readonly ProviderFixture _fixture;

    protected RollupWriteTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    protected abstract Task<DbConnection> OpenAsync();

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    private static long MinuteOf(long nanos) => nanos - nanos % Minute;

    private static SpanModel Span(long tenant, string service, SpanKind kind, long start, long duration, bool error, int n) => new()
    {
        TraceIdHex = (n + 1).ToString("x32"), SpanIdHex = (n + 1).ToString("x16"), Name = "op", Kind = kind,
        StartTimeUnixNano = start, EndTimeUnixNano = start + duration,
        StatusCode = error ? SpanStatusCode.ERROR : SpanStatusCode.OK,
        Resource = SeededDataBuilder.Resource(tenant, service),
        InstrumentationScope = SeededDataBuilder.Scope()
    };

    /// <summary>Spans at every band edge +-1 ns, every kind, errors, a clock-skewed span, over three minutes and two services.</summary>
    private List<SpanModel> SeedSpans(DateTime at)
    {
        var baseMinute = MinuteOf(SeededDataBuilder.ToUnixNano(at));
        var spans = new List<SpanModel>();
        var n = 0;
        var kinds = new[] { SpanKind.SERVER, SpanKind.CONSUMER, SpanKind.CLIENT, SpanKind.INTERNAL, SpanKind.PRODUCER };
        for (var band = 0; band < DurationBands.Count; band++)
        {
            foreach (var d in new[] { DurationBands.LowerEdgeNanos(band) - 1, DurationBands.LowerEdgeNanos(band), DurationBands.LowerEdgeNanos(band) + 1 })
            {
                if (d < 0) continue;
                var kind = kinds[n % kinds.Length];
                spans.Add(Span(_fixture.TenantId, n % 2 == 0 ? "checkout-api" : "payments-worker", kind,
                    baseMinute + (n % 3) * Minute + 1_000_000 + n, d, error: n % 4 == 0, n));
                n++;
            }
        }
        spans.Add(Span(_fixture.TenantId, "checkout-api", SpanKind.SERVER, baseMinute + 5_000, -2_000_000, false, n++)); // negative duration
        spans.Add(Span(_fixture.TenantId, "checkout-api", SpanKind.SERVER, baseMinute + 6_000, 3_000_000_000_000L, true, n++)); // open band
        return spans;
    }

    private List<LogRecordModel> SeedLogs(DateTime at)
    {
        var baseMinute = MinuteOf(SeededDataBuilder.ToUnixNano(at));
        var severities = new int?[] { null, 1, 5, 9, 13, 17, 21 };
        var logs = new List<LogRecordModel>();
        for (var i = 0; i < 70; i++)
            logs.Add(new LogRecordModel
            {
                TimeUnixNano = baseMinute + (i % 3) * Minute + i * 1_000,
                SeverityNumber = severities[i % severities.Length],
                BodyType = AttributeType.STRING, BodyValue = $"log {i}",
                Resource = SeededDataBuilder.Resource(_fixture.TenantId, i % 2 == 0 ? "checkout-api" : "payments-worker"),
                InstrumentationScope = SeededDataBuilder.Scope()
            });
        return logs;
    }

    private async Task WriteAsync(List<SpanModel> spans, List<LogRecordModel> logs)
    {
        using var scope = Scope();
        var writer = scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
        await writer.FlushTracesAsync(spans);
        await writer.FlushLogsAsync(logs);

        var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
        if (store.FedByViews) return;

        // Two halves, appended separately: the table then holds partial rows for the same key.
        var accumulator = new RollupAccumulator();
        foreach (var (s, l) in new[] { (spans.Take(spans.Count / 2).ToList(), logs.Take(logs.Count / 2).ToList()),
                                       (spans.Skip(spans.Count / 2).ToList(), logs.Skip(logs.Count / 2).ToList()) })
        {
            accumulator.AddSpans(s);
            accumulator.AddLogs(l);
            var (requests, logRows) = accumulator.DrainAll();
            await store.AppendRequestsAsync(requests, default);
            await store.AppendLogsAsync(logRows, default);
        }
    }

    private async Task<List<object[]>> QueryAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private static string BandSums() => string.Join(", ", Enumerable.Range(0, DurationBands.Count).Select(i => $"SUM(h{i:00})"));

    private async Task<Dictionary<(string Service, long Minute), long[]>> ReadRequestsAsync()
    {
        var rows = await QueryAsync(
            $"SELECT service_name, bucket_start_unix_nano, SUM(request_count), SUM(error_count), SUM(sum_duration_nanos), MAX(max_duration_nanos), {BandSums()} " +
            $"FROM request_rollup_minute WHERE tenant_id = {_fixture.TenantId} GROUP BY service_name, bucket_start_unix_nano");
        return rows.ToDictionary(
            r => (Convert.ToString(r[0])!, Convert.ToInt64(r[1])),
            r => r.Skip(2).Select(Convert.ToInt64).ToArray());
    }

    private async Task<Dictionary<(string Service, int Severity, long Minute), long>> ReadLogsAsync()
    {
        var rows = await QueryAsync(
            "SELECT service_name, severity_number, bucket_start_unix_nano, SUM(record_count) " +
            $"FROM log_rollup_minute WHERE tenant_id = {_fixture.TenantId} GROUP BY service_name, severity_number, bucket_start_unix_nano");
        return rows.ToDictionary(r => (Convert.ToString(r[0])!, Convert.ToInt32(r[1]), Convert.ToInt64(r[2])), r => Convert.ToInt64(r[3]));
    }

    private async Task<long> CountAsync(string table)
    {
        var rows = await QueryAsync($"SELECT COUNT(*) FROM {table} WHERE tenant_id = {_fixture.TenantId}");
        return Convert.ToInt64(rows[0][0]);
    }

    [Fact]
    public async Task RequestRollup_EqualsALinqRestatementOverTheSeededSpans()
    {
        var spans = SeedSpans(DateTime.UtcNow.AddHours(-1));
        await WriteAsync(spans, SeedLogs(DateTime.UtcNow.AddHours(-1)));

        var inbound = spans.Where(s => s.Kind is SpanKind.SERVER or SpanKind.CONSUMER).ToList();
        var expected = inbound
            .GroupBy(s => (SeededDataBuilder_Service(s), MinuteOf(s.StartTimeUnixNano)))
            .ToDictionary(g => g.Key, g =>
            {
                var durations = g.Select(s => Math.Max(0, s.EndTimeUnixNano - s.StartTimeUnixNano)).ToList();
                var bands = new long[DurationBands.Count];
                foreach (var d in durations) bands[DurationBands.IndexOf(d)]++;
                return new[] { (long)g.Count(), g.Count(s => s.StatusCode == SpanStatusCode.ERROR), durations.Sum(), durations.Max() }
                    .Concat(bands).ToArray();
            });

        var actual = await ReadRequestsAsync();
        Assert.Equal(expected.Keys.OrderBy(k => k).ToList(), actual.Keys.OrderBy(k => k).ToList());
        foreach (var (key, values) in expected)
            Assert.True(values.SequenceEqual(actual[key]), $"{key}: expected [{string.Join(",", values)}] got [{string.Join(",", actual[key])}]");

        // Only inbound spans are rolled up.
        Assert.Equal(inbound.Count, actual.Values.Sum(v => v[0]));
    }

    [Theory]
    [InlineData(60, null)]
    [InlineData(3600, null)]
    [InlineData(300, "checkout-api")]
    public async Task RequestRollupRead_EqualsALinqRestatement_PerChartBucketAndService(int bucketSeconds, string? service)
    {
        var at = DateTime.UtcNow.AddHours(-1);
        var spans = SeedSpans(at);
        await WriteAsync(spans, SeedLogs(at));

        var width = bucketSeconds * 1_000_000_000L;
        var baseMinute = MinuteOf(SeededDataBuilder.ToUnixNano(at));
        var query = new RollupQuery
        {
            StartNano = baseMinute, EndNano = baseMinute + 3 * Minute, BucketSeconds = bucketSeconds, Service = service
        };

        // Only the first two of the three seeded minutes: the third must be excluded by the range.
        query = new RollupQuery { StartNano = baseMinute, EndNano = baseMinute + 2 * Minute, BucketSeconds = bucketSeconds, Service = service };
        var expected = spans
            .Where(s => s.Kind is SpanKind.SERVER or SpanKind.CONSUMER)
            .Where(s => MinuteOf(s.StartTimeUnixNano) >= query.StartNano && MinuteOf(s.StartTimeUnixNano) < query.EndNano)
            .Where(s => service == null || SeededDataBuilder_Service(s) == service)
            .GroupBy(s => (MinuteOf(s.StartTimeUnixNano) / width * width, SeededDataBuilder_Service(s)))
            .ToDictionary(g => g.Key, g =>
            {
                var durations = g.Select(s => Math.Max(0, s.EndTimeUnixNano - s.StartTimeUnixNano)).ToList();
                var bands = new long[DurationBands.Count];
                foreach (var d in durations) bands[DurationBands.IndexOf(d)]++;
                return (Count: (long)g.Count(), Errors: (long)g.Count(s => s.StatusCode == SpanStatusCode.ERROR),
                        Sum: durations.Sum(), Max: durations.Max(), Bands: bands);
            });

        using var scope = Scope();
        var result = await scope.ServiceProvider.GetRequiredService<IRollupReadRepository>().GetRequestRollupAsync(query);

        Assert.False(result.TimedOut);
        Assert.NotEmpty(expected);
        var actual = result.Rows.ToDictionary(r => (r.BucketStartNano, r.Service));
        Assert.Equal(expected.Keys.OrderBy(k => k).ToList(), actual.Keys.OrderBy(k => k).ToList());
        foreach (var (key, e) in expected)
        {
            var a = actual[key];
            Assert.Equal(e.Count, a.RequestCount);
            Assert.Equal(e.Errors, a.ErrorCount);
            Assert.Equal(e.Sum, a.SumDurationNanos);
            Assert.Equal(e.Max, a.MaxDurationNanos);
            Assert.True(e.Bands.SequenceEqual(a.Bands), $"{key}: bands differ");
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(17, null)]
    [InlineData(9, "payments-worker")]
    public async Task LogRollupRead_EqualsALinqRestatement_AndTheSeverityFilterLeavesNullOut(int? minSeverity, string? service)
    {
        var at = DateTime.UtcNow.AddHours(-1);
        var logs = SeedLogs(at);
        await WriteAsync(SeedSpans(at), logs);

        var width = 600 * 1_000_000_000L;
        var baseMinute = MinuteOf(SeededDataBuilder.ToUnixNano(at));
        var query = new RollupQuery
        {
            StartNano = baseMinute, EndNano = baseMinute + 3 * Minute, BucketSeconds = 600, Service = service, MinSeverity = minSeverity
        };
        var expected = logs
            .Where(l => service == null || (string)l.Resource!.Attributes["service.name"] == service)
            .Where(l => minSeverity == null || (l.SeverityNumber.HasValue && l.SeverityNumber >= minSeverity))
            .GroupBy(l => (MinuteOf(l.TimeUnixNano!.Value) / width * width, l.SeverityNumber ?? -1))
            .ToDictionary(g => g.Key, g => (long)g.Count());

        using var scope = Scope();
        var result = await scope.ServiceProvider.GetRequiredService<IRollupReadRepository>().GetLogRollupAsync(query);

        Assert.False(result.TimedOut);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.OrderBy(k => k.Key).ToList(),
            result.Rows.ToDictionary(r => (r.BucketStartNano, r.SeverityNumber), r => r.RecordCount).OrderBy(k => k.Key).ToList());
    }

    [Fact]
    public async Task RollupRead_IsScopedToTheTenant()
    {
        var at = DateTime.UtcNow.AddHours(-1);
        await WriteAsync(SeedSpans(at), SeedLogs(at));
        var baseMinute = MinuteOf(SeededDataBuilder.ToUnixNano(at));

        var other = await _fixture.SeedTenantAsync("rollup-other-tenant", "rollup-other-key", "rollup-other-plaintext");
        _fixture.TenantContext.SetTenantId(other.Id);
        try
        {
            using var scope = Scope();
            var repo = scope.ServiceProvider.GetRequiredService<IRollupReadRepository>();
            var query = new RollupQuery { StartNano = baseMinute, EndNano = baseMinute + 3 * Minute, BucketSeconds = 60 };
            Assert.Empty((await repo.GetRequestRollupAsync(query)).Rows);
            Assert.Empty((await repo.GetLogRollupAsync(query)).Rows);
        }
        finally
        {
            _fixture.TenantContext.SetTenantId(_fixture.TenantId);
        }
    }

    [Fact]
    public async Task SlowRequestCount_IsExactAtTheThresholdPlusMinusOneMs_AndCountsOnlyInboundSpans()
    {
        var at = DateTime.UtcNow.AddMinutes(-30);
        var start = SeededDataBuilder.ToUnixNano(at);
        long Ms(int ms) => ms * 1_000_000L;
        var spans = new List<SpanModel>
        {
            Span(_fixture.TenantId, "checkout-api", SpanKind.SERVER, start + 1_000, Ms(499), false, 1),
            Span(_fixture.TenantId, "checkout-api", SpanKind.SERVER, start + 2_000, Ms(500), false, 2),
            Span(_fixture.TenantId, "checkout-api", SpanKind.CONSUMER, start + 3_000, Ms(501), false, 3),
            Span(_fixture.TenantId, "payments-worker", SpanKind.SERVER, start + 4_000, Ms(900), false, 4),
            Span(_fixture.TenantId, "checkout-api", SpanKind.CLIENT, start + 5_000, Ms(2000), false, 5),   // not inbound
            Span(_fixture.TenantId, "checkout-api", SpanKind.INTERNAL, start + 6_000, Ms(2000), false, 6), // not inbound
            Span(_fixture.TenantId, "checkout-api", SpanKind.SERVER, start + 2 * 3_600_000_000_000L, Ms(5000), false, 7) // outside the window
        };
        await WriteAsync(spans, []);

        using var scope = Scope();
        var traces = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var from = at.AddMinutes(-1);
        var to = at.AddMinutes(10);

        Assert.Equal(3, (await traces.CountSlowInboundSpansAsync(from, to, null, 500)).Count);        // 500, 501, 900
        Assert.Equal(2, (await traces.CountSlowInboundSpansAsync(from, to, null, 501)).Count);        // 501, 900
        Assert.Equal(4, (await traces.CountSlowInboundSpansAsync(from, to, null, 499)).Count);        // 499 included
        Assert.Equal(1, (await traces.CountSlowInboundSpansAsync(from, to, null, 502)).Count);        // 900
        Assert.Equal(2, (await traces.CountSlowInboundSpansAsync(from, to, "checkout-api", 500)).Count);
        Assert.Equal(1, (await traces.CountSlowInboundSpansAsync(from, to, "payments-worker", 500)).Count);
        Assert.False((await traces.CountSlowInboundSpansAsync(from, to, null, 500)).TimedOut);
    }

    private static string SeededDataBuilder_Service(SpanModel s) => (string)s.Resource!.Attributes["service.name"];

    [Fact]
    public async Task LogRollup_EqualsALinqRestatementOverTheSeededLogs_WithNullSeverityAsMinusOne()
    {
        var logs = SeedLogs(DateTime.UtcNow.AddHours(-1));
        await WriteAsync(SeedSpans(DateTime.UtcNow.AddHours(-1)), logs);

        var expected = logs
            .GroupBy(l => ((string)l.Resource!.Attributes["service.name"], l.SeverityNumber ?? -1, MinuteOf(l.TimeUnixNano!.Value)))
            .ToDictionary(g => g.Key, g => (long)g.Count());
        var actual = await ReadLogsAsync();

        Assert.Equal(expected.OrderBy(k => k.Key).ToList(), actual.OrderBy(k => k.Key).ToList());
        Assert.Contains(actual.Keys, k => k.Severity == -1);
    }

    [Fact]
    public async Task Retention_RemovesExpiredRollupRows_WithTheirSignal_AndKeepsCurrentOnes()
    {
        var old = DateTime.UtcNow.AddDays(-100);
        var now = DateTime.UtcNow;
        await WriteAsync(SeedSpans(old).Concat(SeedSpans(now).Select(Renumber)).ToList(), SeedLogs(old).Concat(SeedLogs(now)).ToList());
        var requestsBefore = await CountAsync("request_rollup_minute");
        var logsBefore = await CountAsync("log_rollup_minute");
        Assert.True(requestsBefore > 0 && logsBefore > 0);

        using (var scope = Scope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
            await retention.DeleteOldTracesAsync(TimeSpan.FromDays(90));
            await retention.DeleteOldLogRecordsAsync(TimeSpan.FromDays(90));
        }

        var requestsAfter = await QueryAsync(
            $"SELECT MIN(bucket_start_unix_nano), COUNT(*) FROM request_rollup_minute WHERE tenant_id = {_fixture.TenantId}");
        var logsAfter = await QueryAsync(
            $"SELECT MIN(bucket_start_unix_nano), COUNT(*) FROM log_rollup_minute WHERE tenant_id = {_fixture.TenantId}");
        var cutoff = SeededDataBuilder.ToUnixNano(DateTime.UtcNow.AddDays(-90));
        Assert.InRange(Convert.ToInt64(requestsAfter[0][1]), 1, requestsBefore - 1);
        Assert.InRange(Convert.ToInt64(logsAfter[0][1]), 1, logsBefore - 1);
        Assert.True(Convert.ToInt64(requestsAfter[0][0]) > cutoff);
        Assert.True(Convert.ToInt64(logsAfter[0][0]) > cutoff);
    }

    private static int _renumber = 100_000;
    /// <summary>Gives a span new ids so the second seed does not repeat the first's trace/span ids.</summary>
    private static SpanModel Renumber(SpanModel s)
    {
        var n = Interlocked.Increment(ref _renumber);
        s.TraceIdHex = n.ToString("x32");
        s.SpanIdHex = n.ToString("x16");
        return s;
    }
}
