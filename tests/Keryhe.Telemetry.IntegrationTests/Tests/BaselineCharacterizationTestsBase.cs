using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Characterizes today's (pre-list-pages-server-side-plan) behavior for a seeded window, so any
/// later phase that changes it does so deliberately (plan's Phase 0 section: "these tests are
/// replaced, not deleted"). One concrete subclass per provider supplies the fixture and the
/// <c>[Collection]</c>/<c>[Trait("Provider", …)]</c> pairing; the test bodies are shared.
/// </summary>
public abstract class BaselineCharacterizationTestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected BaselineCharacterizationTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    [Fact]
    public async Task SchemaApply_And_Registration_SmokeTest()
    {
        using var scope = Scope();
        var bulkWriter = scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
        var traceReads = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var logReads = scope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var metricReads = scope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        Assert.NotNull(bulkWriter);
        Assert.NotNull(traceReads);
        Assert.NotNull(logReads);
        Assert.NotNull(metricReads);
    }

    [Fact]
    public async Task SeedThenReadBack_Logs_RoundTrip()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 600);

        using (var writeScope = Scope())
        {
            var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushLogsAsync(logs);
        }

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var records = await repo.GetLogRecordsByTimeRangeAsync(WindowStart.AddMinutes(-1), WindowStart.AddHours(1));

        Assert.Equal(logs.Count, records.Count());
    }

    [Fact]
    public async Task LogHistogram_Totals_MatchSeededWindow()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 600);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var buckets = await repo.GetLogHistogramAsync(new HistogramQuery
        {
            Start = WindowStart.AddMinutes(-1),
            End = WindowStart.AddHours(1),
            BucketCount = 24
        });

        var total = buckets.Sum(b => b.Trace + b.Debug + b.Info + b.Warn + b.Error + b.Fatal);
        Assert.Equal(logs.Count, total);

        var expectedErrors = logs.Count(l => l.SeverityNumber == 17);
        Assert.Equal(expectedErrors, buckets.Sum(b => b.Error));
    }

    [Fact]
    public virtual async Task TraceOverview_Totals_ErrorCounts_And_Percentiles_MatchSeededWindow()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 200);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        var overview = await repo.GetTraceOverviewAsync(new HistogramQuery
        {
            Start = WindowStart.AddMinutes(-1),
            End = WindowStart.AddHours(1),
            BucketCount = 24,
            Mode = "all"
        });

        var expectedRootCount = spans.Count(s => s.ParentSpanIdHex == null);
        var expectedErrorCount = spans.Count(s => s.ParentSpanIdHex == null && s.StatusCode == SpanStatusCode.ERROR);

        Assert.Equal(expectedRootCount, overview.Summary.Count);
        Assert.Equal(expectedErrorCount, overview.Summary.ErrorCount);
        Assert.True(overview.Summary.P50Ms > 0);
        Assert.True(overview.Summary.P95Ms >= overview.Summary.P50Ms);
        Assert.True(overview.Summary.P99Ms >= overview.Summary.P95Ms);
    }

    [Fact]
    public async Task OrphanTrace_Appears_In_TraceByIdLookup()
    {
        var spans = SeededDataBuilder.OrphanTrace(_fixture.TenantId, WindowStart);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var stored = await repo.GetTraceByIdAsync(spans[0].TraceIdHex);

        Assert.Equal(spans.Count, stored.Count);
    }

    /// <summary>
    /// The plan's Decision-1-fix regression check (list-pages-server-side.md Verification #7):
    /// a selective label filter on a metric with plenty of data must match every matching series
    /// over the whole window, not just whatever survived the row cap. Today's
    /// <c>GetGroupedMetricSeriesAsync</c> applies <c>FilterByLabelFilters</c> in C# AFTER the
    /// per-metric-row <c>LIMIT</c>, so a filter selective enough to be rare in the capped sample
    /// can come back sparse or empty even though matching data exists elsewhere in the window —
    /// this is exactly the "correctness, not just slowness" bug the plan's Phase 1 fixes. Skipped
    /// until then; un-skip when Phase 1 lands the fix (`AttributePredicate` compiled into SQL
    /// before `ORDER BY … LIMIT`).
    /// </summary>
    [Fact(Skip = "known bug: label filter runs after the row cap (GetGroupedMetricSeriesAsync/FilterByLabelFilters), fixed in phase 1 — see plans/list-pages-server-side.md decision 24 and Phase 1")]
    public async Task MetricSeries_LabelFilter_MatchesEveryRow_NotJustTheCappedSample()
    {
        var metrics = SeededDataBuilder.MultiInstanceGauge(_fixture.TenantId, WindowStart, instanceCount: 4, pointsPerInstance: 30);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var grouped = await repo.GetGroupedMetricSeriesAsync(
            "phase0.cpu.utilization",
            WindowStart.AddMinutes(-1),
            WindowStart.AddHours(1),
            labelFilters: new Dictionary<string, string> { ["k8s.pod.name"] = "shared-svc-3" });

        Assert.NotNull(grouped);
        var expectedPoints = metrics.Single(m => m.GaugeDataPoints!.All(p => (string)p.Attributes!["k8s.pod.name"] == "shared-svc-3"))
            .GaugeDataPoints!.Count;
        var actualPoints = grouped!.Series.Sum(s => s.Points.Count);
        Assert.Equal(expectedPoints, actualPoints);
    }
}
