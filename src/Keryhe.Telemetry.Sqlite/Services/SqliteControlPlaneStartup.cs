using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Sqlite.Services;

/// <summary>
/// Both hosts: creates the SQLite control-plane database (and its directory) or migrates it forward before anything else starts, so
/// the collector and the API host can start in either order; with a fixed tenant (a fixed-tenant collector, or the API host's
/// <c>Telemetry:Api:FixedTenantId</c>) it also creates that tenant when it has no row. With <see cref="SqliteControlPlaneOptions.ApplySchema"/>
/// off it only checks and fails when the file is missing or not at this build's version. Runs in <c>StartingAsync</c>, which the host
/// calls on every hosted service before any <c>StartAsync</c> (so before Kestrel binds); a failure stops the host with a non-zero exit code.
/// </summary>
internal sealed class SqliteControlPlaneInitializer(
    SqliteControlPlaneConnection connection,
    SqliteControlPlaneOptions options,
    SqliteFixedTenant fixedTenant,
    ILogger<SqliteControlPlaneInitializer> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (options.ApplySchema)
        {
            await SqliteControlPlaneSchema.EnsureCurrentAsync(connection, logger, cancellationToken, fixedTenant.TenantId);
            return;
        }

        var check = await SqliteControlPlaneSchema.CheckAsync(connection, cancellationToken);
        if (!check.IsCurrent) logger.LogCritical("{Message}", check.Message);
        check.ThrowIfNotCurrent();
        logger.LogInformation("The SQLite control-plane database {File} is at schema version {Version} (schema changes are not applied by this host)",
            connection.DataSource, check.RecordedVersion);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The tenant <see cref="SqliteControlPlaneInitializer"/> creates when it has no row; null for none.</summary>
internal sealed record SqliteFixedTenant(long? TenantId);
