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

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddClickHouseCollectorServices(configuration);
        services.AddClickHouseApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        await using var conn = new ClickHouseConnection(_container.ConnectionString);
        await conn.OpenAsync();
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        string[] tables =
        [
            "log_records", "spans", "metrics", "metric_last_seen", "gauge_data_points", "sum_data_points",
            "histogram_data_points", "exponential_histogram_data_points", "summary_data_points",
            "trace_index", "request_rollup_minute", "log_rollup_minute"
        ];
        foreach (var table in tables)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE {table}";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
