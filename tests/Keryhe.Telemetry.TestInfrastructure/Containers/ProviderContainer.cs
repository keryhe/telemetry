using Keryhe.Telemetry.TestInfrastructure.Seeding;
using Docker.DotNet.Models;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// One database container for one provider: start it, create the <c>telemetry</c> database, apply
/// the real <c>schema/*.sql</c> script, and seed tenants + API keys. Shared by the integration
/// tests' provider fixtures and the stress harness.
/// </summary>
public abstract class ProviderContainer : IAsyncDisposable
{
    public abstract string ProviderName { get; }

    /// <summary>Connection string for the <c>telemetry</c> database. Valid once <see cref="StartAsync"/> has completed.</summary>
    public abstract string ConnectionString { get; }

    /// <summary>Docker id of the database container. Valid once <see cref="StartAsync"/> has completed.</summary>
    public abstract string ContainerId { get; }

    /// <summary>The container's stdout and stderr since <paramref name="since"/>, concatenated (deadlock reports and lock-wait log lines live here).</summary>
    public async Task<string> GetLogsAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var (stdout, stderr) = await ReadLogsAsync(since, cancellationToken);
        return stdout + Environment.NewLine + stderr;
    }

    protected abstract Task<(string Stdout, string Stderr)> ReadLogsAsync(DateTime since, CancellationToken cancellationToken);

    public ContainerOptions Options { get; private set; } = ContainerOptions.Default;

    /// <summary>Starts the container, creates the database, applies diagnostics (if requested) and the schema.</summary>
    public async Task StartAsync(ContainerOptions? options = null, CancellationToken cancellationToken = default)
    {
        Options = options ?? ContainerOptions.Default;
        await StartContainerAsync(cancellationToken);
        if (Options.Diagnostics)
            await ConfigureDiagnosticsAsync(cancellationToken);
        await ApplySchemaAsync(cancellationToken);
    }

    /// <summary>Inserts one tenant and one API key (whose plaintext is given) and returns the tenant.</summary>
    public async Task<SeededTenant> SeedTenantAsync(string tenantName, string apiKeyName, string apiKeyPlainText, CancellationToken cancellationToken = default)
    {
        var id = await InsertTenantAndApiKeyAsync(tenantName, apiKeyName, ApiKeyHasher.Hash(apiKeyPlainText), cancellationToken);
        return new SeededTenant(id, tenantName, apiKeyName, apiKeyPlainText);
    }

    /// <summary>Starts the container (and creates the <c>telemetry</c> database) with <see cref="Options"/> applied.</summary>
    protected abstract Task StartContainerAsync(CancellationToken cancellationToken);

    /// <summary>Applies the raw schema script for this provider, exactly as <c>apply-schema.sh</c> would.</summary>
    protected abstract Task ApplySchemaAsync(CancellationToken cancellationToken);

    /// <summary>Runs the post-start half of the diagnostic configuration and verifies the rest is in effect. Only called when <see cref="ContainerOptions.Diagnostics"/> is set.</summary>
    protected abstract Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken);

    protected abstract Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    /// <summary>Applies <see cref="ContainerOptions.CpuLimit"/>/<see cref="ContainerOptions.MemoryLimitBytes"/> through the create-parameter modifier.</summary>
    protected void ApplyResourceLimits(CreateContainerParameters parameters)
    {
        parameters.HostConfig ??= new HostConfig();
        if (Options.CpuLimit is { } cpus)
            parameters.HostConfig.NanoCPUs = (long)(cpus * 1_000_000_000);
        if (Options.MemoryLimitBytes is { } memory)
        {
            parameters.HostConfig.Memory = memory;
            // Equal to Memory disables swap, so the cap is a real cap.
            parameters.HostConfig.MemorySwap = memory;
        }
    }
}
