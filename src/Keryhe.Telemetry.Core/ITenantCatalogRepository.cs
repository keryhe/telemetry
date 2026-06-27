namespace Keryhe.Telemetry.Core;

public interface ITenantCatalogRepository
{
    Task<List<TenantInfo>> GetAllTenantsAsync(CancellationToken cancellationToken = default);
}

public sealed record TenantInfo(long Id, string Name);
