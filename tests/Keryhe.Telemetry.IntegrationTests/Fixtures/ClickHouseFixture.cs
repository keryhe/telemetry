using ClickHouse.Client.ADO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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
    private const int HttpPort = 8123;

    private readonly IContainer _container = new ContainerBuilder("clickhouse/clickhouse-server:24.8")
        .WithPortBinding(HttpPort, true)
        .WithEnvironment("CLICKHOUSE_SKIP_USER_SETUP", "1")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/ping")))
        .Build();

    private string? _connectionString;

    public override string ProviderName => ProviderNames.ClickHouse;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await _container.StartAsync(cancellationToken);
        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(HttpPort);

        await using (var admin = new ClickHouseConnection($"Host={host};Port={port};Username=default;Database=default"))
        {
            await admin.OpenAsync(cancellationToken);
            var cmd = admin.CreateCommand();
            cmd.CommandText = "CREATE DATABASE IF NOT EXISTS telemetry";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _connectionString = $"Host={host};Port={port};Username=default;Database=telemetry";
    }

    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();

    protected override string CollectorConnectionString => _connectionString!;
    protected override string ApiConnectionString => _connectionString!;

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "ClickHouse-Schema.sql"), cancellationToken);
        var statements = SplitStatements(script);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var statement in statements)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddClickHouseCollectorServices(configuration);
        services.AddClickHouseApiServices(configuration);
    }

    protected override async Task<long> SeedTenantAndApiKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        const long tenantId = 1;

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantCmd = conn.CreateCommand();
        tenantCmd.CommandText = $"INSERT INTO tenants (id, name) VALUES ({tenantId}, 'phase0-tenant')";
        await tenantCmd.ExecuteNonQueryAsync(cancellationToken);

        var keyCmd = conn.CreateCommand();
        keyCmd.CommandText = $"INSERT INTO api_keys (id, tenant_id, key_hash, name) VALUES (1, {tenantId}, '{keyHash}', 'phase0-key')";
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }

    public override async Task ResetAsync()
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync();
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        string[] tables =
        [
            "log_records", "spans", "metrics", "gauge_data_points", "sum_data_points",
            "histogram_data_points", "exponential_histogram_data_points", "summary_data_points"
        ];
        foreach (var table in tables)
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"TRUNCATE TABLE {table}";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
