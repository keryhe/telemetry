using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class PostgreSqlFixture : ProviderFixture
{
    private readonly PostgreSqlProviderContainer _container = new();

    public override string ProviderName => ProviderNames.PostgreSql;

    protected override ProviderContainer Container => _container;

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPostgreSqlCollectorServices(configuration);
        services.AddPostgreSqlApiServices(configuration);
    }

    public override async Task ResetAsync()
    {
        // Deliberately leaves resources/instrumentation_scopes alone: ResourceScopeCache is a
        // process-lifetime singleton here (registered once for the whole fixture, like the real
        // hosts) that is never invalidated -- see its doc comment. Truncating the catalog tables
        // out from under it would hand the next flush a cached id whose row no longer exists,
        // failing every subsequent flush's foreign key check for the rest of the run. Nothing in
        // production deletes these rows either; leftover catalog rows between test classes are
        // harmless since every assertion here counts signal rows within a fixed window, not
        // catalog rows.
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

        // Rollup coverage is process-run state (list-pages-server-side plan, Phase 2) -- reset it
        // between test classes the same way retention/catalog rows are left alone but signal data
        // is cleared, so a RollupWorker test in one class never leaks coverage into the next.
        await using var resetRollupState = new NpgsqlCommand(
            "UPDATE rollup_state SET coverage_start_unix_nano = NULL, rolled_until_unix_nano = 0, repassed_until_unix_nano = 0, lease_owner = NULL, lease_expires_at = 'epoch'",
            conn);
        await resetRollupState.ExecuteNonQueryAsync();
    }
}
