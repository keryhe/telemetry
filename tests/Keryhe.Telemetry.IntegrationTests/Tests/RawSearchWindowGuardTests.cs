using Keryhe.Telemetry.Core;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="RawSearchWindowGuard"/> — no database dependency
/// (list-pages-server-side plan, Phase 1, decision 39). Covers tier-based accept/reject for both
/// the analytics tier (no limit) and the standard tier (24-hour default), plus the exemption path.
/// </summary>
public class RawSearchWindowGuardTests
{
    [Fact]
    public void Analytics_Tier_Never_Limited()
    {
        var capabilities = ProviderCapabilities.AnalyticsDefault();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromDays(30));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Standard_Tier_Allows_Search_Within_Window()
    {
        var capabilities = ProviderCapabilities.StandardDefault();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(24));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Standard_Tier_Rejects_Search_Beyond_Window()
    {
        var capabilities = ProviderCapabilities.StandardDefault();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(24.01));
        Assert.False(result.Allowed);
        Assert.Contains("24", result.Message);
    }

    [Fact]
    public void Standard_Tier_Never_Limited_Without_A_Search_Filter()
    {
        var capabilities = ProviderCapabilities.StandardDefault();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: false, isExempt: false, windowLength: TimeSpan.FromDays(365));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Standard_Tier_Exempt_Request_Is_Never_Limited()
    {
        var capabilities = ProviderCapabilities.StandardDefault();
        // e.g. a trace-id lookup or mode=errors — the caller expresses the exemption.
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: true, windowLength: TimeSpan.FromDays(30));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Standard_Tier_Honors_Configured_Override()
    {
        var capabilities = ProviderCapabilities.StandardDefault() with { RawSearchWindowHours = 12 };
        var withinOverride = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(12));
        var beyondOverride = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(13));

        Assert.True(withinOverride.Allowed);
        Assert.False(beyondOverride.Allowed);
    }

    [Fact]
    public void Standard_Tier_Override_To_Null_Removes_Limit()
    {
        var capabilities = ProviderCapabilities.StandardDefault() with { RawSearchWindowHours = null };
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromDays(365));
        Assert.True(result.Allowed);
    }
}
