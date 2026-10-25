using Dapper;
using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>
/// <see cref="CollectorAuthenticationMode.FixedTenant"/> mode only, in <see cref="StartingAsync"/> (before Kestrel binds): refuses to start
/// outside Development without <see cref="CollectorAuthenticationOptions.AllowUnauthenticatedIngestion"/>, and refuses to start when the
/// fixed tenant has no <c>tenants</c> row in the control plane, since its telemetry would never appear under a tenant in the UI. On a
/// SQLite control plane the row is created (as <c>Default</c>) by the SQLite schema step, which is ordered ahead of this check; the
/// relational control planes need it created with the Admin tool. A control-plane error is retried for up to <see cref="RetryWindow"/> (an on-site service can start before its database), except on
/// SQLite, a local file that is either there or not. The tenant is not re-checked after startup.
/// </summary>
internal sealed class FixedTenantStartupCheck(
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<TelemetryCollectorOptions> options,
    ILogger<FixedTenantStartupCheck> logger) : IHostedLifecycleService
{
    internal TimeSpan RetryWindow { get; init; } = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var auth = options.Value.Authentication;
        if (!environment.IsDevelopment() && !auth.AllowUnauthenticatedIngestion)
            throw new InvalidOperationException(
                $"{CollectorAuthenticationOptions.SectionName}:Mode is FixedTenant, which accepts exports from anyone who can reach the collector. " +
                $"Set {CollectorAuthenticationOptions.SectionName}:AllowUnauthenticatedIngestion=true to run it outside Development.");

        logger.LogWarning(
            "Collector authentication is FixedTenant: every export is stored under tenant {TenantId} with no API key, so anyone who can reach the OTLP endpoints can write telemetry",
            auth.FixedTenantId);

        var provider = configuration["ControlPlane:Provider"] ?? "";
        var retry = !provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
        var deadline = DateTime.UtcNow + RetryWindow;
        while (true)
        {
            try
            {
                if (await TenantExistsAsync(auth.FixedTenantId, cancellationToken))
                    return;
                throw new InvalidOperationException(
                    $"{CollectorAuthenticationOptions.SectionName}:FixedTenantId is {auth.FixedTenantId}, but the {provider} control plane has no tenant with that id. " +
                    "Create the tenant first (the Admin tool's \"Create tenant\"), then start the collector.");
            }
            catch (Exception ex) when (ex is not InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                if (!retry || DateTime.UtcNow + RetryDelay > deadline)
                    throw new InvalidOperationException(
                        $"Could not check that tenant {auth.FixedTenantId} exists in the {provider} control plane: {ex.Message}", ex);
                logger.LogWarning(ex, "Could not reach the control plane to check tenant {TenantId}; retrying", auth.FixedTenantId);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    private async Task<bool> TenantExistsAsync(long tenantId, CancellationToken ct)
    {
        await using var connection = ControlPlaneDbConnection.TryCreate(configuration)
            ?? throw new InvalidOperationException("ControlPlane:Provider and ConnectionStrings:ControlPlane are required.");
        await connection.OpenAsync(ct);
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM tenants WHERE id = @id", new { id = tenantId }, cancellationToken: ct));
        return count > 0;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
