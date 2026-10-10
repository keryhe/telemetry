using Keryhe.Telemetry.Collector;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.IntegrationTests.ApiHttp;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>Plaintext-transport guard (decision 3) and the API startup validator's view of the collector's scheme (decision 5).</summary>
[Trait("Suite", "CollectorAuth")]
public class TransportAndValidatorTests
{
    private sealed class FakeServer(params string[] addresses) : IServer
    {
        public IFeatureCollection Features { get; } = Build(addresses);
        private static FeatureCollection Build(string[] addresses)
        {
            var f = new FeatureCollection();
            var feature = new ServerAddressesFeature();
            foreach (var a in addresses) feature.Addresses.Add(a);
            f.Set<IServerAddressesFeature>(feature);
            return f;
        }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static IHost Build(string environment, Dictionary<string, string?> config, bool allowInsecure = false, string[]? boundAddresses = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddSingleton<IOptions<TelemetryCollectorOptions>>(
            Options.Create(new TelemetryCollectorOptions { AllowInsecureTransport = allowInsecure }));
        if (boundAddresses is not null) builder.Services.AddSingleton<IServer>(new FakeServer(boundAddresses));
        builder.Services.AddHostedService<PlaintextTransportGuard>();
        return builder.Build();
    }

    [Fact]
    public async Task Production_with_a_plaintext_endpoint_fails_before_binding_and_names_the_address()
    {
        using var host = Build("Production", new() { ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5117" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("http://0.0.0.0:5117", ex.Message);
    }

    [Theory]
    [InlineData("urls", "http://localhost:5000")]
    [InlineData("HTTP_PORTS", "8080")]
    public async Task Production_with_plaintext_urls_or_http_ports_fails(string key, string value)
    {
        using var host = Build("Production", new() { [key] = value });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
    }

    // The official aspnet container image sets ASPNETCORE_HTTP_PORTS=8080; Kestrel ignores it (and urls) while
    // Kestrel:Endpoints is configured, so the guard must too.
    [Theory]
    [InlineData("urls", "http://localhost:5000")]
    [InlineData("HTTP_PORTS", "8080")]
    public async Task Production_ignores_urls_and_http_ports_overridden_by_a_kestrel_endpoint(string key, string value)
    {
        using var host = Build("Production", new()
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:7057",
            [key] = value,
        });
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Production_with_preferHostingUrls_still_checks_urls_over_a_kestrel_endpoint()
    {
        using var host = Build("Production", new()
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:7057",
            ["urls"] = "http://localhost:5000",
            ["preferHostingUrls"] = "true",
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("http://localhost:5000", ex.Message);
    }

    [Fact]
    public async Task Production_with_only_https_and_unix_sockets_starts()
    {
        using var host = Build("Production", new()
        {
            ["Kestrel:Endpoints:Https:Url"] = "https://0.0.0.0:7057",
            ["urls"] = "http://unix:/tmp/collector.sock",
        });
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task AllowInsecureTransport_and_Development_both_start_on_a_plaintext_endpoint()
    {
        var config = new Dictionary<string, string?> { ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5117" };
        using (var host = Build("Production", config, allowInsecure: true)) { await host.StartAsync(); await host.StopAsync(); }
        using (var host = Build("Development", config)) { await host.StartAsync(); await host.StopAsync(); }
    }

    [Fact]
    public async Task A_plaintext_address_added_in_code_stops_the_host_with_a_non_zero_exit_code()
    {
        var original = Environment.ExitCode;
        try
        {
            using var host = Build("Production", new(), boundAddresses: ["http://127.0.0.1:5117"]);
            await host.StartAsync();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Equal(1, Environment.ExitCode);
        }
        finally { Environment.ExitCode = original; }
    }

    [Fact]
    public async Task A_host_with_no_server_feature_is_tolerated()
    {
        using var host = Build("Production", new());
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task The_API_validator_does_not_count_the_collectors_scheme_as_an_authentication_scheme()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ApiHost.StartAsync(new ApiHost.Options
        {
            WithAuthentication = false,
            Config = new() { ["Telemetry:Api:Authorization:Enabled"] = "true" },
            Services = s =>
            {
                // The handler's own dependencies, so DI validation reaches the validator.
                s.AddSingleton<Keryhe.Telemetry.Core.Data.IngestionMetrics>();
                s.AddScoped<ITenantResolver>(_ => null!);
                s.AddSingleton<AuthFailureLimiter>();
                s.AddSingleton(TimeProvider.System);
                s.Configure<TelemetryCollectorOptions>(_ => { });
                s.AddAuthentication()
                    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(TelemetryAuthenticationSchemes.ApiKey, null);
            },
        }));
        Assert.Contains("no authentication scheme is registered", ex.ToString());
    }
}
