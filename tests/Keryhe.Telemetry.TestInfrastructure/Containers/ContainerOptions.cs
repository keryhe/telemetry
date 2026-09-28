namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// What a caller may ask of a provider container beyond the defaults. The integration tests pass
/// none of it (<see cref="Default"/>); the stress harness turns on diagnostics and resource limits.
/// </summary>
/// <param name="Diagnostics">Apply the per-provider diagnostic configuration (pg_stat_statements, Query Store, deadlock printing, ...).</param>
/// <param name="CpuLimit">CPU cap for the container (e.g. 4 = four cores); null for unlimited.</param>
/// <param name="MemoryLimitBytes">Memory cap for the container; null for unlimited.</param>
public sealed record ContainerOptions(bool Diagnostics = false, double? CpuLimit = null, long? MemoryLimitBytes = null)
{
    public static ContainerOptions Default { get; } = new();

    public const long Gigabyte = 1024L * 1024 * 1024;
}
