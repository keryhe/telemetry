using Docker.DotNet;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Keryhe.Telemetry.TestInfrastructure.Seeding;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Covers the stress-test-plan Phase 0 additions that the provider fixtures don't exercise: N-tenant
/// seeding, the per-provider diagnostic configuration (whose own verification throws on a missed
/// setting) and container CPU/memory limits. Each case starts its own short-lived container.
/// </summary>
public class TestInfrastructureTests
{
    public static IEnumerable<object[]> Providers() =>
    [
        [ProviderNames.PostgreSql],
        [ProviderNames.Timescale],
        [ProviderNames.SqlServer],
        [ProviderNames.MySql],
        [ProviderNames.ClickHouse]
    ];

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Diagnostics_limits_and_multi_tenant_seeding_work(string provider)
    {
        await using var container = ProviderContainerFactory.Create(provider);
        var options = new ContainerOptions(Diagnostics: true, CpuLimit: 4, MemoryLimitBytes: 8 * ContainerOptions.Gigabyte);

        // Throws if any diagnostic setting did not take effect.
        await container.StartAsync(options);

        var tenants = await TenantSeeder.SeedAsync(container, 3);

        Assert.Equal(3, tenants.Count);
        Assert.Equal(3, tenants.Select(t => t.Id).Distinct().Count());
        Assert.Equal(3, tenants.Select(t => t.ApiKey).Distinct().Count());
        Assert.All(tenants, t => Assert.False(string.IsNullOrEmpty(t.ApiKey)));
    }
}
