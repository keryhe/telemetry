using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The control-plane repositories (alert rules, the tenant catalog, retention settings, the API-key touch) against the
/// real <c>ControlPlane</c> schema script, once per control-plane provider. ClickHouse's subclass runs them against the
/// PostgreSQL container it composes as its control plane: with no unkeyed <c>NpgsqlDataSource</c> registered there, a
/// control-plane class that resolved the telemetry pool would fail to build, so passing proves each one takes the
/// keyed control-plane source.
/// </summary>
public abstract class ControlPlaneTestsBase(ProviderFixture fixture)
{
    protected ProviderFixture Fixture { get; } = fixture;

    private IServiceScope Scope() => Fixture.Services.CreateScope();

    private AlertRule NewRule(string name) => new()
    {
        TenantId = Fixture.TenantId,
        Name = name,
        Type = AlertRuleType.ErrorRate,
        ServiceName = "checkout-api",
        ConditionJson = "{\"threshold\":5,\"windowMinutes\":5}",
        WebhookUrl = "https://example.test/hook",
        CooldownMinutes = 60,
        Enabled = true
    };

    [Fact]
    public async Task Alert_rules_round_trip_through_create_update_and_delete()
    {
        using var scope = Scope();
        var rules = scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>();
        var name = $"rule-{Guid.NewGuid():N}";

        var created = await rules.CreateRuleAsync(NewRule(name));
        Assert.True(created.Id > 0);
        try
        {
            Assert.Contains(await rules.GetAllRulesAsync(), r => r.Id == created.Id && r.Name == name && r.Enabled);
            Assert.Contains(await rules.GetEnabledTenantIdsAsync(), id => id == Fixture.TenantId);
            Assert.Contains(await rules.GetEnabledRulesAsync(Fixture.TenantId), r => r.Id == created.Id);

            created.Enabled = false;
            created.Name = name + "-edited";
            await rules.UpdateRuleAsync(created);
            Assert.DoesNotContain(await rules.GetEnabledRulesAsync(Fixture.TenantId), r => r.Id == created.Id);
            Assert.Contains(await rules.GetAllRulesAsync(), r => r.Id == created.Id && r.Name == name + "-edited" && !r.Enabled);
        }
        finally
        {
            await rules.DeleteRuleAsync(created.Id);
        }
        Assert.DoesNotContain(await rules.GetAllRulesAsync(), r => r.Id == created.Id);
    }

    /// <summary>Several evaluators claiming one rule at once: exactly one wins the cooldown window (ClickHouse could not do this).</summary>
    [Fact]
    public async Task TryClaimFire_is_atomic_under_concurrent_claimers()
    {
        int ruleId;
        using (var scope = Scope())
            ruleId = (await scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>().CreateRuleAsync(NewRule($"claim-{Guid.NewGuid():N}"))).Id;

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = Scope();
            return await scope.ServiceProvider.GetRequiredService<IAlertRuleRepository>()
                .TryClaimFireAsync(ruleId, Fixture.TenantId, cooldownMinutes: 60);
        }));

        Assert.Equal(1, claims.Count(c => c));

        using var verify = Scope();
        var repo = verify.ServiceProvider.GetRequiredService<IAlertRuleRepository>();
        var rule = Assert.Single(await repo.GetAllRulesAsync(), r => r.Id == ruleId);
        Assert.NotNull(rule.LastFiredAt);
        await repo.DeleteRuleAsync(ruleId);
    }

    [Fact]
    public async Task Tenant_catalog_lists_the_seeded_tenant()
    {
        using var scope = Scope();
        var tenants = await scope.ServiceProvider.GetRequiredService<ITenantCatalogRepository>().GetAllTenantsAsync();
        Assert.Contains(tenants, t => t.Id == Fixture.TenantId && t.Name == "phase0-tenant");
    }

    [Fact]
    public async Task Retention_settings_are_seeded_and_update_in_place()
    {
        using var scope = Scope();
        var settings = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        var original = await settings.GetSettingsAsync();
        try
        {
            await settings.UpdateSettingsAsync(new RetentionSettings { TraceRetentionDays = 31, LogRetentionDays = 32, MetricRetentionDays = 33 });
            var updated = await settings.GetSettingsAsync();
            Assert.Equal((31, 32, 33), (updated.TraceRetentionDays, updated.LogRetentionDays, updated.MetricRetentionDays));
        }
        finally
        {
            await settings.UpdateSettingsAsync(original);
        }
    }

    [Fact]
    public async Task Touching_an_api_key_sets_last_used_without_error()
    {
        var key = $"touch-{Guid.NewGuid():N}";
        await Fixture.SeedTenantAsync($"t-{Guid.NewGuid():N}", "k", key);
        var hash = Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash(key);
        using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<IApiKeyTouchStore>().TouchAsync([hash], CancellationToken.None);
    }
}
