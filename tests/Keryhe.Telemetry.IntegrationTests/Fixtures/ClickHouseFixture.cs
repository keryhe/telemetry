using ClickHouse.Client.ADO;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// No first-party Testcontainers module exists for ClickHouse, so this builds the container
/// directly against the official image, waiting on the HTTP interface (port 8123) that
/// <c>ClickHouse.Client</c> (and this provider) talks to.
/// </summary>
public sealed class ClickHouseFixture : ProviderFixture
{
    private readonly ClickHouseProviderContainer _container = new();

    public override string ProviderName => ProviderNames.ClickHouse;

    protected override ProviderContainer Container => _container;

    // The opt-in benchmarks read with the 25.x query condition cache off, or a repeated predicate looks free (Phase 0, spike 4).
    // The row-count tests read from system.query_log and are not affected, so they keep the production setting.
    protected override string ApiConnectionString =>
        Environment.GetEnvironmentVariable("TRACE_BENCH_OUT") is null && Environment.GetEnvironmentVariable("ROLLUP_BENCH_OUT") is null
            ? base.ApiConnectionString
            : base.ApiConnectionString + ";set_use_query_condition_cache=0";

    // The shared suites seed a few hundred thousand rows and assert how many rows a read touches, which assumes one part per
    // flush (what merges settle to in production). Splitting a large flush into parts is covered by ClickHouseParallelFlushTests.
    protected override IReadOnlyDictionary<string, string?> ProviderSettings => new Dictionary<string, string?>
    {
        ["Telemetry:ClickHouse:Ingestion:ParallelFlushMinRows"] = int.MaxValue.ToString()
    };

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddClickHouseCollectorServices(configuration);
        services.AddClickHouseApiServices(configuration);
        // ClickHouse has no control plane of its own: tenants, keys, alert rules and retention settings run on PostgreSQL.
        services.AddPostgreSqlControlPlaneCollectorServices(configuration);
        services.AddPostgreSqlControlPlaneApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        await using var conn = new ClickHouseConnection(_container.ConnectionString);
        await conn.OpenAsync();
        string[] tables =
        [
            "log_records", "spans", "gauge_points", "sum_points", "histogram_points", "exp_histogram_points",
            "summary_points", "trace_index", "metric_catalog", "metric_series", "request_rollup_minute", "log_rollup_minute"
        ];
        // the writer remembers which catalog/series rows it wrote; the tables just lost them
        ((Keryhe.Telemetry.ClickHouse.Services.ClickHouseBulkWriter)Services.GetRequiredService<Keryhe.Telemetry.Core.ITelemetryBulkWriter>()).ResetCatalogTrackers();
        foreach (var table in tables)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE {table}";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
