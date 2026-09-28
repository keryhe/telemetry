using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>The TimescaleDB image is Postgres-compatible (same entrypoint/env vars/port), so the plain Postgres Testcontainers module works unchanged with the Timescale image swapped in.</summary>
public sealed class TimescaleFixture : ProviderFixture
{
    private readonly TimescaleProviderContainer _container = new();

    public override string ProviderName => ProviderNames.Timescale;

    protected override ProviderContainer Container => _container;

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddTimescaleCollectorServices(configuration);
        services.AddTimescaleApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        // See PostgreSqlFixture.ResetAsync's comment: resources/instrumentation_scopes are
        // deliberately left alone because ResourceScopeCache is a process-lifetime singleton here
        // and is never invalidated.
        await using var conn = new NpgsqlConnection(_container.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE log_records, spans, metrics, metric_last_seen,
                gauge_data_points, sum_data_points, histogram_data_points,
                exponential_histogram_data_points, summary_data_points,
                log_rollup_minute, log_rollup_hour, orphan_roots, trace_rollup_minute, trace_rollup_hour
            RESTART IDENTITY CASCADE
            """, conn);
        await cmd.ExecuteNonQueryAsync();

        // See PostgreSqlFixture.ResetAsync's comment: rollup coverage is process-run state, reset
        // between test classes the same way signal data is cleared.
        await using var resetRollupState = new NpgsqlCommand(
            "UPDATE rollup_state SET coverage_start_unix_nano = NULL, rolled_until_unix_nano = 0, repassed_until_unix_nano = 0, lease_owner = NULL, lease_expires_at = 'epoch'",
            conn);
        await resetRollupState.ExecuteNonQueryAsync();
    }
}
