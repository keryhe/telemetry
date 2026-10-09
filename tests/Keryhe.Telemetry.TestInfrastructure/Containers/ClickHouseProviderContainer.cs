using ClickHouse.Client.ADO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keryhe.Telemetry.TestInfrastructure.Schema;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// No first-party Testcontainers module exists for ClickHouse, so this builds the container
/// directly against the official image, waiting on the HTTP interface (port 8123) that
/// <c>ClickHouse.Client</c> (and this provider) talks to.
///
/// ClickHouse holds telemetry only. Its control plane (tenants, API keys, alert rules, retention
/// settings) runs on a PostgreSQL container composed here, started and disposed with it; the tenants
/// and keys are seeded there. The <see cref="ContainerOptions"/> diagnostics and limits apply to the
/// ClickHouse container only.
/// </summary>
public sealed class ClickHouseProviderContainer : ProviderContainer
{
    private const int HttpPort = 8123;

    private IContainer? _container;
    private string? _connectionString;
    private readonly PostgreSqlProviderContainer _controlPlane = new(controlPlaneOnly: true);

    public override string ProviderName => "ClickHouse";
    public override string ImageName => "clickhouse/clickhouse-server:25.8";
    public override string ConnectionString => _connectionString!;
    public override string ControlPlaneConnectionString => _controlPlane.ConnectionString;
    public override string ControlPlaneProviderName => _controlPlane.ProviderName;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await _controlPlane.StartAsync(ContainerOptions.Default, cancellationToken);

        _container = new ContainerBuilder(ImageName)
            .WithPortBinding(HttpPort, true)
            .WithEnvironment("CLICKHOUSE_SKIP_USER_SETUP", "1")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(HttpPort).ForPath("/ping")))
            .WithCreateParameterModifier(ApplyResourceLimits)
            .Build();
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

    protected override async Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // system.query_log / part_log / processes are on by default; verify rather than assume.
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var setting = conn.CreateCommand();
        setting.CommandText = "SELECT value FROM system.settings WHERE name = 'log_queries'";
        var logQueries = Convert.ToString(await setting.ExecuteScalarAsync(cancellationToken));
        if (logQueries != "1")
            throw new InvalidOperationException($"ClickHouse diagnostics: log_queries is '{logQueries}', expected '1'.");

        foreach (var table in new[] { "query_log", "part_log" })
        {
            var exists = conn.CreateCommand();
            exists.CommandText = $"SELECT count() FROM system.tables WHERE database = 'system' AND name = '{table}'";
            // The system log tables are created lazily on first write, so a missing one is only an
            // error if the server config disables it; query_log is written by our own statements.
            var count = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken));
            if (table == "query_log" && count == 0)
            {
                var flush = conn.CreateCommand();
                flush.CommandText = "SYSTEM FLUSH LOGS";
                await flush.ExecuteNonQueryAsync(cancellationToken);
                count = Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken));
                if (count == 0)
                    throw new InvalidOperationException("ClickHouse diagnostics: system.query_log does not exist after SYSTEM FLUSH LOGS.");
            }
        }
    }

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken) =>
        await ExecuteSqlAsync(await SchemaApplier.ReadScriptAsync("ClickHouse-Telemetry.sql", cancellationToken), cancellationToken);

    public override async Task ExecuteSqlAsync(string script, CancellationToken cancellationToken = default)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var statement in SchemaApplier.SplitStatements(script))
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override async Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        // The control plane's own container seeds the tenant and key.
        return await _controlPlane.InsertTenantWithHashedKeyAsync(tenantName, apiKeyName, keyHash, expiresAtUtc, cancellationToken);
    }

    public override string ContainerId => _container!.Id;

    protected override Task<(string Stdout, string Stderr)> ReadLogsAsync(DateTime since, CancellationToken cancellationToken) =>
        _container!.GetLogsAsync(since, timestampsEnabled: false, ct: cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        await _controlPlane.DisposeAsync();
    }
}
