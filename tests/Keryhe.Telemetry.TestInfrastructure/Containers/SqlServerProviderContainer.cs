using Keryhe.Telemetry.TestInfrastructure.Schema;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

public sealed class SqlServerProviderContainer : ProviderContainer
{
    private MsSqlContainer? _container;
    private string? _connectionString;

    public override string ProviderName => "SqlServer";
    public override string ImageName => "mcr.microsoft.com/mssql/server:2022-latest";
    public override string ConnectionString => _connectionString!;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        _container = new MsSqlBuilder(ImageName)
            .WithCreateParameterModifier(ApplyResourceLimits)
            .Build();
        await _container.StartAsync(cancellationToken);

        await using (var conn = new SqlConnection(_container.GetConnectionString()))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("CREATE DATABASE telemetry", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _connectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "telemetry" }.ConnectionString;
    }

    protected override async Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        // Query Store gives the slowest-SQL report; deadlocks come from the built-in system_health
        // Extended Events session, which needs no setup.
        await using var conn = new SqlConnection(_container!.GetConnectionString());
        await conn.OpenAsync(cancellationToken);
        await using (var cmd = new SqlCommand(
            "ALTER DATABASE telemetry SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL)", conn))
            await cmd.ExecuteNonQueryAsync(cancellationToken);

        await using var check = new SqlCommand(
            "SELECT actual_state_desc FROM telemetry.sys.database_query_store_options", conn);
        var state = (string?)await check.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(state, "READ_WRITE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SqlServer diagnostics: Query Store state is '{state}', expected READ_WRITE.");
    }

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var file in new[] { "SqlServer-ControlPlane.sql", "SqlServer-Telemetry.sql" })
        {
            var script = await SchemaApplier.ReadScriptAsync(file, cancellationToken);
            foreach (var batch in SchemaApplier.SplitGoBatches(script))
            {
                await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    protected override async Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new SqlCommand("INSERT INTO tenants (name) OUTPUT INSERTED.id VALUES (@name)", conn);
        tenantCmd.Parameters.AddWithValue("@name", tenantName);
        var tenantId = (long)(await tenantCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var keyCmd = new SqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt)", conn);
        keyCmd.Parameters.AddWithValue("@tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("@keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("@name", apiKeyName);
        keyCmd.Parameters.Add(new SqlParameter("@expiresAt", System.Data.SqlDbType.DateTimeOffset) { Value = expiresAtUtc is { } e ? new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)) : DBNull.Value });
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
