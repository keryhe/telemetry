using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Every summary-class read through the real repositories with <c>Telemetry:Query:SummaryTimeoutSeconds = 0</c>, which
/// forces each one down its timeout path deterministically, on every provider. Each must answer — flagged timed out or
/// lower-bounded — and never throw: in the 3.0.1 stress ramp these paths answered 500 instead (the drivers' own timeout
/// exceptions escaped <c>TimedQuery</c>, and the fallbacks reused the aborted connection). Covers:
/// <list type="bullet">
/// <item>trace and log summaries, whose capped-count fallback now runs on a fresh connection;</item>
/// <item>the dashboard's trace samples and the logs page's facets, which had no time bound at all;</item>
/// <item>the "last" trace and log pages, whose exact count now runs on its own connection so the page itself is read
/// on a connection that was never aborted;</item>
/// <item>metric series, whose quarter-resolution retry runs on a fresh connection.</item>
/// </list>
/// <see cref="TimedQueryProviderTestsBase"/> covers the drivers' exception types themselves.
/// </summary>
public abstract class SummaryTimeoutDegradationTestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected SummaryTimeoutDegradationTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowEnd = WindowStart.AddMinutes(30);

    [Fact]
    public async Task ZeroBudget_EverySummaryClassRead_DegradesInsteadOfThrowing()
    {
        using (var write = _fixture.Services.CreateScope())
        {
            var writer = write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushTracesAsync(SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 40));
            await writer.FlushLogsAsync(SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 60));
            // Service name unique to this test: the fixture keeps ResourceScopeCache across ResetAsync (see
            // MetricPhase4TestsBase.RedeliveredCumulativeBatch_...), so a metric identity must not be shared.
            await writer.FlushMetricsAsync(SeededDataBuilder.MultiInstanceGauge(_fixture.TenantId, WindowStart, serviceName: "timeout-degradation-svc", instanceCount: 2, pointsPerInstance: 5));
        }

        using var provider = _fixture.CreateServices(new Dictionary<string, string?>
        {
            ["Telemetry:Query:SummaryTimeoutSeconds"] = "0"
        });
        using var scope = provider.CreateScope();
        var traces = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var logs = scope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var metrics = scope.ServiceProvider.GetRequiredService<IMetricReadRepository>();
        var asOf = DateTime.UtcNow.AddMinutes(5); // past every seeded row's created_at, whatever the provider's asOf backoff

        var traceSummary = await traces.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = WindowEnd, AsOf = asOf });
        Assert.True(traceSummary.TimedOut);
        Assert.Empty(traceSummary.Buckets);
        Assert.True(traceSummary.TotalIsLowerBound);

        var logSummary = await logs.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = WindowEnd, AsOf = asOf });
        Assert.True(logSummary.TimedOut);
        Assert.True(logSummary.TotalIsLowerBound);

        var facets = await logs.GetLogFacetsAsync(new LogFacetsQuery { Start = WindowStart, End = WindowEnd });
        Assert.True(facets.TimedOut);
        Assert.Empty(facets.Facets);

        foreach (var kind in new[] { "errors", "slowest" })
        {
            var samples = await traces.GetTraceSamplesAsync(new TraceSamplesQuery { Start = WindowStart, End = WindowEnd, Kind = kind });
            Assert.True(samples.TimedOut, kind);
            Assert.Empty(samples.Items);
        }

        // The "last" pages: the exact count times out, so the page falls back to a full page of the oldest rows. The rows
        // must still come back, read on a connection the aborted count never touched.
        var lastTraces = await traces.GetTracePageAsync(new TraceQuery { Start = WindowStart, End = WindowEnd, Nav = "last", Size = 10, AsOf = asOf });
        Assert.Equal(10, lastTraces.Items.Count);

        var lastLogs = await logs.GetLogPageAsync(new LogQuery { Start = WindowStart, End = WindowEnd, Nav = "last", Size = 10, AsOf = asOf });
        Assert.Equal(10, lastLogs.Items.Count);

        var series = await metrics.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase0.cpu.utilization", Start = WindowStart, End = WindowEnd, Points = 10, Top = 8
        });
        Assert.NotNull(series);
        Assert.True(series!.TimedOut);
    }
}

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlSummaryTimeoutDegradationTests(PostgreSqlFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleSummaryTimeoutDegradationTests(TimescaleFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerSummaryTimeoutDegradationTests(SqlServerFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlSummaryTimeoutDegradationTests(MySqlFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseSummaryTimeoutDegradationTests(ClickHouseFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);
