using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Api.Services;

public sealed class ApiTenantContext : ITenantContext
{
    private long _tenantId = ResourceModel.DefaultTenantId;

    public long GetRequiredTenantId()
    {
        if (_tenantId <= 0)
            throw new InvalidOperationException("A tenant must be selected before querying telemetry data.");
        return _tenantId;
    }

    public void SetTenantId(long tenantId)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant ID must be greater than zero.");
        _tenantId = tenantId;
    }
}
