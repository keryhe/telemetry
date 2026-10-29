using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Sinks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

/// <summary>
/// <c>Generator:FixedTenant</c>, for a collector that stores every export under one tenant: no API key is required or sent, and only the
/// first configured tenant is simulated.
/// </summary>
public class FixedTenantTests
{
    private static GeneratorOptions WithTenants(bool fixedTenant, params TenantOptions[] tenants) =>
        new() { FixedTenant = fixedTenant, Tenants = [.. tenants] };

    [Fact]
    public void A_missing_api_key_is_accepted_only_in_fixed_tenant_mode()
    {
        var keyless = new TenantOptions { Name = "acme-retail" };

        WithTenants(fixedTenant: true, keyless).Validate();
        var ex = Assert.Throws<InvalidOperationException>(() => WithTenants(fixedTenant: false, keyless).Validate());
        Assert.Contains("Generator:Tenants:0:ApiKey", ex.Message);
    }

    [Fact]
    public void Fixed_tenant_mode_simulates_the_first_tenant_only()
    {
        TenantOptions[] tenants = [new() { Name = "a", ApiKey = "k" }, new() { Name = "b", ApiKey = "k" }, new() { Name = "c", ApiKey = "k" }];

        Assert.Equal(["a"], WithTenants(fixedTenant: true, tenants).ActiveTenants.Select(t => t.Name));
        Assert.Equal(["a", "b", "c"], WithTenants(fixedTenant: false, tenants).ActiveTenants.Select(t => t.Name));
    }

    [Fact]
    public async Task Both_sinks_send_no_authorization_header_without_a_key()
    {
        await using var backfill = await FakeCollector.StartAsync();
        await using var live = await FakeCollector.StartAsync();
        // Its own tenant name: SDK providers listen by source name, which is per pod and tenant, so sharing the parity tests' tenant
        // would deliver each test's spans to the other's collector while both run.
        var options = TestSupport.Options();
        options.Tenants[0].Name = "keyless-retail";
        var from = TestSupport.Day.AddHours(20);
        var chunk = TestSupport.Simulator(options).Simulate(from, from.AddMinutes(1), includeSamples: true);

        await using (var otlp = new OtlpSink("keyless-retail", apiKey: null, backfill.Endpoint, 2000, 7, NullLogger.Instance))
            await otlp.WriteAsync(chunk, CancellationToken.None);
        // Disposing the SDK sink flushes every provider, which is what delivers the metrics.
        await using (var sdk = new SdkSink(live.Endpoint, apiKey: null, metricIntervalSeconds: 3600))
            await sdk.WriteAsync(chunk, CancellationToken.None);

        foreach (var collector in new[] { backfill, live })
        {
            Assert.NotEmpty(collector.Traces);
            Assert.NotEmpty(collector.Logs);
            Assert.NotEmpty(collector.Metrics);
            Assert.All(collector.Traces, t => Assert.Equal("", t.Auth));
            Assert.All(collector.Logs, t => Assert.Equal("", t.Auth));
            Assert.All(collector.Metrics, t => Assert.Equal("", t.Auth));
        }
    }
}
