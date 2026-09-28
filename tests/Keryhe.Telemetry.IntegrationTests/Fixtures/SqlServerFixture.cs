using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

public sealed class SqlServerFixture : ProviderFixture
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    private string? _connectionString;

    public override string ProviderName => ProviderNames.SqlServer;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await _container.StartAsync(cancellationToken);

        var master = new SqlConnectionStringBuilder(_container.GetConnectionString());
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("CREATE DATABASE telemetry", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "telemetry" };
        _connectionString = builder.ConnectionString;
    }

    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();

    protected override string CollectorConnectionString => _connectionString!;
    protected override string ApiConnectionString => _connectionString!;

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Schema", "SqlServer-Schema.sql"), cancellationToken);
        var batches = SplitGoBatches(script);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var batch in batches)
        {
            // ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION (decision 35) is not part of phase 0's
            // schema; nothing in this script needs to run outside a transaction the way that
            // statement would in a later phase.
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSqlServerCollectorServices(configuration);
        services.AddSqlServerApiServices(configuration);
    }

    protected override async Task<long> SeedTenantAndApiKeyAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new SqlCommand(
            "INSERT INTO tenants (name) OUTPUT INSERTED.id VALUES (@name)", conn);
        tenantCmd.Parameters.AddWithValue("@name", "phase0-tenant");
        var tenantId = (long)(await tenantCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var keyCmd = new SqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (@tenantId, @keyHash, @name)", conn);
        keyCmd.Parameters.AddWithValue("@tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("@keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("@name", "phase0-key");
        await keyCmd.ExecuteNonQueryAsync(cancellationToken);

        return tenantId;
    }

    public override async Task ResetAsync()
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        // No CASCADE-capable multi-table TRUNCATE on SQL Server: delete children before parents.
        // resources/instrumentation_scopes are deliberately left alone -- see PostgreSqlFixture.
        // ResetAsync's comment on ResourceScopeCache.
        const string sql = """
            DELETE FROM spans;
            DELETE FROM gauge_data_points;
            DELETE FROM sum_data_points;
            DELETE FROM histogram_data_points;
            DELETE FROM exponential_histogram_data_points;
            DELETE FROM summary_data_points;
            DELETE FROM metrics;
            DELETE FROM metric_last_seen;
            DELETE FROM log_records;
            DELETE FROM log_rollup_minute;
            DELETE FROM log_rollup_hour;
            DELETE FROM orphan_roots;
            DELETE FROM trace_rollup_minute;
            DELETE FROM trace_rollup_hour;
            UPDATE rollup_state SET coverage_start_unix_nano = NULL, rolled_until_unix_nano = 0, repassed_until_unix_nano = 0, lease_owner = NULL, lease_expires_at = '1970-01-01T00:00:00';
            """;
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync();
    }
}
