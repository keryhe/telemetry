using Keryhe.Telemetry.Api.Controllers;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// <c>GET /api/capabilities</c> after schema 3.0.0 (schema-simplification decision 19): the provider
/// tiers and the indexed-search flag are gone; the remaining fields keep their values. Pure logic --
/// no database.
/// </summary>
public class CapabilitiesTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Response_HasNoTierOrIndexedSearch_AndKeepsTheRemainingFields()
    {
        var capabilities = ProviderCapabilities.Default() with { AsOfBackoffSeconds = 5 };
        var controller = new CapabilitiesController(capabilities, Config(("Database:Provider", "PostgreSQL")));

        var dto = Assert.IsType<OkObjectResult>(controller.GetCapabilities().Result).Value as CapabilitiesDto;

        Assert.NotNull(dto);
        var properties = typeof(CapabilitiesDto).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Tier", properties);
        Assert.DoesNotContain("IndexedSearch", properties);
        Assert.Equal(["AsOfBackoffSeconds", "ExemplarPaging", "ExportMaxWindowDays", "Provider", "RawSearchWindowHours"], properties.Order());

        Assert.Equal("PostgreSQL", dto!.Provider);
        Assert.True(dto.ExemplarPaging);
        Assert.Equal(24, dto.RawSearchWindowHours);
        Assert.Equal(7, dto.ExportMaxWindowDays);
        Assert.Equal(5, dto.AsOfBackoffSeconds);
    }

    [Fact]
    public void ProviderDefaults_KeepTheirPerProviderValues()
    {
        var constrained = ProviderCapabilities.Constrained();   // SQL Server, MySQL
        Assert.False(constrained.ExemplarPaging);
        Assert.Equal(1, constrained.ExportMaxWindowDays);
        Assert.Equal(24, constrained.RawSearchWindowHours);     // search is window-bounded on EVERY provider now

        var standard = ProviderCapabilities.Default();          // PostgreSQL, ClickHouse
        Assert.True(standard.ExemplarPaging);
        Assert.Equal(7, standard.ExportMaxWindowDays);
        Assert.Equal(24, standard.RawSearchWindowHours);
    }

    [Fact]
    public void ConfigurationOverrides_StillApply()
    {
        var capabilities = ProviderCapabilities.FromConfiguration(ProviderCapabilities.Constrained(), Config(
            ("Telemetry:Query:RawSearchWindowHoursOverride", "6"),
            ("Telemetry:Export:MaxWindowDaysOverride", "3")));

        Assert.Equal(6, capabilities.RawSearchWindowHours);
        Assert.Equal(3, capabilities.ExportMaxWindowDays);
    }
}
