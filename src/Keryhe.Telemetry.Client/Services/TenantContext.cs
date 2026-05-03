using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Client.Services;

public sealed class TenantContext : ITenantContext
{
    private long _tenantId;
    public event Action? OnChange;

    public TenantContext(IConfiguration configuration)
    {
        var configuredTenantId = configuration.GetValue<long?>("TenantContext:DefaultTenantId");
        _tenantId = configuredTenantId.HasValue && configuredTenantId.Value > 0
            ? configuredTenantId.Value
            : ResourceModel.DefaultTenantId;
    }

            public long CurrentTenantId => GetRequiredTenantId();

    public long GetRequiredTenantId()
    {
        if (_tenantId <= 0)
        {
            throw new InvalidOperationException("A tenant must be selected before querying telemetry data.");
        }

        return _tenantId;
    }

    public void SetTenantId(long tenantId)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant ID must be greater than zero.");
        }

        if (_tenantId == tenantId)
        {
            return;
        }

        _tenantId = tenantId;
        OnChange?.Invoke();
    }
}