using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// Fixed-tenant <see cref="ITenantContext"/> for the integration harness. The real
/// implementation (<c>ApiTenantContext</c>) is request-scoped and set by <c>TenantMiddleware</c>;
/// tests have no request pipeline, so this is set once, after the fixture seeds its tenant row,
/// and never changes for the lifetime of the fixture.
/// </summary>
public sealed class TestTenantContext : ITenantContext
{
    private long _tenantId;

    public long GetRequiredTenantId() => _tenantId > 0
        ? _tenantId
        : throw new InvalidOperationException("TestTenantContext has no tenant id set yet.");

    public void SetTenantId(long tenantId) => _tenantId = tenantId;
}
