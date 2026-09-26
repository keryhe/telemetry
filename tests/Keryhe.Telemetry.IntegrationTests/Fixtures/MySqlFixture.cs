using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Testcontainers.MySql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class MySqlFixture : ProviderFixture
{
    private readonly MySqlContainer _container = new MySqlBuilder("mysql:8.0")
        .WithDatabase("telemetry")
        .Build();

    public override string ProviderName => ProviderNames.MySql;

    protected override Task StartContainerAsync(CancellationToken cancellationToken) => _container.StartAsync(cancellationToken);
    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();

    protected override string CollectorConnectionString => _container.GetConnectionString();
    protected override string ApiConnectionString => _container.GetConnectionString();

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "MySQL-Schema.sql"), cancellationToken);
        var statements = SplitStatements(script);

        await using var conn = new MySqlConnection(_container.GetConnectionString());
        await conn.OpenAsync(cancellationToken);
        foreach (var statement in statements)
        {
            await using var cmd = new MySqlCommand(statement, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddMySqlCollectorServices(configuration);
        services.AddMySqlApiServices(configuration);
    }

    protected override async Task<long> SeedTenantAndApiKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(_container.GetConnectionString());
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new MySqlCommand("INSERT INTO tenants (name) VALUES (@name)", conn);
        tenantCmd.Parameters.AddWithValue("@name", "phase0-tenant");
        await tenantCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn);
        var tenantId = Convert.ToInt64(await idCmd.ExecuteScalarAsync(cancellationToken));

        await using var keyCmd = new MySqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (@tenantId, @keyHash, @name)", conn);
        keyCmd.Parameters.AddWithValue("@tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("@keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("@name", "phase0-key");
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }

    public override async Task ResetAsync()
    {
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        await using var conn = new MySqlConnection(_container.GetConnectionString());
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
            "SET FOREIGN_KEY_CHECKS = 1"
        ];
        foreach (var statement in statements)
        {
            await using var cmd = new MySqlCommand(statement, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
