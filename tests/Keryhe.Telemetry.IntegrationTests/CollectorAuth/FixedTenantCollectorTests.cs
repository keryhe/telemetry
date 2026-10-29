using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;
using Grpc.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.TestInfrastructure.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// The collector's keyless single-tenant mode (fixed-tenant-collector plan): every export on a collector endpoint is stored under the
/// configured tenant whatever it sends, the tenant must exist at startup, and outside Development the mode needs an explicit opt-in.
/// In process, no Docker; the startup check runs against a temp SQLite control-plane file. The negative control for "accepted with no
/// header" is <see cref="An_explicit_ApiKey_mode_still_rejects_a_missing_key"/> (and every rejection test in <see cref="CollectorAuthTests"/>).
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class FixedTenantCollectorTests
{
    private const long Tenant = 7;

    private static Dictionary<string, string?> FixedConfig(long tenantId = Tenant) => new()
    {
        ["Telemetry:Collector:Authentication:Mode"] = "FixedTenant",
        ["Telemetry:Collector:Authentication:FixedTenantId"] = tenantId.ToString(),
    };

    [Theory]
    [InlineData("traces")]
    [InlineData("logs")]
    [InlineData("metrics")]
    public async Task An_export_with_no_key_is_accepted_under_the_fixed_tenant(string signal)
    {
        await using var host = await CollectorHost.StartAsync(FixedConfig());

        await CollectorAuthTests.ExportAsync(host, signal, new Metadata());

        Assert.Equal([Tenant], CollectorAuthTests.TenantsEnqueued(host, signal));
        Assert.Equal(0, host.Lookup.Calls);
        Assert.Equal(0, host.TotalAuthFailures);
    }

    [Theory]
    [InlineData("Bearer ktel_unknown")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    public async Task A_presented_authorization_header_is_ignored(string header)
    {
        await using var host = await CollectorHost.StartAsync(FixedConfig());
        host.Lookup.Add("ktel_other-tenants-key", tenantId: 99);

        await CollectorAuthTests.ExportAsync(host, "traces", new Metadata { { "authorization", header } });
        await CollectorAuthTests.ExportAsync(host, "traces", CollectorAuthTests.Bearer("ktel_other-tenants-key"));

        Assert.Equal([Tenant, Tenant], CollectorAuthTests.TenantsEnqueued(host, "traces"));
        Assert.Equal(0, host.Lookup.Calls);
    }

    [Fact]
    public async Task An_otlp_http_export_with_no_key_is_accepted_under_the_fixed_tenant()
    {
        await using var host = await CollectorHost.StartAsync(FixedConfig());

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/traces")
        {
            Content = new ByteArrayContent(CollectorAuthTests.TraceRequest().ToByteArray()),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-protobuf");
        var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([Tenant], CollectorAuthTests.TenantsEnqueued(host, "traces"));
    }

    [Fact]
    public async Task A_non_collector_endpoint_is_left_alone()
    {
        await using var host = await CollectorHost.StartAsync(FixedConfig(), map: app =>
            app.MapGet("/plain", (HttpContext c) => c.User.Identity?.IsAuthenticated == true ? "authenticated" : "anonymous"));

        Assert.Equal("anonymous", await host.Http.GetStringAsync("/plain"));
    }

    [Fact]
    public async Task An_explicit_ApiKey_mode_still_rejects_a_missing_key()
    {
        await using var host = await CollectorHost.StartAsync(new()
        {
            ["Telemetry:Collector:Authentication:Mode"] = "ApiKey",
            ["Telemetry:Collector:Authentication:FixedTenantId"] = Tenant.ToString(),
        });

        var ex = await Assert.ThrowsAsync<RpcException>(() => CollectorAuthTests.ExportAsync(host, "traces", new Metadata()));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Empty(CollectorAuthTests.TenantsEnqueued(host, "traces"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData(null)]
    public void A_fixed_tenant_id_that_is_not_positive_fails_registration(string? id)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Collector:Authentication:Mode"] = "FixedTenant",
            ["Telemetry:Collector:Authentication:FixedTenantId"] = id,
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddKeryheTelemetryCollector(config));
        Assert.Contains("FixedTenantId", ex.Message);
    }

    [Theory]
    [InlineData("FixedTenant", null, 1.0)]
    [InlineData("FixedTenant", "0.3", 0.3)]
    [InlineData("ApiKey", null, 0.5)]
    public void The_tenant_quota_share_defaults_to_the_whole_queue_in_fixed_mode(string mode, string? configured, double expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["Telemetry:Collector:Authentication:Mode"] = mode,
            ["Telemetry:Collector:Authentication:FixedTenantId"] = Tenant.ToString(),
        };
        if (configured is not null) values["Telemetry:Ingestion:TenantQuota:MaxShare"] = configured;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddKeryheTelemetryCollector(config);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, provider.GetRequiredService<IOptions<TenantQuotaOptions>>().Value.MaxShare);
    }

    // ── The startup check, against a real SQLite control-plane file ─────────────────────────────────────────────

    private static bool StartupCheck(Type t) => t.Name == "FixedTenantStartupCheck";

    private static Dictionary<string, string?> WithControlPlane(Dictionary<string, string?> config, string connectionString)
    {
        config[ProviderKeys.CollectorControlPlane] = SqliteControlPlane.ProviderName;
        config["ConnectionStrings:ControlPlane"] = connectionString;
        return config;
    }

    private static async Task<long> AddTenantAsync(SqliteControlPlane controlPlane)
    {
        await using var connection = new SqliteConnection(controlPlane.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO tenants (name) VALUES ('on-site') RETURNING id";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Exception?> StartFailureAsync(Dictionary<string, string?> config, string environment = "Development", bool withSqliteProvider = false)
    {
        try
        {
            // withSqliteProvider: the SQLite control plane registered after the collector, as Collector.Server does, so its schema step
            // (which creates the file and the fixed tenant) must still run ahead of the fixed-tenant check.
            await using var host = await CollectorHost.StartAsync(config, environmentName: environment, keepHostedService: StartupCheck,
                addServices: withSqliteProvider
                    ? (services, configuration) => services.AddSqliteApiKeyLookup(configuration, long.Parse(config["Telemetry:Collector:Authentication:FixedTenantId"]!))
                    : null);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string AllMessages(Exception ex) =>
        string.Join(" | ", (ex is AggregateException a ? a.Flatten().InnerExceptions : [ex]).Select(e => e.ToString()));

    [Fact]
    public async Task The_collector_starts_when_the_fixed_tenant_exists()
    {
        await using var controlPlane = new SqliteControlPlane();
        await controlPlane.StartAsync();
        var id = await AddTenantAsync(controlPlane);

        Assert.Null(await StartFailureAsync(WithControlPlane(FixedConfig(id), controlPlane.ConnectionString)));
    }

    [Fact]
    public async Task The_collector_refuses_to_start_when_the_fixed_tenant_has_no_row()
    {
        await using var controlPlane = new SqliteControlPlane();
        await controlPlane.StartAsync();
        var id = await AddTenantAsync(controlPlane);

        var ex = await StartFailureAsync(WithControlPlane(FixedConfig(id + 1000), controlPlane.ConnectionString));

        Assert.NotNull(ex);
        Assert.Contains($"FixedTenantId is {id + 1000}", AllMessages(ex));
    }

    [Fact]
    public async Task Without_the_sqlite_schema_step_a_missing_control_plane_file_is_refused_and_not_created()
    {
        await using var controlPlane = new SqliteControlPlane();
        Directory.CreateDirectory(Path.GetDirectoryName(controlPlane.FilePath)!);

        var ex = await StartFailureAsync(WithControlPlane(FixedConfig(), controlPlane.ConnectionString));

        Assert.NotNull(ex);
        Assert.Contains("Could not check that tenant", AllMessages(ex));
        Assert.False(File.Exists(controlPlane.FilePath));
    }

    [Fact]
    public async Task With_the_sqlite_provider_a_missing_control_plane_file_and_the_fixed_tenant_are_created()
    {
        await using var controlPlane = new SqliteControlPlane();

        Assert.Null(await StartFailureAsync(WithControlPlane(FixedConfig(), controlPlane.ConnectionString), withSqliteProvider: true));

        await using var connection = new SqliteConnection(controlPlane.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM tenants WHERE id = {Tenant}";
        Assert.Equal("Default", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task With_the_sqlite_provider_and_ApplySchema_off_a_missing_tenant_is_still_refused()
    {
        await using var controlPlane = new SqliteControlPlane();
        await controlPlane.StartAsync();
        var config = WithControlPlane(FixedConfig(), controlPlane.ConnectionString);
        config["Telemetry:ControlPlane:Sqlite:ApplySchema"] = "false";

        var ex = await StartFailureAsync(config, withSqliteProvider: true);

        Assert.NotNull(ex);
        Assert.Contains($"FixedTenantId is {Tenant}", AllMessages(ex));
    }

    [Fact]
    public async Task Outside_Development_the_mode_needs_AllowUnauthenticatedIngestion()
    {
        await using var controlPlane = new SqliteControlPlane();
        await controlPlane.StartAsync();
        var id = await AddTenantAsync(controlPlane);

        var refused = await StartFailureAsync(WithControlPlane(FixedConfig(id), controlPlane.ConnectionString), "Production");
        var allowed = WithControlPlane(FixedConfig(id), controlPlane.ConnectionString);
        allowed["Telemetry:Collector:Authentication:AllowUnauthenticatedIngestion"] = "true";

        Assert.NotNull(refused);
        Assert.Contains("AllowUnauthenticatedIngestion", AllMessages(refused));
        Assert.Null(await StartFailureAsync(allowed, "Production"));
    }
}
