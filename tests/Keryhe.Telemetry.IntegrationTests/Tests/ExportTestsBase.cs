using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Phase 8 export checks (list-pages-server-side plan, Verification item 12), run directly against
/// each provider's read repository — the same "call the repository through DI, assert against a
/// hand-computed expectation" shape <see cref="SearchParityTestsBase"/> and the other phase test
/// bases use, rather than driving the controllers over HTTP (this test project has no
/// WebApplicationFactory/TestServer harness; see the Phase 8 CLAUDE.md notes for why the
/// concurrency-gate/CSV-escaping/window-guard checks are covered as separate pure-logic unit tests
/// instead of per-provider HTTP tests).
///
/// Covers the plan's "auto" Export checks that are meaningfully per-provider: row-count parity
/// against the corresponding summary/listTotal for logs and traces, and the metrics export's
/// "every series, no top-N truncation" behavior (decision 29) for a metric with more than 8
/// streams.
/// </summary>
public abstract class ExportTestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected ExportTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    [Fact]
    public async Task LogsExport_RowCount_MatchesSummaryTotal()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 300);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var windowEnd = WindowStart.AddSeconds(305);
        var summary = await repo.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = windowEnd, AsOf = DateTime.UtcNow.AddMinutes(1) });

        var exportedCount = 0;
        await foreach (var _ in repo.ExportLogsAsync(new LogExportQuery { Start = WindowStart, End = windowEnd }))
            exportedCount++;

        Assert.Equal(300, exportedCount);
        Assert.Equal(summary.Total, exportedCount);
    }

    [Fact]
    public async Task LogsExport_HonorsServiceFilter_SameAsSummary()
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count: 300);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();

        var windowEnd = WindowStart.AddSeconds(305);
        var summary = await repo.GetLogSummaryAsync(new LogSummaryQuery { Start = WindowStart, End = windowEnd, Service = "checkout-api", AsOf = DateTime.UtcNow.AddMinutes(1) });

        var exportedCount = 0;
        await foreach (var log in repo.ExportLogsAsync(new LogExportQuery { Start = WindowStart, End = windowEnd, Service = "checkout-api" }))
        {
            Assert.Equal("checkout-api", log.Resource?.Attributes["service.name"]?.ToString());
            exportedCount++;
        }

        Assert.Equal(summary.Total, exportedCount);
        Assert.Equal(100, exportedCount); // one third of 300 rows, by BasicLogWindow's round-robin service assignment
    }

    [Fact]
    public async Task TracesExport_RowCount_MatchesListTotal()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 150);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        var windowEnd = WindowStart.AddSeconds(155);
        var summary = await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = WindowStart, End = windowEnd, Mode = "all", AsOf = DateTime.UtcNow.AddMinutes(1) });

        var exportedTraceIds = new HashSet<string>();
        await foreach (var trace in repo.ExportTracesAsync(new TraceExportQuery { Start = WindowStart, End = windowEnd, Mode = "all" }))
            exportedTraceIds.Add(trace.TraceIdHex);

        Assert.Equal(150, exportedTraceIds.Count);
        Assert.Equal(summary.ListTotal, exportedTraceIds.Count);
    }

    /// <summary>Exercises the chunked export path across more than one internal chunk (the trace repository chunks at 1000 anchors per round trip).</summary>
    [Fact]
    public async Task TracesExport_SpansMultipleInternalChunks_NoGapsOrDuplicates()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 1500);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ITraceReadRepository>();

        var windowEnd = WindowStart.AddSeconds(1505);
        var exportedTraceIds = new List<string>();
        await foreach (var trace in repo.ExportTracesAsync(new TraceExportQuery { Start = WindowStart, End = windowEnd, Mode = "all" }))
            exportedTraceIds.Add(trace.TraceIdHex);

        Assert.Equal(1500, exportedTraceIds.Count);
        Assert.Equal(1500, exportedTraceIds.Distinct().Count());
    }

    /// <summary>
    /// Decision 29's core check: a metric with more than 8 streams exports EVERY series, unlike
    /// <c>/series</c>'s default top-8-plus-other. <see cref="SeededDataBuilder.ManyStreamsForTopN"/>
    /// seeds 10 single-point gauge streams, one per service — the chart endpoint folds the bottom 2
    /// into "other" (see <c>MetricPhase4TestsBase</c>'s own top-N test), but export must return a
    /// row for all 10 (row count = series × buckets = 10 × 1 requested bucket point covering the
    /// single seeded timestamp).
    /// </summary>
    [Fact]
    public async Task MetricsExport_IncludesEverySeries_NoTopNTruncation()
    {
        const int streamCount = 10;
        const string metricName = "phase8.export.topn.gauge";
        // Distinct metric name/service prefix from MetricPhase4TestsBase's own top-N test: both
        // classes share this provider's fixture collection, and the process-lifetime
        // ResourceScopeCache isn't cleared between test classes even though `metrics` itself is
        // truncated (see ManyStreamsForTopN's doc comment) — reusing the exact same identity would
        // hand this flush a stale cached metric id and fail its data-point insert on the FK.
        var metrics = SeededDataBuilder.ManyStreamsForTopN(_fixture.TenantId, WindowStart, streamCount, metricName, servicePrefix: "export-svc");
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        // Sanity check the fixture's own premise: the chart endpoint (default top=8) really does
        // fold 2 series into "other" for this data.
        var chart = await repo.GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = metricName,
            Start = WindowStart,
            End = WindowStart.AddSeconds(2),
            Points = 1
        });
        Assert.NotNull(chart);
        Assert.Equal(8, chart!.Series.Count);
        Assert.NotNull(chart.Other);
        Assert.Equal(2, chart.Other!.SeriesCount);

        var exportedSeriesNames = new HashSet<string>();
        var exportedRowCount = 0;
        await foreach (var row in repo.ExportMetricSeriesAsync(new MetricExportQuery
        {
            MetricName = metricName,
            Start = WindowStart,
            End = WindowStart.AddSeconds(2),
            Points = 1
        }))
        {
            exportedSeriesNames.Add(row.SeriesName);
            exportedRowCount++;
        }

        Assert.Equal(streamCount, exportedSeriesNames.Count); // every series, not just the top 8
        Assert.Equal(streamCount * 1, exportedRowCount); // series × buckets (1 requested point)
    }

    /// <summary>
    /// The export row carries the same elapsed-time-based <c>Rate</c> as the chart endpoint
    /// (<c>MetricPhase4TestsBase.SumCumulative_RateUsesElapsedTimeNotBucketWidth</c>) — added to
    /// <see cref="MetricExportRow"/>/the CSV export as a still-not-changed follow-up to the metric
    /// chart correctness fixes. +15/15s → 1/s; one wide bucket covers the whole 60s stream.
    /// </summary>
    [Fact]
    public async Task MetricsExport_IncludesRate()
    {
        // Distinct service name from MetricPhase4TestsBase's own rate test (same metric name is
        // hardcoded in the shared builder): both classes share this provider's fixture collection,
        // and the process-lifetime ResourceScopeCache isn't cleared between test classes even
        // though `metrics`/`sum_data_points` are truncated (see ManyStreamsForTopN's doc comment)
        // — reusing the exact same resource+metric identity would hand this flush a stale cached
        // metric id and fail its data-point insert on the FK.
        var metric = SeededDataBuilder.CumulativeCounterSeries(_fixture.TenantId, WindowStart, [15, 30, 45], intervalSeconds: 15, serviceName: "rate-export-svc");
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var rows = new List<MetricExportRow>();
        await foreach (var row in repo.ExportMetricSeriesAsync(new MetricExportQuery
        {
            MetricName = "correctness.requests.cumulative",
            Start = WindowStart,
            End = WindowStart.AddSeconds(60),
            Points = 600
        }))
            rows.Add(row);

        var valued = rows.Where(r => r.Value.HasValue).ToList();
        Assert.Equal(3, valued.Count);
        Assert.All(valued, r => Assert.Equal(1.0, r.Rate!.Value, precision: 6));
    }

    [Fact]
    public async Task MetricsExport_UnknownMetricName_ReturnsEmpty()
    {
        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var count = 0;
        await foreach (var _ in repo.ExportMetricSeriesAsync(new MetricExportQuery
        {
            MetricName = "no.such.metric",
            Start = WindowStart,
            End = WindowStart.AddMinutes(1)
        }))
            count++;

        Assert.Equal(0, count);
    }
}
