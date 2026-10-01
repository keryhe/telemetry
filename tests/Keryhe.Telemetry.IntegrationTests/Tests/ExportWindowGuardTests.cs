using Keryhe.Telemetry.Core;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="ExportWindowGuard"/> (list-pages-server-side plan, Phase 8,
/// decision 17) — no database dependency, mirroring <see cref="RawSearchWindowGuardTests"/>'s own
/// shape for <see cref="RawSearchWindowGuard"/>. Unlike that guard, this one applies to every export
/// request regardless of filters, so there is no "hasFilter"/"isExempt" axis to cover here.
/// </summary>
public class ExportWindowGuardTests
{
    [Fact]
    public void SevenDay_Provider_Allows_Window_Within_SevenDays()
    {
        var capabilities = ProviderCapabilities.Default();
        var result = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(7));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void SevenDay_Provider_Rejects_Window_Beyond_SevenDays()
    {
        var capabilities = ProviderCapabilities.Default();
        var result = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(7.01));
        Assert.False(result.Allowed);
        Assert.Contains("7", result.Message);
    }

    [Fact]
    public void OneDay_Provider_Allows_Window_Within_OneDay()
    {
        var capabilities = ProviderCapabilities.Constrained();
        var result = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(1));
        Assert.True(result.Allowed);
    }

    [Fact]
    public void OneDay_Provider_Rejects_Window_Beyond_OneDay()
    {
        var capabilities = ProviderCapabilities.Constrained();
        var result = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(1.01));
        Assert.False(result.Allowed);
        Assert.Contains("1", result.Message);
    }

    [Fact]
    public void OneDay_Provider_Rejects_Even_Without_Any_SearchFilter()
    {
        // Unlike RawSearchWindowGuard, ExportWindowGuard has no "no filter, no limit" escape hatch:
        // decision 17's window cap applies to every export (unfiltered included), since export has
        // no row cap to otherwise bound the work.
        var capabilities = ProviderCapabilities.Constrained();
        var result = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(2));
        Assert.False(result.Allowed);
    }

    [Fact]
    public void Honors_Configured_Override()
    {
        var capabilities = ProviderCapabilities.Constrained() with { ExportMaxWindowDays = 3 };
        var within = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(3));
        var beyond = ExportWindowGuard.Check(capabilities, TimeSpan.FromDays(3.01));

        Assert.True(within.Allowed);
        Assert.False(beyond.Allowed);
    }
}
