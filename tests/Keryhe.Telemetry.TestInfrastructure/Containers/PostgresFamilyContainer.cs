using Npgsql;
using Testcontainers.PostgreSql;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// Plain Postgres and TimescaleDB differ only in image, schema script and preloaded libraries (the
/// Timescale image is Postgres-compatible: same entrypoint, env vars and port), so the plain
/// Postgres Testcontainers module serves both.
/// </summary>
public abstract class PostgresFamilyContainer : ProviderContainer
{
    private PostgreSqlContainer? _container;

    protected abstract string Image { get; }

    public override string ImageName => Image;
    protected abstract string SchemaFileName { get; }

    /// <summary>Value of <c>shared_preload_libraries</c> when diagnostics are on. Timescale must keep <c>timescaledb</c> first.</summary>
    protected abstract string DiagnosticPreloadLibraries { get; }

    public override string ConnectionString => _container!.GetConnectionString();

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        var builder = new PostgreSqlBuilder(Image)
            .WithDatabase("telemetry")
            .WithUsername("telemetry")
            .WithPassword("telemetry")
            .WithCreateParameterModifier(ApplyResourceLimits);

        if (Options.Diagnostics)
        {
            // Passed as server flags (the image's entrypoint forwards them to postgres), which
            // override postgresql.conf — including the Timescale image's own preload setting.
            // track=all counts statements run inside other statements too: on plain Postgres the
            // foreign-key checks a COPY fires (SELECT ... FOR KEY SHARE) are only visible that way.
            builder = builder.WithCommand(
                "-c", $"shared_preload_libraries={DiagnosticPreloadLibraries}",
                "-c", "pg_stat_statements.track=all",
                "-c", "log_lock_waits=on",
                "-c", "deadlock_timeout=1s",
                "-c", "track_io_timing=on");
        }

        _container = builder.Build();
        await _container.StartAsync(cancellationToken);
    }

    protected override async Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using (var create = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_stat_statements", conn))
            await create.ExecuteNonQueryAsync(cancellationToken);

        // Fail loudly if a flag did not take effect, so a stress run never silently loses a signal.
        foreach (var (setting, expected) in new[] { ("log_lock_waits", "on"), ("deadlock_timeout", "1s"), ("track_io_timing", "on"), ("pg_stat_statements.track", "all") })
        {
            await using var show = new NpgsqlCommand($"SHOW {setting}", conn);
            var actual = (string?)await show.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{ProviderName} diagnostics: {setting} is '{actual}', expected '{expected}'.");
        }

        await using var preload = new NpgsqlCommand("SHOW shared_preload_libraries", conn);
        var libraries = (string?)await preload.ExecuteScalarAsync(cancellationToken);
        if (libraries is null || !libraries.Contains("pg_stat_statements"))
            throw new InvalidOperationException($"{ProviderName} diagnostics: shared_preload_libraries is '{libraries}', pg_stat_statements is missing.");
    }

    protected override async Task ApplySchemaAsync(CancellationToken cancellationToken)
    {
        var script = await Schema.SchemaApplier.ReadScriptAsync(SchemaFileName, cancellationToken);
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(script, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    protected override async Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var tenantCmd = new NpgsqlCommand("INSERT INTO tenants (name) VALUES (@name) RETURNING id", conn);
        tenantCmd.Parameters.AddWithValue("name", tenantName);
        var tenantId = (long)(await tenantCmd.ExecuteScalarAsync(cancellationToken))!;

        await using var keyCmd = new NpgsqlCommand(
            "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt)", conn);
        keyCmd.Parameters.AddWithValue("tenantId", tenantId);
        keyCmd.Parameters.AddWithValue("keyHash", keyHash);
        keyCmd.Parameters.AddWithValue("name", apiKeyName);
        keyCmd.Parameters.Add(new NpgsqlParameter("expiresAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = expiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : DBNull.Value });
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
