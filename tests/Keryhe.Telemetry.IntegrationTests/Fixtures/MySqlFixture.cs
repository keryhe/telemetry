using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class MySqlFixture : ProviderFixture
{
    private readonly MySqlProviderContainer _container = new();

    public override string ProviderName => ProviderNames.MySql;

    protected override ProviderContainer Container => _container;

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddMySqlCollectorServices(configuration);
        services.AddMySqlApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        await using var conn = new MySqlConnection(_container.ConnectionString);
        await conn.OpenAsync();
        string[] statements =
        [
            "SET FOREIGN_KEY_CHECKS = 0",
            "TRUNCATE TABLE log_records",
            "TRUNCATE TABLE spans",
            "TRUNCATE TABLE gauge_data_points",
            "TRUNCATE TABLE sum_data_points",
            "TRUNCATE TABLE histogram_data_points",
            "TRUNCATE TABLE exponential_histogram_data_points",
            "TRUNCATE TABLE summary_data_points",
            "TRUNCATE TABLE metrics",
            "TRUNCATE TABLE metric_last_seen",
            "TRUNCATE TABLE log_rollup_minute",
            "TRUNCATE TABLE log_rollup_hour",
            "TRUNCATE TABLE orphan_roots",
            "TRUNCATE TABLE trace_rollup_minute",
            "TRUNCATE TABLE trace_rollup_hour",
            "UPDATE rollup_state SET coverage_start_unix_nano = NULL, rolled_until_unix_nano = 0, repassed_until_unix_nano = 0, lease_owner = NULL, lease_expires_at = '1970-01-01 00:00:00'",
            "SET FOREIGN_KEY_CHECKS = 1"
        ];
        foreach (var statement in statements)
        {
            await using var cmd = new MySqlCommand(statement, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
