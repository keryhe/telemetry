using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetTracePhase3Tests(ParquetFixture fixture) : TracePhase3TestsBase(fixture)
{
    // The hint names the hours to read, standing in for the trace_index lookup.
    protected override bool HonorsStartHint => true;
}

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetExportTests(ParquetFixture fixture) : ExportTestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetSearchParityTests(ParquetFixture fixture) : SearchParityTestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetSummaryTimeoutDegradationTests(ParquetFixture fixture) : SummaryTimeoutDegradationTestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetRollupWriteTests(ParquetFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var engine = fixture.Services.GetRequiredService<global::Keryhe.Telemetry.Parquet.ParquetQueryEngine>();
        return await engine.OpenAsync(
            [
                new global::Keryhe.Telemetry.Parquet.ParquetViewScope(global::Keryhe.Telemetry.Parquet.ParquetTables.RequestRollupTable, fixture.TenantId, null, null),
                new global::Keryhe.Telemetry.Parquet.ParquetViewScope(global::Keryhe.Telemetry.Parquet.ParquetTables.LogRollupTable, fixture.TenantId, null, null)
            ],
            default);
    }
}

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetRetentionTests(ParquetFixture fixture) : RetentionTestsBase(fixture)
{
    // Day folders are deleted whole: a row just past the cutoff survives until its day is wholly expired.
    protected override bool RowGranularRetention => false;

    // Counts across every tenant (one test seeds a tenant the control plane has never heard of): the files of all tenants' partitions.
    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        var parquetTable = table switch
        {
            "spans" => global::Keryhe.Telemetry.Parquet.ParquetTables.SpanTable,
            "request_rollup_minute" => global::Keryhe.Telemetry.Parquet.ParquetTables.RequestRollupTable,
            "log_records" => global::Keryhe.Telemetry.Parquet.ParquetTables.LogTable,
            "log_rollup_minute" => global::Keryhe.Telemetry.Parquet.ParquetTables.LogRollupTable,
            "gauge_data_points" => global::Keryhe.Telemetry.Parquet.ParquetTables.GaugeTable,
            "metrics" => global::Keryhe.Telemetry.Parquet.ParquetTables.MetricCatalogTable,
            _ => throw new NotSupportedException(table)
        };
        var store = new global::Keryhe.Telemetry.Parquet.LocalParquetFileStore(fixture.RootPath);
        var files = new List<string>();
        var hours = store.EnumeratePartitions(parquetTable.Name, null, DateOnly.MinValue, DateOnly.MaxValue).ToList();
        foreach (var tenant in hours.GroupBy(p => p.TenantId))
            foreach (var file in await global::Keryhe.Telemetry.Parquet.ParquetManifests.ResolveAsync(store, [.. tenant], default))
                files.Add(file.PathIn(store));
        if (files.Count == 0) return 0;
        var column = parquetTable.PartitionTimeColumn;
        using var conn = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        conn.Open();
        var list = string.Join(", ", files.Select(f => $"'{f.Replace("'", "''")}'"));
        return await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn,
            $"SELECT count(*) FROM read_parquet([{list}]) WHERE {column} >= {fromNano} AND {column} < {toNano}");
    }
}

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetTimedQueryTests(ParquetFixture fixture, ITestOutputHelper output) : TimedQueryProviderTestsBase(fixture, output)
{
    // A scan of billions of generated rows: well past the budget, bounded, and an error when interrupted.
    protected override string SlowSql => "SELECT sum(hash(i)) FROM range(40000000000) t(i)";
    // DuckDB.NET does not enforce CommandTimeout, as ClickHouse.Client does not: the token is the only thing bounding a summary.
    protected override bool DriverEnforcesCommandTimeout => false;
    protected override System.Data.Common.DbConnection CreateConnection(string connectionString)
        => new global::Keryhe.Telemetry.Parquet.NamedParameterConnection(new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:"));
}

// The same shared suites over compacted data (plans/parquet-duckdb phase 4): every span flush is followed by a forced
// compaction that day-merges everything and deletes what it superseded, so the suites read the compactor's output.
[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedTracePhase3Tests(ParquetCompactedFixture fixture) : TracePhase3TestsBase(fixture)
{
    protected override bool HonorsStartHint => true;
}

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedExportTests(ParquetCompactedFixture fixture) : ExportTestsBase(fixture);

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedSearchParityTests(ParquetCompactedFixture fixture) : SearchParityTestsBase(fixture);

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedRollupWriteTests(ParquetCompactedFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var engine = fixture.Services.GetRequiredService<global::Keryhe.Telemetry.Parquet.ParquetQueryEngine>();
        return await engine.OpenAsync(
            [
                new global::Keryhe.Telemetry.Parquet.ParquetViewScope(global::Keryhe.Telemetry.Parquet.ParquetTables.RequestRollupTable, fixture.TenantId, null, null),
                new global::Keryhe.Telemetry.Parquet.ParquetViewScope(global::Keryhe.Telemetry.Parquet.ParquetTables.LogRollupTable, fixture.TenantId, null, null)
            ],
            default);
    }
}

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedFixtureTests(ParquetCompactedFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Guards the compacted suites against passing vacuously: a flush through the fixture's writer must leave a merged day.
    [Fact]
    public async Task AFlushLeavesAMergedDay_AndNoWriterFilesBehind()
    {
        using var scope = fixture.Services.CreateScope();
        var resource = new global::Keryhe.Telemetry.Core.Models.ResourceModel
        {
            TenantId = fixture.TenantId,
            Attributes = new Dictionary<string, object> { ["service.name"] = "svc" }
        };
        var now = (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        var spans = Enumerable.Range(0, 3).Select(i => new global::Keryhe.Telemetry.Core.Models.SpanModel
        {
            TraceIdHex = Guid.NewGuid().ToString("N"),
            SpanIdHex = Guid.NewGuid().ToString("N")[..16],
            Name = "op",
            StartTimeUnixNano = now + i,
            EndTimeUnixNano = now + i + 1_000_000,
            Resource = resource,
            InstrumentationScope = new global::Keryhe.Telemetry.Core.Models.InstrumentationScopeModel { Name = "s", Version = "1" }
        }).ToList();

        await scope.ServiceProvider.GetRequiredService<global::Keryhe.Telemetry.Core.ITelemetryBulkWriter>().FlushTracesAsync(spans);

        var days = Directory.GetDirectories(fixture.RootPath, "_day", SearchOption.AllDirectories);
        Assert.Equal(2, days.Length);                                         // spans and trace_index (the spans are not inbound, so no rollup rows)
        Assert.All(days, d => Assert.True(File.Exists(Path.Combine(d, "_compacted.000001.json"))));
        var parquet = Directory.GetFiles(fixture.RootPath, "*.parquet", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}")).ToList();
        Assert.Equal(2, parquet.Count);
        Assert.All(parquet, f => Assert.Contains("_day", f));
    }
}

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetLogPhase2Tests(ParquetFixture fixture) : LogPhase2TestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetMetricPhase4Tests(ParquetFixture fixture) : MetricPhase4TestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetMetricPhase5Tests(ParquetFixture fixture) : MetricPhase5TestsBase(fixture)
{
    // The catalog is one row per (service, metric, type), as on ClickHouse (README D9).
    protected override bool CatalogInstanceIsPerResource => false;
}

[Collection(ProviderNames.Parquet)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetBaselineTests(ParquetFixture fixture) : BaselineCharacterizationTestsBase(fixture);

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedLogPhase2Tests(ParquetCompactedFixture fixture) : LogPhase2TestsBase(fixture);

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedMetricPhase4Tests(ParquetCompactedFixture fixture) : MetricPhase4TestsBase(fixture);

[Collection(ProviderNames.ParquetCompacted)]
[Trait("Provider", ProviderNames.Parquet)]
public sealed class ParquetCompactedMetricPhase5Tests(ParquetCompactedFixture fixture) : MetricPhase5TestsBase(fixture)
{
    protected override bool CatalogInstanceIsPerResource => false;
}
