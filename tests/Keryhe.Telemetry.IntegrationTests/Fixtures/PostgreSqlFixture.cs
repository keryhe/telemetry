using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class PostgreSqlFixture : ProviderFixture
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("telemetry")
        .WithUsername("telemetry")
        .WithPassword("telemetry")
        .Build();

    public override string ProviderName => ProviderNames.PostgreSql;

    protected override Task StartContainerAsync(CancellationToken cancellationToken) => _container.StartAsync(cancellationToken);
    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();

    protected override string CollectorConnectionString => _container.GetConnectionString();
    protected override string ApiConnectionString => _container.GetConnectionString();

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "PostgreSQL-Schema.sql"), cancellationToken);
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(script, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPostgreSqlCollectorServices(configuration);
        services.AddPostgreSqlApiServices(configuration);
    }

    protected override async Task<long> SeedTenantAndApiKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new NpgsqlCommand(
            "INSERT INTO tenants (name) VALUES (@name) RETURNING id", conn);
        tenantCmd.Parameters.AddWithValue("name", "phase0-tenant");
        var tenantId = (long)(await tenantCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var keyCmd = new NpgsqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (@tenantId, @keyHash, @name)", conn);
        keyCmd.Parameters.AddWithValue("tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("name", "phase0-key");
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
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
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
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
