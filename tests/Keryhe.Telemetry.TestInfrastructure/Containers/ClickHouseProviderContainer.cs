using ClickHouse.Client.ADO;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Keryhe.Telemetry.TestInfrastructure.Schema;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// No first-party Testcontainers module exists for ClickHouse, so this builds the container
/// directly against the official image, waiting on the HTTP interface (port 8123) that
/// <c>ClickHouse.Client</c> (and this provider) talks to.
/// </summary>
public sealed class ClickHouseProviderContainer : ProviderContainer
{
    private const int HttpPort = 8123;

    private IContainer? _container;
    private string? _connectionString;

    // ClickHouse has no auto-increment: the schema takes explicit ids, so the seeder hands them out.
    private long _nextId;

    public override string ProviderName => "ClickHouse";
    public override string ConnectionString => _connectionString!;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        _container = new ContainerBuilder("clickhouse/clickhouse-server:24.8")
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

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await SchemaApplier.ReadScriptAsync("ClickHouse-Schema.sql", cancellationToken);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var statement in SchemaApplier.SplitStatements(script))
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override async Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);

        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var tenantCmd = conn.CreateCommand();
        tenantCmd.CommandText = $"INSERT INTO tenants (id, name) VALUES ({id}, '{Escape(tenantName)}')";
        await tenantCmd.ExecuteNonQueryAsync(cancellationToken);

        var keyCmd = conn.CreateCommand();
        keyCmd.CommandText = $"INSERT INTO api_keys (id, tenant_id, key_hash, name) VALUES ({id}, {id}, '{keyHash}', '{Escape(apiKeyName)}')";
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return id;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
