using Microsoft.Data.SqlClient;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class SqlServerFixture : ProviderFixture
{
    private readonly SqlServerProviderContainer _container = new();

    public override string ProviderName => ProviderNames.SqlServer;

    protected override ProviderContainer Container => _container;

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSqlServerCollectorServices(configuration);
        services.AddSqlServerApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        await using var conn = new SqlConnection(_container.ConnectionString);
        await conn.OpenAsync();
        // No CASCADE-capable multi-table TRUNCATE on SQL Server: delete children before parents.
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        const string sql = """
            DELETE FROM spans;
            DELETE FROM gauge_data_points;
            DELETE FROM sum_data_points;
            DELETE FROM histogram_data_points;
            DELETE FROM exponential_histogram_data_points;
            DELETE FROM summary_data_points;
            DELETE FROM metrics;
            DELETE FROM metric_last_seen;
            DELETE FROM log_records;
            DELETE FROM log_rollup_minute;
            DELETE FROM log_rollup_hour;
            DELETE FROM orphan_roots;
            DELETE FROM trace_rollup_minute;
            DELETE FROM trace_rollup_hour;
            UPDATE rollup_state SET coverage_start_unix_nano = NULL, rolled_until_unix_nano = 0, repassed_until_unix_nano = 0, lease_owner = NULL, lease_expires_at = '1970-01-01T00:00:00';
            """;
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }
}
