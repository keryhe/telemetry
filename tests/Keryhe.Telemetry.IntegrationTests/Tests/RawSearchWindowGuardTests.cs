using Keryhe.Telemetry.Core;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="RawSearchWindowGuard"/> — no database dependency
/// (schema-simplification plan: search is unindexed and window-bounded on every provider, so
/// there is no tier axis any more). Covers accept/reject at the 24-hour default, the exemption path,
/// and the configured override.
/// </summary>
public class RawSearchWindowGuardTests
{
    [Fact]
    public void Every_Provider_Default_Limits_Search_To_24_Hours()
    {
        foreach (var capabilities in new[] { ProviderCapabilities.Default(), ProviderCapabilities.Constrained() })
        {
            Assert.Equal(24, capabilities.RawSearchWindowHours);
            Assert.False(RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromDays(30)).Allowed);
        }
    }

    [Fact]
    public void Default_Allows_Search_Within_Window()
    {
        var capabilities = ProviderCapabilities.Default();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(24));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Default_Rejects_Search_Beyond_Window()
    {
        var capabilities = ProviderCapabilities.Default();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(24.01));
        Assert.False(result.Allowed);
        Assert.Contains("24", result.Message);
    }

    [Fact]
    public void Default_Never_Limited_Without_A_Search_Filter()
    {
        var capabilities = ProviderCapabilities.Default();
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: false, isExempt: false, windowLength: TimeSpan.FromDays(365));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Default_Exempt_Request_Is_Never_Limited()
    {
        var capabilities = ProviderCapabilities.Default();
        // e.g. a trace-id lookup or mode=errors — the caller expresses the exemption.
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: true, windowLength: TimeSpan.FromDays(30));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Default_Honors_Configured_Override()
    {
        var capabilities = ProviderCapabilities.Default() with { RawSearchWindowHours = 12 };
        var withinOverride = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(12));
        var beyondOverride = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromHours(13));

        Assert.True(withinOverride.Allowed);
        Assert.False(beyondOverride.Allowed);
    }

    [Fact]
    public void Default_Override_To_Null_Removes_Limit()
    {
        var capabilities = ProviderCapabilities.Default() with { RawSearchWindowHours = null };
        var result = RawSearchWindowGuard.Check(capabilities, hasRawSearchFilter: true, isExempt: false, windowLength: TimeSpan.FromDays(365));
        Assert.True(result.Allowed);
    }
}
