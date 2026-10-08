using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Keryhe.Telemetry.Api.Controllers;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.IntegrationTests.ApiHttp;

/// <summary>
/// An in-process host (TestServer) running the real <c>AddKeryheTelemetryApi</c> registration over fake
/// repositories and a header-driven authentication scheme. No database, no Docker (api-base-path-authorization
/// plan, decision 14). Only the repositories the exercised routes need are faked; a route that needs another
/// fails with a 500 once it gets past authorization, which the tests use as "authorized".
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    public static readonly TenantInfo[] Tenants = [new(1, "payments"), new(2, "checkout"), new(3, "other")];

    private readonly WebApplication _app;
    public HttpClient Client { get; }
    public List<string> Warnings { get; }
    public IServiceProvider Services => _app.Services;

    private ApiHost(WebApplication app, List<string> warnings)
    {
        _app = app;
        Warnings = warnings;
        Client = app.GetTestClient();
    }

    public sealed record Options
    {
        public Dictionary<string, string?> Config { get; init; } = new();
        public Action<IServiceCollection>? Services { get; init; }
        public bool WithAuthentication { get; init; } = true;
        public Action<Microsoft.AspNetCore.Authorization.AuthorizationOptions>? Policies { get; init; }
        public string Environment { get; init; } = "Development";
        /// <summary>Mounts the real UI middleware (false: a stand-in SPA fallback instead).</summary>
        public bool RealUi { get; init; }
    }

    public static async Task<ApiHost> StartAsync(Options? options = null)
    {
        options ??= new Options();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = options.Environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(options.Config);

        var warnings = new List<string>();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new WarningCapture(warnings));

        var services = builder.Services;
        if (options.WithAuthentication)
            services.AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { })
                .AddScheme<AuthenticationSchemeOptions, AltAuthHandler>("Alt", _ => { });
        services.AddAuthorization(o =>
        {
            o.AddPolicy("Read", p => p.RequireAuthenticatedUser());
            o.AddPolicy("Admin", p => p.RequireClaim("role", "admin"));
            o.AddPolicy("ExportPolicy", p => p.RequireClaim("scp", "export"));
            o.AddPolicy("SettingsPolicy", p => p.RequireClaim("role", "settings"));
            // Authenticated only through the non-default "Alt" scheme.
            o.AddPolicy("AltRead", p => p.AddAuthenticationSchemes("Alt").RequireAuthenticatedUser());
            options.Policies?.Invoke(o);
        });

        services.AddKeryheTelemetryApi(builder.Configuration);
        // The test assembly's own controller, to prove the library leaves a consumer's controllers alone.
        services.AddControllers().AddApplicationPart(typeof(OwnController).Assembly);
        services.AddScoped<ITenantCatalogRepository, FakeTenantCatalog>();
        services.AddScoped<IResourceReadRepository, FakeResources>();
        services.AddScoped<IRetentionSettingsRepository, FakeRetention>();
        services.AddScoped<IAlertRuleRepository, FakeAlerts>();
        services.AddSingleton(ProviderCapabilities.Default());
        if (options.RealUi) services.AddKeryheTelemetryUi(builder.Configuration);
        options.Services?.Invoke(services);

        var app = builder.Build();
        if (options.RealUi) app.UseKeryheTelemetryUi();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKeryheTelemetryApi();
        if (options.RealUi) app.MapKeryheTelemetryUiFallback();
        else app.MapFallback("{*path:nonfile}", () => Results.Text("<html>spa</html>", "text/html"));

        await app.StartAsync();
        return new ApiHost(app, warnings);
    }

    /// <summary>Sends a request as a signed-in user holding the given claims ("type=value"), or anonymous when <paramref name="claims"/> is null.</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string[]? claims = null,
        Action<HttpRequestMessage>? configure = null, bool json = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (json) request.Content = JsonContent.Create(new RetentionSettings { TraceRetentionDays = 30, LogRetentionDays = 30, MetricRetentionDays = 30 });
        if (claims is not null)
        {
            request.Headers.Add("X-Test-User", "u");
            if (claims.Length > 0) request.Headers.Add("X-Test-Claims", string.Join(';', claims));
        }
        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string url, string[]? claims = null) => SendAsync(HttpMethod.Get, url, claims);

    /// <summary>True when the request got past authorization (it may still fail later: unfaked repositories answer 500).</summary>
    public static bool Authorized(HttpResponseMessage r) => r.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ── Fakes ────────────────────────────────────────────────────────────────

    public static int ResourceCalls;

    private sealed class FakeTenantCatalog : ITenantCatalogRepository
    {
        public Task<List<TenantInfo>> GetAllTenantsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Tenants.ToList());
    }

    private sealed class FakeResources(ITenantContext tenant) : IResourceReadRepository
    {
        public Task<List<string>> GetDistinctServicesAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ResourceCalls);
            return Task.FromResult(new List<string> { $"svc-{tenant.GetRequiredTenantId()}" });
        }
    }

    private sealed class FakeRetention : IRetentionSettingsRepository
    {
        public Task<RetentionSettings> GetSettingsAsync(CancellationToken ct = default) => Task.FromResult(new RetentionSettings());
        public Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    public sealed class FakeAlerts : IAlertRuleRepository
    {
        public List<long> EnabledTenants { get; } = [1, 2];
        public Task<List<long>> GetEnabledTenantIdsAsync(CancellationToken ct = default) => Task.FromResult(EnabledTenants);
        public Task<List<AlertRule>> GetEnabledRulesAsync(CancellationToken ct = default) => Task.FromResult(new List<AlertRule>());
        public Task<List<AlertRule>> GetEnabledRulesAsync(long tenantId, CancellationToken ct = default) => Task.FromResult(new List<AlertRule>());
        public Task<List<AlertRule>> GetAllRulesAsync(CancellationToken ct = default) => Task.FromResult(new List<AlertRule>());
        public Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken ct = default) => Task.FromResult(rule);
        public Task<AlertRule> UpdateRuleAsync(AlertRule rule, CancellationToken ct = default) => Task.FromResult(rule);
        public Task DeleteRuleAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryClaimFireAsync(int ruleId, long tenantId, int cooldownMinutes, CancellationToken ct = default) => Task.FromResult(true);
        public Task AddAlertEventAsync(AlertEvent alertEvent, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<AlertEvent>> GetRecentAlertEventsAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult(new List<AlertEvent>());
    }

    /// <summary>"X-Test-User" signs the caller in; "X-Test-Claims" ("type=value;type=value") supplies its claims.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-User")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.Name, "u") };
            foreach (var pair in Request.Headers["X-Test-Claims"].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                claims.Add(new Claim(kv[0], kv[1]));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    /// <summary>A second, non-default scheme: "X-Alt-User" signs in as role=operator; its challenge says "Alt".</summary>
    private sealed class AltAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Alt-User")) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alt"), new Claim("role", "operator")], "Alt"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Alt")));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = "Alt";
            return Task.CompletedTask;
        }
    }

    private sealed class WarningCapture(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);
        public void Dispose() { }

        private sealed class Capture(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning) lock (sink) sink.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>A consumer's own controller, in the test assembly.</summary>
[ApiController]
[Route("api/own")]
public sealed class OwnController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok("own");
}
