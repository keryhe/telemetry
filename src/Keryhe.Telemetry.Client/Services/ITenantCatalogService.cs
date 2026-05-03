using Keryhe.Telemetry.Data.Access;
using Microsoft.EntityFrameworkCore;

namespace Keryhe.Telemetry.Client.Services;

public interface ITenantCatalogService
{
    Task<IReadOnlyList<TenantOption>> GetTenantsAsync(CancellationToken cancellationToken = default);
}

public sealed record TenantOption(long Id, string Name);

public sealed class TenantCatalogService : ITenantCatalogService
{
    private readonly IDbContextFactory<TelemetryReadDbContext> _contextFactory;

    public TenantCatalogService(IDbContextFactory<TelemetryReadDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<IReadOnlyList<TenantOption>> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Tenants
            .OrderBy(tenant => tenant.Name)
            .Select(tenant => new TenantOption(tenant.Id, tenant.Name))
            .ToListAsync(cancellationToken);
    }
}