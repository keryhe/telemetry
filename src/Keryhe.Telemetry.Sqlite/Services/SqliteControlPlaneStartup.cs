using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Sqlite.Services;

/// <summary>
/// API host: creates the SQLite control-plane database or migrates it forward before anything else starts (the alerting and
/// retention workers read it), or, with <see cref="SqliteControlPlaneOptions.ApplySchema"/> off, only checks it. Runs in
/// <c>StartingAsync</c>, which the host calls on every hosted service before any <c>StartAsync</c>.
/// </summary>
internal sealed class SqliteControlPlaneInitializer(
    SqliteControlPlaneConnection connection,
    SqliteControlPlaneOptions options,
    ILogger<SqliteControlPlaneInitializer> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (options.ApplySchema)
        {
            await SqliteControlPlaneSchema.EnsureCurrentAsync(connection, logger, cancellationToken);
            return;
        }

        var check = await SqliteControlPlaneSchema.CheckAsync(connection, cancellationToken);
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

/// <summary>
/// Collector host: fails startup, before Kestrel binds, when the SQLite control-plane database is missing or not at exactly this
/// build's schema version. It never creates or migrates anything; the message says to start the API host. The host stops with a
/// non-zero exit code, as it does for <c>PlaintextTransportGuard</c>.
/// </summary>
internal sealed class SqliteControlPlaneStartupCheck(
    SqliteControlPlaneConnection connection,
    ILogger<SqliteControlPlaneStartupCheck> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var check = await SqliteControlPlaneSchema.CheckAsync(connection, cancellationToken);
        if (!check.IsCurrent) logger.LogCritical("{Message}", check.Message);
        check.ThrowIfNotCurrent();
        logger.LogInformation("The SQLite control-plane database {File} is at schema version {Version}", connection.DataSource, check.RecordedVersion);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
