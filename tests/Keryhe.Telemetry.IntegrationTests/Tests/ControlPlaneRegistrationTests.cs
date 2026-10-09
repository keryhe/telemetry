using Keryhe.Telemetry.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The control-plane registrations fail at startup, naming <c>ConnectionStrings:ControlPlane</c>, when it is missing or blank
/// (control-plane split, decision 5), from the provider methods themselves so a consumer's own host gets the check too.
/// No Docker: nothing connects.
/// </summary>
public class ControlPlaneRegistrationTests
{
    private static IConfiguration Config(string? controlPlane)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:Api"] = "unused", ["ConnectionStrings:Collector"] = "unused" };
        if (controlPlane is not null) settings["ConnectionStrings:ControlPlane"] = controlPlane;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    public static IEnumerable<object[]> Registrations()
    {
        foreach (var side in new[] { "Collector", "Api" })
            foreach (var provider in new[] { "PostgreSql", "SqlServer", "MySql" })
                yield return [provider, side];
    }

    private static IServiceCollection Register(string provider, string side, IConfiguration configuration)
    {
        var services = new ServiceCollection();
        return (provider, side) switch
        {
            ("PostgreSql", "Collector") => services.AddPostgreSqlControlPlaneCollectorServices(configuration),
            ("PostgreSql", _) => services.AddPostgreSqlControlPlaneApiServices(configuration),
            ("SqlServer", "Collector") => services.AddSqlServerControlPlaneCollectorServices(configuration),
            ("SqlServer", _) => services.AddSqlServerControlPlaneApiServices(configuration),
            ("MySql", "Collector") => services.AddMySqlControlPlaneCollectorServices(configuration),
            _ => services.AddMySqlControlPlaneApiServices(configuration),
        };
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_missing_control_plane_connection_string_fails_naming_the_key(string provider, string side)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Register(provider, side, Config(null)));
        Assert.Contains("ConnectionStrings:ControlPlane", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_blank_control_plane_connection_string_fails_naming_the_key(string provider, string side)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Register(provider, side, Config("  ")));
        Assert.Contains("ConnectionStrings:ControlPlane", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public void A_configured_control_plane_registers_its_interfaces(string provider, string side)
    {
        var services = Register(provider, side, Config("Host=localhost;Database=cp"));
        var registered = services.Select(d => d.ServiceType).ToHashSet();
        if (side == "Collector")
            Assert.Contains(typeof(IApiKeyLookup), registered);
        else
        {
            Assert.Contains(typeof(IAlertRuleRepository), registered);
            Assert.Contains(typeof(ITenantCatalogRepository), registered);
            Assert.Contains(typeof(IRetentionSettingsRepository), registered);
        }
        // The control-plane registrations never carry the telemetry sweeper or bulk writer.
        Assert.DoesNotContain(typeof(IRetentionSweeper), registered);
        Assert.DoesNotContain(typeof(ITelemetryBulkWriter), registered);
    }
}
