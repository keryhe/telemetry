using System.Net;
using System.Text.Json;
using Keryhe.Telemetry.Api;
using Keryhe.Telemetry.Api.Alerting.Evaluators;
using Keryhe.Telemetry.Api.Alerting.Notifications;
using Keryhe.Telemetry.Api.Alerting.Services;
using Keryhe.Telemetry.Api.Authorization;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.ApiHttp;

/// <summary>
/// HTTP-level tests of the API's base path, route shape and authorization (api-base-path-authorization
/// plan, verification 1-17). In-process, no database. A route whose repository is not faked answers 500
/// once it clears authorization, so "authorized" below means "not 401/403".
/// </summary>
[Trait("Suite", "ApiHttp")]
public class ApiHttpTests
{
    private static Dictionary<string, string?> Cfg(params (string Key, string Value)[] pairs) =>
        pairs.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => (string?)g.Last().Value); // last wins

    private static readonly (string, string) AuthOn = ("Telemetry:Api:Authorization:Enabled", "true");
    private static (string, string) Policy(string level, string name) => ($"Telemetry:Api:Authorization:Policies:{level}", name);
    private static (string, string)[] Mapping(int i, string type, string value, params string[] tenants) =>
        [($"Telemetry:Api:Authorization:TenantMappings:{i}:ClaimType", type),
         ($"Telemetry:Api:Authorization:TenantMappings:{i}:ClaimValue", value),
         .. tenants.Select((t, n) => ($"Telemetry:Api:Authorization:TenantMappings:{i}:Tenants:{n}", t))];

    private static async Task StartDisposed(ApiHost.Options o) { await using var _ = await ApiHost.StartAsync(o); }
    private static async Task<ApiHost> Start(ApiHost.Options? o = null) => await ApiHost.StartAsync(o);
    private static ApiHost.Options Opts(IEnumerable<(string, string)> cfg, Action<IServiceCollection>? services = null, bool realUi = false, string env = "Development", bool auth = true) =>
        new() { Config = Cfg(cfg.ToArray()), Services = services, RealUi = realUi, Environment = env, WithAuthentication = auth };

    private static (string Operator, string Admin, string Reader) Users => ("role=operator", "role=admin", "role=reader");

    // The default policy set most enabled tests use: reads need sign-in, Admin needs role=admin, and
    // role=operator reaches every tenant.
    private static (string, string)[] Enabled(params (string, string)[] extra) =>
        [AuthOn, Policy("Admin", "Admin"), .. Mapping(0, "role", "operator", "*"), .. extra];

    // ── 1-5: base path and route shape ───────────────────────────────────────

    [Fact]
    public async Task DefaultRoutes_AreUnderApiAndTenantScopedOnesUnderTenants()
    {
        await using var host = await Start();
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/capabilities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/1/resources/services")).StatusCode);

        var tenantScoped = new[] { "Traces", "Logs", "Metrics", "Resources", "Alerts" };
        foreach (var d in LibraryActions(host))
        {
            var template = d.AttributeRouteInfo!.Template!;
            if (tenantScoped.Contains(d.ControllerName))
                Assert.StartsWith("api/tenants/{tenantId", template);
            else
            {
                Assert.StartsWith("api/", template);
                Assert.DoesNotContain("tenants/{tenantId", template);
            }
        }
    }

    [Fact]
    public async Task ConfiguredBasePath_MovesEveryRoute()
    {
        await using var host = await Start(Opts([("Telemetry:Api:BasePath", "/telemetry/api")]));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/telemetry/api/tenants/1/resources/services")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/telemetry/api/capabilities")).StatusCode);
        // The old path is no longer the API: with a base path configured, it is just an unknown path.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/1/resources/services")).StatusCode); // the SPA stand-in
        Assert.Equal("text/html", (await host.GetAsync("/api/tenants/1/resources/services")).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task ConsumerControllers_AreNotPrefixedOrAuthorized()
    {
        await using var host = await Start(Opts([("Telemetry:Api:BasePath", "/telemetry/api"), AuthOn, .. Mapping(0, "role", "operator", "*")]));
        var response = await host.GetAsync("/api/own"); // anonymous, and authorization is on
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("own", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/a b")]
    [InlineData("/x/{y}")]
    public async Task InvalidBasePath_FailsStartup(string basePath)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => StartDisposed(Opts([("Telemetry:Api:BasePath", basePath)])));
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/tenants/abc/resources/services")]
    [InlineData("/api/tenants/0/resources/services")]
    public async Task UnknownApiPaths_AreJson404_NotTheSpaShell(string path)
    {
        await using var host = await Start();
        var response = await host.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("json", response.Content.Headers.ContentType!.MediaType);
        // ...while a SPA deep link on the same host still reaches the shell.
        var spa = await host.GetAsync("/t/1/traces");
        Assert.Equal("text/html", spa.Content.Headers.ContentType!.MediaType);
    }

    // ── 6: the tenant comes from the route ───────────────────────────────────

    [Fact]
    public async Task Repositories_SeeTheRoutesTenant_AndTheOldHeaderIsIgnored()
    {
        await using var host = await Start();
        Assert.Equal("""["svc-1"]""", await (await host.GetAsync("/api/tenants/1/resources/services")).Content.ReadAsStringAsync());
        var response = await host.SendAsync(HttpMethod.Get, "/api/tenants/2/resources/services", configure: r => r.Headers.Add("X-Tenant-Id", "1"));
        Assert.Equal("""["svc-2"]""", await response.Content.ReadAsStringAsync());
    }

    // ── 7-8: the UI follows the API ──────────────────────────────────────────

    [Fact]
    public async Task UiApiUrl_FollowsTheApiBasePath_UnlessSetExplicitly()
    {
        await using (var host = await Start(Opts([("Telemetry:Api:BasePath", "/t/api")], realUi: true)))
            Assert.Equal("/t/api", (await ConfigJson(host, "/config.json")).GetProperty("apiUrl").GetString());

        await using (var host = await Start(Opts([("Telemetry:Api:BasePath", "/t/api"), ("TelemetryUi:ApiBasePath", "https://other.example/api")], realUi: true)))
            Assert.Equal("https://other.example/api", (await ConfigJson(host, "/config.json")).GetProperty("apiUrl").GetString());

        await using (var host = await Start(Opts([], realUi: true)))
            Assert.Equal("/api", (await ConfigJson(host, "/config.json")).GetProperty("apiUrl").GetString());
    }

    [Fact]
    public async Task UiAndApiUnderOneHost_BothAnswer()
    {
        await using var host = await Start(Opts([("Telemetry:Api:BasePath", "/telemetry/api"), ("TelemetryUi:BasePath", "/telemetry")], realUi: true));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/telemetry/api/tenants/1/resources/services")).StatusCode);
        Assert.Equal("/telemetry/api", (await ConfigJson(host, "/telemetry/config.json")).GetProperty("apiUrl").GetString());
        // The UI's deep link is not the API's: it is left to the UI fallback. (This host has no packaged
        // bundle, so the shell itself answers 404, but not with the API's JSON problem.)
        var deepLink = await host.GetAsync("/telemetry/t/1/traces");
        Assert.NotEqual("application/problem+json", deepLink.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetAsync("/telemetry/api/nope")).StatusCode);
    }

    [Fact]
    public async Task UiAuthSettings_ReachConfigJson_AndBadOnesFailStartup()
    {
        await using (var host = await Start(Opts([("TelemetryUi:Auth:LoginUrl", "/account/login"), ("TelemetryUi:Auth:IncludeCredentials", "true")], realUi: true)))
        {
            var auth = (await ConfigJson(host, "/config.json")).GetProperty("auth");
            Assert.Equal("cookie", auth.GetProperty("mode").GetString());
            Assert.Equal("/account/login", auth.GetProperty("loginUrl").GetString());
            Assert.True(auth.GetProperty("includeCredentials").GetBoolean());
        }
        await using (var host = await Start(Opts([], realUi: true)))
            Assert.False((await ConfigJson(host, "/config.json")).TryGetProperty("auth", out _)); // nothing set: omitted

        await using (var host = await Start(Opts([("TelemetryUi:Auth:Mode", "oidc"), ("TelemetryUi:Auth:Oidc:Authority", "https://idp.example"),
                         ("TelemetryUi:Auth:Oidc:ClientId", "spa")], realUi: true)))
            Assert.Equal("spa", (await ConfigJson(host, "/config.json")).GetProperty("auth").GetProperty("oidc").GetProperty("clientId").GetString());

        foreach (var bad in new[]
        {
            new[] { ("TelemetryUi:Auth:Mode", "saml") },
            new[] { ("TelemetryUi:Auth:Mode", "oidc") },
            new[] { ("TelemetryUi:Auth:Mode", "oidc"), ("TelemetryUi:Auth:Oidc:Authority", "http://idp.example"), ("TelemetryUi:Auth:Oidc:ClientId", "spa") },
            new[] { ("TelemetryUi:Auth:LoginUrl", "javascript:alert(1)") },
        })
            await Assert.ThrowsAnyAsync<Exception>(() => StartDisposed(Opts(bad, realUi: true)));
    }

    private static async Task<JsonElement> ConfigJson(ApiHost host, string url) =>
        JsonDocument.Parse(await host.Client.GetStringAsync(url)).RootElement.Clone();

    // ── 9-10: disabled, and classification ───────────────────────────────────

    [Fact]
    public async Task AuthorizationDisabled_AnonymousCallersReachEveryTenant()
    {
        await using var host = await Start(Opts([], auth: false));
        foreach (var id in new[] { 1, 2, 3 })
            Assert.Equal(HttpStatusCode.OK, (await host.GetAsync($"/api/tenants/{id}/resources/services")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", json: true)).StatusCode);
    }

    [Fact]
    public async Task EveryActionHasExactlyOneOperation_AndTheTenantScopedSetIsFixed()
    {
        await using var host = await Start();
        var actions = LibraryActions(host).ToList();
        Assert.NotEmpty(actions);
        foreach (var a in actions)
            Assert.Single(a.MethodInfo.GetCustomAttributes(typeof(TelemetryOperationAttribute), false));

        var scoped = actions.Where(a => a.ControllerTypeInfo.IsDefined(typeof(TenantScopedAttribute), false))
            .Select(a => a.ControllerName).Distinct().Order().ToArray();
        Assert.Equal(new[] { "Alerts", "Logs", "Metrics", "Resources", "Traces" }, scoped);

        // The documented classification: three exports, alert changes, the retention change.
        Assert.Equal(3, actions.Count(a => Op(a) == TelemetryOperation.Export));
        Assert.All(actions.Where(a => a.ControllerName == "Alerts" && a.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().Single().HttpMethods.Single() != "GET"),
            a => Assert.Equal(TelemetryOperation.ManageAlerts, Op(a)));
        Assert.Equal(TelemetryOperation.ManageSettings, Op(actions.Single(a => a.ActionName == "UpdateRetentionSettings")));
        Assert.Equal(TelemetryOperation.Read, Op(actions.Single(a => a.ActionName == "GetRetentionSettings")));

        static TelemetryOperation Op(ControllerActionDescriptor a) =>
            ((TelemetryOperationAttribute)a.MethodInfo.GetCustomAttributes(typeof(TelemetryOperationAttribute), false).Single()).Operation;
    }

    private static IEnumerable<ControllerActionDescriptor> LibraryActions(ApiHost host) =>
        host.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(a => a.ControllerTypeInfo.Assembly == typeof(TelemetryApiOptions).Assembly);

    // ── 11-12: operations and the policy fallbacks ───────────────────────────

    [Fact]
    public async Task Enabled_Anonymous_Is401OnEveryLibraryAction()
    {
        await using var host = await Start(Opts(Enabled()));
        foreach (var a in LibraryActions(host))
        {
            var method = a.ActionConstraints!.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>().Single().HttpMethods.Single();
            var url = "/" + System.Text.RegularExpressions.Regex.Replace(a.AttributeRouteInfo!.Template!, @"\{(tenantId)[^}]*\}", "1");
            url = System.Text.RegularExpressions.Regex.Replace(url, @"\{[^}]+\}", "1");
            var response = await host.SendAsync(new HttpMethod(method), url);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{method} {url} gave {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Enabled_ReaderReads_AdminChanges()
    {
        await using var host = await Start(Opts(Enabled()));
        var reader = new[] { "role=operator" };
        var admin = new[] { "role=operator", "role=admin" };

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/1/resources/services", reader)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/settings/retention", reader)).StatusCode);
        Assert.True(ApiHost.Authorized(await host.GetAsync("/api/tenants/1/logs/export", reader)));

        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", reader, json: true, configure: Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Post, "/api/tenants/1/alerts/rules", reader, configure: Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true, configure: Csrf)).StatusCode);
        Assert.True(ApiHost.Authorized(await host.SendAsync(HttpMethod.Post, "/api/tenants/1/alerts/rules", admin, configure: Csrf)));
    }

    [Fact]
    public async Task ExportPolicy_RestrictsExportWithoutChangingReads()
    {
        await using var host = await Start(Opts(Enabled(Policy("Export", "ExportPolicy"))));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/1/resources/services", ["role=operator"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/tenants/1/logs/export", ["role=operator"])).StatusCode);
        Assert.True(ApiHost.Authorized(await host.GetAsync("/api/tenants/1/logs/export", ["role=operator", "scp=export"])));
    }

    [Fact]
    public async Task ManageSettingsPolicy_SeparatesSettingsFromAlertManagement()
    {
        await using var host = await Start(Opts(Enabled(Policy("ManageSettings", "SettingsPolicy"))));
        var admin = new[] { "role=operator", "role=admin" };
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true, configure: Csrf)).StatusCode);
        Assert.True(ApiHost.Authorized(await host.SendAsync(HttpMethod.Post, "/api/tenants/1/alerts/rules", admin, configure: Csrf)));
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", ["role=settings"], json: true, configure: Csrf)).StatusCode);
    }

    // ── 13: startup validation ───────────────────────────────────────────────

    [Fact]
    public async Task Misconfiguration_FailsStartupNamingTheProblem()
    {
        var undefinedPolicy = await Assert.ThrowsAnyAsync<Exception>(() => StartDisposed(Opts(Enabled(Policy("Admin", "NoSuchPolicy")))));
        Assert.Contains("NoSuchPolicy", Flatten(undefinedPolicy));

        var noScheme = await Assert.ThrowsAnyAsync<Exception>(() => StartDisposed(Opts(Enabled(), auth: false)));
        Assert.Contains("authentication scheme", Flatten(noScheme));

        var malformed = await Assert.ThrowsAnyAsync<Exception>(() =>
            StartDisposed(Opts([AuthOn, ("Telemetry:Api:Authorization:TenantMappings:0:ClaimType", "role")])));
        Assert.Contains("TenantMappings:0", Flatten(malformed));

        var noTenantAccess = await Assert.ThrowsAnyAsync<Exception>(() => StartDisposed(Opts([AuthOn])));
        Assert.Contains("TenantMappings", Flatten(noTenantAccess));

        static string Flatten(Exception e) => e.ToString();
    }

    [Fact]
    public async Task Warnings_AdminUnset_AndUnauthenticatedOutsideDevelopment()
    {
        await using (var host = await Start(Opts([AuthOn, .. Mapping(0, "role", "operator", "*")])))
            Assert.Contains(host.Warnings, w => w.Contains("No Admin policy"));
        await using (var host = await Start(Opts(Enabled())))
            Assert.DoesNotContain(host.Warnings, w => w.Contains("No Admin policy"));

        await using (var host = await Start(Opts([], env: "Production")))
            Assert.Contains(host.Warnings, w => w.Contains("unauthenticated"));
        await using (var host = await Start(Opts([], env: "Development")))
            Assert.DoesNotContain(host.Warnings, w => w.Contains("unauthenticated"));
    }

    // ── 14-15: tenant access ─────────────────────────────────────────────────

    [Fact]
    public async Task TenantMappings_GrantByNameAndId_AndRefuseOthersWithoutTouchingTheRepository()
    {
        await using var host = await Start(Opts([AuthOn, Policy("Admin", "Admin"),
            .. Mapping(0, "groups", "payments-team", "payments", "2"), .. Mapping(1, "role", "operator", "*")]));
        var team = new[] { "groups=payments-team" };

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/1/resources/services", team)).StatusCode); // by name
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/2/resources/services", team)).StatusCode); // by id

        var before = ApiHost.ResourceCalls;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/tenants/3/resources/services", team)).StatusCode);
        Assert.Equal(before, ApiHost.ResourceCalls);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/3/resources/services", ["role=operator"])).StatusCode);

        var listed = JsonDocument.Parse(await (await host.GetAsync("/api/tenants", team)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(new long[] { 1, 2 }, listed.EnumerateArray().Select(t => t.GetProperty("id").GetInt64()).ToArray());
        var all = JsonDocument.Parse(await (await host.GetAsync("/api/tenants", ["role=operator"])).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, all.GetArrayLength());

        // An all-tenants reader that is not an admin cannot change anything.
        Assert.Equal(HttpStatusCode.Forbidden, (await host.SendAsync(HttpMethod.Post, "/api/tenants/3/alerts/rules", ["role=operator"], configure: Csrf)).StatusCode);
    }

    [Fact]
    public async Task UnknownTenantName_MatchesNothing()
    {
        await using var host = await Start(Opts([AuthOn, .. Mapping(0, "groups", "g", "ghost")]));
        foreach (var id in new[] { 1, 2, 3 })
            Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync($"/api/tenants/{id}/resources/services", ["groups=g"])).StatusCode);
    }

    private sealed class GrantByClaim : AuthorizationHandler<TenantAccessRequirement, TelemetryResource>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAccessRequirement requirement, TelemetryResource resource)
        {
            if (context.User.HasClaim("grant", resource.TenantId.ToString()!)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class DenyTenantThree : AuthorizationHandler<TenantAccessRequirement, TelemetryResource>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAccessRequirement requirement, TelemetryResource resource)
        {
            if (resource.TenantId == 3) context.Fail();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConsumerHandler_CanGrantAccessWithoutMappings()
    {
        await using var host = await Start(Opts([AuthOn],
            s => s.AddScoped<IAuthorizationHandler, GrantByClaim>()));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/2/resources/services", ["grant=2"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/tenants/1/resources/services", ["grant=2"])).StatusCode);
    }

    [Fact]
    public async Task ConsumerHandler_CanNarrowWhatAMappingGranted()
    {
        await using var host = await Start(Opts(Enabled(), s => s.AddScoped<IAuthorizationHandler, DenyTenantThree>()));
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/tenants/2/resources/services", ["role=operator"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/tenants/3/resources/services", ["role=operator"])).StatusCode);
    }

    [Fact]
    public async Task TenantRefusals_AreMarked_OperationRefusalsAreNot()
    {
        await using var host = await Start(Opts([AuthOn, Policy("Admin", "Admin"), .. Mapping(0, "groups", "payments-team", "payments")]));
        var team = new[] { "groups=payments-team" };

        var tenant = await host.GetAsync("/api/tenants/3/resources/services", team);
        Assert.Equal(HttpStatusCode.Forbidden, tenant.StatusCode);
        Assert.Equal("tenant", tenant.Headers.GetValues(TelemetryAuthorizationFilter.DeniedHeader).Single());

        // Tenant 1 is theirs, but they may not manage its alerts: refused without the tenant marker.
        var operation = await host.SendAsync(HttpMethod.Post, "/api/tenants/1/alerts/rules", team, configure: Csrf);
        Assert.Equal(HttpStatusCode.Forbidden, operation.StatusCode);
        Assert.False(operation.Headers.Contains(TelemetryAuthorizationFilter.DeniedHeader));
    }

    [Fact]
    public async Task APolicysOwnScheme_AuthenticatesAndChallenges()
    {
        await using var host = await Start(Opts(Enabled(Policy("Read", "AltRead"))));

        // Signed in only through "Alt", which is not the default scheme.
        var signedIn = await host.SendAsync(HttpMethod.Get, "/api/tenants/1/resources/services", configure: r => r.Headers.Add("X-Alt-User", "a"));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        var anonymous = await host.GetAsync("/api/tenants/1/resources/services");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Alt", anonymous.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public void InRepoHosts_DoNotPinTheUisApiPath()
    {
        // Pinning TelemetryUi:ApiBasePath stops the UI following Telemetry:Api:BasePath.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Telemetry.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var host in new[] { "Keryhe.Telemetry.Server", "Keryhe.Telemetry.Api.Server" })
        {
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddJsonFile(Path.Combine(root.FullName, "src", host, "appsettings.json")).Build();
            Assert.Null(config["TelemetryUi:ApiBasePath"]);
        }
    }

    // ── 16: CSRF ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csrf_ChangesNeedTheClientHeaderUnlessABearerTokenIsPresent()
    {
        await using var host = await Start(Opts(Enabled()));
        var admin = new[] { "role=operator", "role=admin" };

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true, configure: Csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true,
            configure: r => r.Headers.Add("Authorization", "Bearer abc"))).StatusCode);
        // Basic (and Negotiate) credentials are attached by the browser itself, so they do not exempt a change.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/settings/retention", admin, json: true,
            configure: r => r.Headers.Add("Authorization", "Basic dTpw"))).StatusCode);
        // Reads never need it.
        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/settings/retention", admin)).StatusCode);
    }

    // ── 17: background alert evaluation ──────────────────────────────────────

    [Fact]
    public async Task AlertEvaluation_SetsTheTenantDirectly_OutsideAnyRequest()
    {
        var tenant = new ApiTenantContext();
        var service = new AlertService(new ApiHost.FakeAlerts(), Array.Empty<IAlertEvaluator>(), new WebhookNotificationChannel(new NoFactory(), NullLogger<WebhookNotificationChannel>.Instance),
            NullLogger<AlertService>.Instance, tenant);
        await service.EvaluateAllAsync();
        Assert.Equal(2, tenant.GetRequiredTenantId()); // last tenant evaluated; no filter involved
    }

    private sealed class NoFactory : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    private static void Csrf(HttpRequestMessage r) => r.Headers.Add(TelemetryAuthorizationFilter.ClientHeader, "1");
}
