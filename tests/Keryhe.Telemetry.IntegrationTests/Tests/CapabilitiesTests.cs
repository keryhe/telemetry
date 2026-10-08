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
        var capabilities = ProviderCapabilities.Default();
        var controller = new CapabilitiesController(capabilities, Config(("Database:Provider", "PostgreSQL")));

        var dto = Assert.IsType<OkObjectResult>(controller.GetCapabilities().Result).Value as CapabilitiesDto;

        Assert.NotNull(dto);
        var properties = typeof(CapabilitiesDto).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Tier", properties);
        Assert.DoesNotContain("IndexedSearch", properties);
        Assert.DoesNotContain("ExemplarPaging", properties);
        Assert.DoesNotContain("AsOfBackoffSeconds", properties);
        Assert.Equal(
            ["ExemplarLimit", "ExportMaxWindowDays", "LogListLimit", "MetricCatalogLimit", "Provider", "RawSearchWindowHours", "TraceListLimit"],
            properties.Order());

        Assert.Equal("PostgreSQL", dto!.Provider);
        Assert.Equal(24, dto.RawSearchWindowHours);
        Assert.Equal(7, dto.ExportMaxWindowDays);
        Assert.Equal(1000, dto.LogListLimit);
        Assert.Equal(500, dto.TraceListLimit);
        Assert.Equal(500, dto.MetricCatalogLimit);
        Assert.Equal(500, dto.ExemplarLimit);
    }

    [Fact]
    public void ProviderDefaults_KeepTheirPerProviderValues()
    {
        var constrained = ProviderCapabilities.Constrained();   // SQL Server, MySQL
        Assert.Equal(1, constrained.ExportMaxWindowDays);
        Assert.Equal(24, constrained.RawSearchWindowHours);     // search is window-bounded on EVERY provider now

        var standard = ProviderCapabilities.Default();          // PostgreSQL, ClickHouse
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

    [Fact]
    public void ListLimits_AreConfigurable()
    {
        var capabilities = ProviderCapabilities.FromConfiguration(ProviderCapabilities.Default(), Config(
            ("Telemetry:Query:Limits:Logs", "200"),
            ("Telemetry:Query:Limits:Traces", "100"),
            ("Telemetry:Query:Limits:MetricCatalog", "50"),
            ("Telemetry:Query:Limits:Exemplars", "25")));

        Assert.Equal(200, capabilities.Limits.Logs);
        Assert.Equal(100, capabilities.Limits.Traces);
        Assert.Equal(50, capabilities.Limits.MetricCatalog);
        Assert.Equal(25, capabilities.Limits.Exemplars);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void ListLimit_ThatIsNotPositive_FailsStartupNamingTheKey(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ProviderCapabilities.FromConfiguration(
            ProviderCapabilities.Default(), Config(("Telemetry:Query:Limits:Logs", value))));

        Assert.Contains("Telemetry:Query:Limits:Logs", ex.Message);
    }
}
