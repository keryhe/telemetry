using Keryhe.Telemetry.TestInfrastructure.Schema;
using MySqlConnector;
using Testcontainers.MySql;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

public sealed class MySqlProviderContainer : ProviderContainer
{
    private MySqlContainer? _container;

    public override string ProviderName => "MySql";
    public override string ImageName => "mysql:8.0";
    public override string ConnectionString => _container!.GetConnectionString();

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        var builder = new MySqlBuilder(ImageName)
            .WithDatabase("telemetry")
            .WithCreateParameterModifier(ApplyResourceLimits);

        // The image's entrypoint forwards these to mysqld. Deadlock details land in the container
        // error log; performance_schema digests and data_lock_waits are on by default in 8.x.
        // The module's default unprivileged user cannot read performance_schema.data_lock_waits /
        // the digest tables, which the stress harness's observers query, so diagnostics run as root.
        if (Options.Diagnostics)
            builder = builder.WithUsername("root").WithCommand("--innodb_print_all_deadlocks=ON");

        _container = builder.Build();
        await _container.StartAsync(cancellationToken);
    }

    protected override async Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);

        foreach (var (variable, expected) in new[] { ("innodb_print_all_deadlocks", "1"), ("performance_schema", "1") })
        {
            await using var cmd = new MySqlCommand($"SELECT @@GLOBAL.{variable}", conn);
            var actual = Convert.ToString(await cmd.ExecuteScalarAsync(cancellationToken));
            if (actual != expected)
                throw new InvalidOperationException($"MySql diagnostics: {variable} is '{actual}', expected '{expected}'.");
        }

        await using var locks = new MySqlCommand("SELECT COUNT(*) FROM performance_schema.data_lock_waits", conn);
        await locks.ExecuteScalarAsync(cancellationToken); // throws if the table is not readable
    }

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await SchemaApplier.ReadScriptAsync("MySQL-Schema.sql", cancellationToken);

        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var statement in SchemaApplier.SplitStatements(script))
        {
            await using var cmd = new MySqlCommand(statement, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override async Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new MySqlCommand("INSERT INTO tenants (name) VALUES (@name)", conn);
        tenantCmd.Parameters.AddWithValue("@name", tenantName);
        await tenantCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var idCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn);
        var tenantId = Convert.ToInt64(await idCmd.ExecuteScalarAsync(cancellationToken));

        await using var keyCmd = new MySqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt)", conn);
        keyCmd.Parameters.AddWithValue("@tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("@keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("@name", apiKeyName);
        // DATETIME(6) holds UTC by convention.
        keyCmd.Parameters.AddWithValue("@expiresAt", expiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Unspecified) : DBNull.Value);
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }

    public override string ContainerId => _container!.Id;

    protected override Task<(string Stdout, string Stderr)> ReadLogsAsync(DateTime since, CancellationToken cancellationToken) =>
        _container!.GetLogsAsync(since, timestampsEnabled: false, ct: cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
