using Keryhe.Telemetry.TestInfrastructure.Seeding;
using Docker.DotNet.Models;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// One database container for one provider: start it, create the <c>telemetry</c> database, apply
/// the real <c>schema/*.sql</c> script, and seed tenants + API keys. Shared by the integration
/// tests' provider fixtures and the stress harness.
/// </summary>
/// <summary>A database container's state as Docker reports it. <see cref="Status"/> is <c>running</c>, <c>exited</c>, <c>restarting</c> ... or <c>unknown</c>.</summary>
public sealed record ContainerOutcome(string Status, int? ExitCode, bool? OomKilled, string? Error);

public abstract class ProviderContainer : IAsyncDisposable
{
    public abstract string ProviderName { get; }

    /// <summary>The Docker image this provider's container runs, recorded in the report's run metadata.</summary>
    public abstract string ImageName { get; }

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

    /// <summary>
    /// What Docker says about the container now (<c>docker inspect</c>): whether the database is still running and, if it is not, why. A
    /// database that falls over under overload (ClickHouse at the memory limit) refuses connections for everyone, and the harness reports
    /// that as a finding about the database, with this, instead of as a failure of the run.
    /// </summary>
    public async Task<ContainerOutcome> InspectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("docker",
                ["inspect", "--format", "{{.State.Status}} {{.State.ExitCode}} {{.State.OOMKilled}}", ContainerId])
            { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            await process.WaitForExitAsync(cancellationToken);
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (process.ExitCode != 0 || parts.Length < 3)
                return new ContainerOutcome("unknown", null, null, (await process.StandardError.ReadToEndAsync(cancellationToken)).Trim());
            return new ContainerOutcome(parts[0], int.TryParse(parts[1], out var code) ? code : null, bool.TryParse(parts[2], out var oom) ? oom : null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ContainerOutcome("unknown", null, null, ex.Message);
        }
    }

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

    /// <summary>Inserts one tenant and one API key (whose plaintext is given, optionally expiring at a UTC instant) and returns the tenant.</summary>
    public async Task<SeededTenant> SeedTenantAsync(string tenantName, string apiKeyName, string apiKeyPlainText, CancellationToken cancellationToken = default, DateTime? expiresAtUtc = null)
    {
        var id = await InsertTenantAndApiKeyAsync(tenantName, apiKeyName, ApiKeyHasher.Hash(apiKeyPlainText), expiresAtUtc, cancellationToken);
        return new SeededTenant(id, tenantName, apiKeyName, apiKeyPlainText);
    }

    /// <summary>
    /// Runs extra SQL against the <c>telemetry</c> database after the schema, statement by statement (split as the schema
    /// scripts are). For experiments that change the schema under test, e.g. dropping views; not every provider supports it.
    /// </summary>
    public virtual Task ExecuteSqlAsync(string script, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{ProviderName} does not support running extra SQL.");

    /// <summary>Starts the container (and creates the <c>telemetry</c> database) with <see cref="Options"/> applied.</summary>
    protected abstract Task StartContainerAsync(CancellationToken cancellationToken);

    /// <summary>Applies the raw schema script for this provider, exactly as <c>apply-schema.sh</c> would.</summary>
    protected abstract Task ApplySchemaAsync(CancellationToken cancellationToken);

    /// <summary>Runs the post-start half of the diagnostic configuration and verifies the rest is in effect. Only called when <see cref="ContainerOptions.Diagnostics"/> is set.</summary>
    protected abstract Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken);

    protected abstract Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken);

    public abstract ValueTask DisposeAsync();

    /// <summary>Applies <see cref="ContainerOptions.CpuLimit"/>/<see cref="ContainerOptions.MemoryLimitBytes"/> through the create-parameter modifier.</summary>
    protected void ApplyResourceLimits(CreateContainerParameters parameters)
    {
        parameters.HostConfig ??= new HostConfig();
        if (Options.CpuLimit is { } cpus)
            parameters.HostConfig.NanoCPUs = (long)(cpus * 1_000_000_000);
        if (Options.CpusetCpus is { Length: > 0 } cpuset)
            parameters.HostConfig.CpusetCpus = cpuset;
        if (Options.MemoryLimitBytes is { } memory)
        {
            parameters.HostConfig.Memory = memory;
            // Equal to Memory disables swap, so the cap is a real cap.
            parameters.HostConfig.MemorySwap = memory;
        }
    }
}
