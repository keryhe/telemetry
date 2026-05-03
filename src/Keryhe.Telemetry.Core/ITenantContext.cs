namespace Keryhe.Telemetry.Core;

public interface ITenantContext
{
    long GetRequiredTenantId();
    void SetTenantId(long tenantId);
}