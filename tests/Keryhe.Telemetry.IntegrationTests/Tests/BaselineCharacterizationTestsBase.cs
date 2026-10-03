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
    private static readonly DateTime SummaryWindowStart = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

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

    /// <summary>
    /// Replaces the retired <c>GetLogHistogramAsync</c> baseline (list-pages-server-side plan,
    /// Phase 2: "these tests are replaced, not deleted, as each phase changes the behavior they pin
    /// down"). Schema 3.0.0 has no rollup tables, so <c>GetLogSummaryAsync</c> has one (raw) path.
    /// </summary>
    [Fact]
    public async Task LogSummary_Totals_MatchSeededWindow()
    {
        // Its own window: log rows are plain appends on every provider (schema 3.0.0), so a window
        // another test in this shared fixture also seeded would count twice.
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, SummaryWindowStart, count: 600);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var summary = await repo.GetLogSummaryAsync(new LogSummaryQuery
        {
            Start = SummaryWindowStart.AddMinutes(-1),
            End = SummaryWindowStart.AddHours(1),
            BucketCount = 24
        });

        Assert.Equal("raw", summary.Source);
        Assert.Equal(logs.Count, summary.Total);

        var expectedErrors = logs.Count(l => l.SeverityNumber == 17);
        Assert.Equal(expectedErrors, summary.Buckets.Sum(b => b.Error));
    }

    /// <summary>
    /// Replaces the retired <c>GetTraceOverviewAsync</c> baseline (list-pages-server-side plan,
    /// Phase 3: "these tests are replaced, not deleted"). <c>BasicTraceWindow</c>'s roots are each
    /// trace's earliest span and all <c>SERVER</c> kind, so every trace's anchor is its root and the
    /// inbound-anchor restriction (schema-simplification decision 12) doesn't change the expected counts.
    /// </summary>
    [Fact]
    public virtual async Task TraceSummary_Totals_ErrorCounts_And_Percentiles_MatchSeededWindow()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 200);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        var summary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery
        {
            Start = WindowStart.AddMinutes(-1),
            End = WindowStart.AddHours(1),
            BucketCount = 24,
            Mode = "all",
            // The summary is pinned like the page (3.0.1); a future pin keeps this about the aggregates, not
            // PostgreSQL/Timescale's 5-second "now minus 5s" margin (the pin has its own tests).
            AsOf = DateTime.UtcNow.AddMinutes(5)
        });

        var expectedRootCount = spans.Count(s => s.ParentSpanIdHex == null);
        var expectedErrorCount = spans.Count(s => s.ParentSpanIdHex == null && s.StatusCode == SpanStatusCode.ERROR);

        Assert.Equal("raw", summary.Source);
        Assert.Equal(expectedRootCount, summary.Summary.Count);
        Assert.Equal(expectedErrorCount, summary.Summary.ErrorCount);
        Assert.Equal(expectedRootCount, summary.ListTotal);
        Assert.True(summary.Summary.P50Ms > 0);
        Assert.True(summary.Summary.P95Ms >= summary.Summary.P50Ms);
        Assert.True(summary.Summary.P99Ms >= summary.Summary.P95Ms);
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
    /// over the whole window, not just whatever survived a row cap. Originally written against
    /// the pre-Phase-4 <c>GetGroupedMetricSeriesAsync</c>'s raw point list; updated for Phase 4's
    /// bucketed <c>GetMetricSeriesAsync(MetricSeriesQuery)</c> shape, which has no raw point list
    /// to sum any more — instead this asserts the filtered-out pod's data never contributes to any
    /// bucket (every bucket's value comes from exactly one stream, "shared-svc-3"'s own gauge
    /// values), using a wide bucket count so each of the 30 seeded points gets its own bucket.
    /// </summary>
    [Fact]
    public async Task MetricSeries_LabelFilter_MatchesEveryRow_NotJustTheCappedSample()
    {
        var metrics = SeededDataBuilder.MultiInstanceGauge(_fixture.TenantId, WindowStart, instanceCount: 4, pointsPerInstance: 30);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var result = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = "phase0.cpu.utilization",
            Start = WindowStart.AddMinutes(-1),
            End = WindowStart.AddHours(1),
            LabelFilters = new Dictionary<string, string> { ["k8s.pod.name"] = "shared-svc-3" },
            Points = 1000,
            Top = 8
        });

        Assert.NotNull(result);
        var expected = metrics.Single(m => m.GaugeDataPoints!.All(p => (string)p.Attributes!["k8s.pod.name"] == "shared-svc-3"));
        var expectedValues = expected.GaugeDataPoints!
            .Select(p => p.ValueDouble ?? p.ValueInt ?? 0)
            .OrderBy(v => v)
            .ToList();

        // With the filter applied, only the one matching stream should ever contribute — so every
        // non-empty bucket's value must be one of that stream's own raw values (never another
        // pod's), and the bucketed series must carry the same number of non-empty buckets as the
        // matching stream's own point count (one point per bucket at this resolution).
        var nonEmpty = result!.Series.Single().Points.Where(p => p.Value.HasValue).ToList();
        Assert.Equal(expected.GaugeDataPoints!.Count, nonEmpty.Count);
        foreach (var point in nonEmpty)
            Assert.Contains(expectedValues, v => Math.Abs(v - point.Value!.Value) < 0.0001);
    }
}
