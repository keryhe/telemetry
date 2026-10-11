using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The stress harness's Parquet observer (plans/parquet-duckdb phase 6 decision 4) against a real root: its row counts must equal what was
/// written (and split by age at the cutoff), the table list must carry the manifests' rows, and the statement timings must come out of a
/// host log of the shape the read engine writes. A wrong table mapping or column name fails here instead of reading as zero rows in a run.
/// </summary>
[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetObserverTests(ParquetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Counts_rows_per_tenant_and_age_and_reads_statement_timings_from_the_host_log()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-3);
        var spans = new List<SpanModel>();
        spans.AddRange(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, now, traceCount: 5, seedOffset: 1));
        spans.AddRange(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, old, traceCount: 3, seedOffset: 2));
        var logs = SeededDataBuilder.BasicLogWindow(fixture.TenantId, now, count: 7);
        using (var scope = fixture.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            await writer.FlushTracesAsync(spans);
            await writer.FlushLogsAsync(logs);
        }

        var observer = new ParquetObserver(fixture.RootPath);
        var cutoff = SeededDataBuilder.ToUnixNano(now.AddDays(-1));
        var counts = await observer.CountRowsAsync(cutoff, default);

        long Of(string table, bool backdated) => counts.Cells.Where(c => c.Table == table && c.Backdated == backdated && c.TenantId == fixture.TenantId).Sum(c => c.Rows);
        Assert.Equal(spans.Count(s => (long)s.StartTimeUnixNano >= cutoff), Of("spans", false));
        Assert.Equal(spans.Count(s => (long)s.StartTimeUnixNano < cutoff), Of("spans", true));
        Assert.Equal(logs.Count, Of("log_records", false));
        Assert.True(Of("request_rollup_minute", false) + Of("request_rollup_minute", true) > 0, "the request rollup is counted");

        var tables = await observer.ReadAsync(default);
        Assert.Equal(spans.Count, Assert.Single(tables, t => t.Table == "spans").Rows);

        var log = Path.Combine(Path.GetTempPath(), "keryhe-observer-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var at = DateTime.UtcNow.AddSeconds(-60).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            await File.WriteAllLinesAsync(log,
            [
                $"{at} info: Keryhe.Api[0] something else",
                $"{at} dbug: Keryhe.Telemetry.Parquet.ParquetQueryEngine[0] parquet-sql ms=12.5 files=40 sql=SELECT 1 FROM spans",
                $"{at} dbug: Keryhe.Telemetry.Parquet.ParquetQueryEngine[0] parquet-sql ms=7.5 files=60 sql=SELECT 1 FROM spans"
            ]);
            observer.AttachHostLog(log);
            await observer.ResetAsync(default);
            var snapshot = await observer.SnapshotAsync(10, default);
            Assert.Empty(snapshot.ByTotal); // lines before the window start are excluded

            var fresh = new ParquetObserver(fixture.RootPath);
            fresh.AttachHostLog(log);
            var stat = Assert.Single((await fresh.SnapshotAsync(10, default)).ByTotal);
            Assert.Equal("SELECT 1 FROM spans", stat.Query);
            Assert.Equal(2, stat.Calls);
            Assert.Equal(20.0, stat.TotalMs, 3);
            Assert.Equal(100, stat.Rows); // files read
        }
        finally { File.Delete(log); }
    }
}
