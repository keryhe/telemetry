using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>
/// Refuses to run the collector on a plaintext TCP address outside Development unless
/// <see cref="TelemetryCollectorOptions.AllowInsecureTransport"/> is set (collector-authentication plan,
/// decision 3). Two checks, because an exception thrown from an <c>ApplicationStarted</c> callback is
/// caught and logged by the host and does not stop it:
/// (a) <see cref="StartingAsync"/>, which runs before any hosted service's <c>StartAsync</c> and so before
/// Kestrel binds, inspects the configured addresses (<c>Kestrel:Endpoints</c>, plus <c>urls</c>/<c>HTTP_PORTS</c>
/// only when Kestrel would honor them) and throws; (b) a backstop on <c>ApplicationStarted</c>
/// inspects <see cref="IServerAddressesFeature"/> (addresses added in code) and stops the host with a
/// non-zero exit code. Both tolerate a missing or empty feature (<c>TestServer</c>).
/// </summary>
public sealed class PlaintextTransportGuard(
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<TelemetryCollectorOptions> options,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<PlaintextTransportGuard> logger) : IHostedLifecycleService
{
    private bool Enforced => !environment.IsDevelopment() && !options.Value.AllowInsecureTransport;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var plaintext = ConfiguredAddresses().Where(IsPlaintextTcp).ToList();
        if (plaintext.Count == 0) return Task.CompletedTask;

        if (!Enforced)
        {
            foreach (var a in plaintext)
                logger.LogWarning("Collector is listening on plaintext address {Address}; API keys cross the network unencrypted unless TLS is terminated in front of it", a);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            $"The collector refuses to start on plaintext address(es) {string.Join(", ", plaintext)}: API keys would be sent in cleartext. " +
            "Use an https:// endpoint, or set Telemetry:Collector:AllowInsecureTransport=true if TLS is terminated by a proxy in front of the collector.");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(CheckBoundAddresses);
        return Task.CompletedTask;
    }

    private void CheckBoundAddresses()
    {
        var addresses = (services.GetService(typeof(IServer)) as IServer)?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var plaintext = addresses?.Where(IsPlaintextTcp).ToList();
        if (plaintext is not { Count: > 0 }) return;

        if (!Enforced)
        {
            foreach (var a in plaintext)
                logger.LogWarning("Collector is listening on plaintext address {Address}", a);
            return;
        }

        logger.LogCritical("The collector is listening on plaintext address(es) {Addresses} and AllowInsecureTransport is not set; stopping", string.Join(", ", plaintext));
        Environment.ExitCode = 1;
        lifetime.StopApplication();
    }

    private IEnumerable<string> ConfiguredAddresses()
    {
        var kestrelEndpoints = false;
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
            if (endpoint["Url"] is { Length: > 0 } url)
            {
                kestrelEndpoints = true;
                foreach (var a in Split(url)) yield return a;
            }

        // Kestrel ignores the hosting URLs and ports while it has configured endpoints (it logs "Overriding
        // address(es)") unless preferHostingUrls is set. The official aspnet container image sets
        // ASPNETCORE_HTTP_PORTS=8080, which must not fail a TLS-only collector. CheckBoundAddresses still
        // checks whatever was actually bound.
        if (kestrelEndpoints && !string.Equals(configuration[WebHostDefaults.PreferHostingUrlsKey], "true", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (var key in new[] { "urls", "URLS" })
            if (configuration[key] is { Length: > 0 } urls)
                foreach (var a in Split(urls)) yield return a;

        // ASPNETCORE_HTTP_PORTS / HTTP_PORTS bind plaintext http on those ports.
        foreach (var key in new[] { "http_ports", "HTTP_PORTS" })
            if (configuration[key] is { Length: > 0 } ports)
                foreach (var p in Split(ports)) yield return $"http://*:{p}";
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsPlaintextTcp(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        && !address.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase);

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
