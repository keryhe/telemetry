using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>The TimescaleDB image is Postgres-compatible (same entrypoint/env vars/port), so the plain Postgres Testcontainers module works unchanged with the Timescale image swapped in.</summary>
public sealed class TimescaleFixture : ProviderFixture
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("timescale/timescaledb:latest-pg16")
        .WithDatabase("telemetry")
        .WithUsername("telemetry")
        .WithPassword("telemetry")
        .Build();

    public override string ProviderName => ProviderNames.Timescale;

    protected override Task StartContainerAsync(CancellationToken cancellationToken) => _container.StartAsync(cancellationToken);
    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();

    protected override string CollectorConnectionString => _container.GetConnectionString();
    protected override string ApiConnectionString => _container.GetConnectionString();

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "Timescale-Schema.sql"), cancellationToken);
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(script, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddTimescaleCollectorServices(configuration);
        services.AddTimescaleApiServices(configuration);
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
        // See PostgreSqlFixture.ResetAsync's comment: resources/instrumentation_scopes are
        // deliberately left alone because ResourceScopeCache is a process-lifetime singleton here
        // and is never invalidated.
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            TRUNCATE TABLE log_records, spans, metrics,
                gauge_data_points, sum_data_points, histogram_data_points,
                exponential_histogram_data_points, summary_data_points
            RESTART IDENTITY CASCADE
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
