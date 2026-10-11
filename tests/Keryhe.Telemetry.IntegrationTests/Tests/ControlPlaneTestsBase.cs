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

    private async Task<long> NewTenantAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid():N}";
        return (await Fixture.SeedTenantAsync(name, name + "-key", name + "-secret")).Id;
    }

    /// <summary>Each tenant's windows are columns on its row; a new tenant takes the schema defaults, 30/30/30.</summary>
    [Fact]
    public async Task A_new_tenant_has_the_default_retention_windows()
    {
        var tenantId = await NewTenantAsync("retention-defaults");
        using var scope = Scope();
        var settings = await scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>().GetSettingsAsync(tenantId);

        Assert.NotNull(settings);
        Assert.Equal((RetentionDefaults.TraceDays, RetentionDefaults.LogDays, RetentionDefaults.MetricDays),
            (settings.TraceRetentionDays, settings.LogRetentionDays, settings.MetricRetentionDays));
        Assert.Equal((30, 30, 30), (settings.TraceRetentionDays, settings.LogRetentionDays, settings.MetricRetentionDays));
    }

    [Fact]
    public async Task Updating_one_tenants_retention_leaves_the_others_unchanged_and_stamps_the_update()
    {
        var first = await NewTenantAsync("retention-a");
        var second = await NewTenantAsync("retention-b");
        using var scope = Scope();
        var settings = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        var secondBefore = await settings.GetSettingsAsync(second);

        var change = new RetentionSettings { TraceRetentionDays = 31, LogRetentionDays = 32, MetricRetentionDays = 33 };
        var before = DateTime.UtcNow.AddSeconds(-5);
        Assert.True(await settings.UpdateSettingsAsync(first, change));

        var updated = await settings.GetSettingsAsync(first);
        Assert.Equal((31, 32, 33), (updated!.TraceRetentionDays, updated.LogRetentionDays, updated.MetricRetentionDays));
        Assert.True(change.UpdatedAt > before, "the update stamps retention_updated_at on the passed settings");
        Assert.True(updated.UpdatedAt > before);

        var secondAfter = await settings.GetSettingsAsync(second);
        Assert.Equal((secondBefore!.TraceRetentionDays, secondBefore.LogRetentionDays, secondBefore.MetricRetentionDays, secondBefore.UpdatedAt),
            (secondAfter!.TraceRetentionDays, secondAfter.LogRetentionDays, secondAfter.MetricRetentionDays, secondAfter.UpdatedAt));
    }

    [Fact]
    public async Task Retention_of_an_unknown_tenant_is_null_and_false()
    {
        using var scope = Scope();
        var settings = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        Assert.Null(await settings.GetSettingsAsync(987_654_321));
        Assert.False(await settings.UpdateSettingsAsync(987_654_321, new RetentionSettings { TraceRetentionDays = 1, LogRetentionDays = 1, MetricRetentionDays = 1 }));
    }

    [Fact]
    public async Task GetAll_lists_every_tenants_windows()
    {
        var first = await NewTenantAsync("retention-all-a");
        var second = await NewTenantAsync("retention-all-b");
        using var scope = Scope();
        var settings = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        await settings.UpdateSettingsAsync(second, new RetentionSettings { TraceRetentionDays = 7, LogRetentionDays = 8, MetricRetentionDays = 9 });

        var all = await settings.GetAllAsync();

        Assert.True(all.ContainsKey(Fixture.TenantId) && all.ContainsKey(first) && all.ContainsKey(second));
        Assert.Equal((30, 30, 30), (all[first].TraceRetentionDays, all[first].LogRetentionDays, all[first].MetricRetentionDays));
        Assert.Equal((7, 8, 9), (all[second].TraceRetentionDays, all[second].LogRetentionDays, all[second].MetricRetentionDays));
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
