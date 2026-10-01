using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// One per provider, started once per test run (xUnit collection fixture, see
/// <see cref="Collections"/>) and shared by every test class in that provider's collection.
///
/// Builds a bare <see cref="ServiceCollection"/> through the provider's own
/// <c>Add&lt;Provider&gt;CollectorServices</c>/<c>Add&lt;Provider&gt;ApiServices</c> — the same
/// registration path the real hosts use — rather than hand-built repositories, per the plan's
/// Phase 0 section. It deliberately does NOT call <c>AddKeryheTelemetryCollector</c>/
/// <c>AddKeryheTelemetryApi</c>: those also wire gRPC, MVC controllers and the background
/// ingestion worker, none of which this harness needs since it writes through
/// <see cref="ITelemetryBulkWriter"/> directly. It does still register the handful of
/// provider-agnostic singletons the bulk writers depend on directly
/// (<see cref="ResourceScopeCache"/>, logging) — see each provider's own
/// <c>*BulkWriter</c> constructor.
/// </summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    public abstract string ProviderName { get; }

    public const string ApiKeyPlainText = "phase0-integration-test-key";

    public ServiceProvider Services { get; private set; } = null!;
    public TestTenantContext TenantContext { get; } = new();
    public long TenantId { get; private set; }

    /// <summary>The provider's database container (started, schema applied and diagnostics off, as the integration tests always ran).</summary>
    protected abstract ProviderContainer Container { get; }

    /// <summary>The test database's connection string, for tests that issue raw SQL (counting rows, reading plans).</summary>
    public string DatabaseConnectionString => Container.ConnectionString;

    /// <summary>Connection string for the write side (<c>ConnectionStrings:Collector</c>).</summary>
    protected string CollectorConnectionString => Container.ConnectionString;

    /// <summary>Connection string for the read side (<c>ConnectionStrings:Api</c>). Same database as the collector string — one test database.</summary>
    protected string ApiConnectionString => Container.ConnectionString;

    /// <summary>Calls this provider's own <c>Add&lt;Provider&gt;CollectorServices</c>/<c>Add&lt;Provider&gt;ApiServices</c>.</summary>
    protected abstract void AddProviderServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Truncates the signal tables (not the container) so test classes in the same collection start clean.</summary>
    public abstract Task ResetAsync();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Collector"] = CollectorConnectionString,
                ["ConnectionStrings:Api"] = ApiConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        // Depended on directly by every provider's *BulkWriter — normally registered by
        // AddKeryheTelemetryCollector/AddKeryheTelemetryApi, which this harness deliberately does
        // not call (see class doc comment). TraceQueryCache was retired in Phase 3 (the traces list
        // page no longer scans the whole window in memory), so it no longer needs registering here.
        services.AddSingleton<ResourceScopeCache>();

        // Depended on directly by every provider's *BulkWriter since Phase 5 (list-pages-server-side
        // plan) — normally registered by AddKeryheTelemetryCollector, which this harness deliberately
        // does not call (see class doc comment). IMetricTouchStore itself is registered per-provider
        // by AddProviderServices below (mirrors IApiKeyTouchStore).
        services.AddSingleton<MetricTouchTracker>();

        // Mirrors AddKeryheTelemetryApi's own registration (list-pages-server-side plan, Phase 1)
        // so the integration harness sees these options too, even though this harness doesn't call
        // AddKeryheTelemetryApi itself (see class doc comment).
        services.Configure<QueryOptions>(configuration.GetSection(QueryOptions.SectionName));
        services.Configure<ExportOptions>(configuration.GetSection(ExportOptions.SectionName));

        services.AddSingleton<ITenantContext>(TenantContext);

        AddProviderServices(services, configuration);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var tenant = await Container.SeedTenantAsync("phase0-tenant", "phase0-key", ApiKeyPlainText);
        TenantId = tenant.Id;
        TenantContext.SetTenantId(TenantId);
    }

    public async Task DisposeAsync()
    {
        if (Services != null)
            await Services.DisposeAsync();
        await Container.DisposeAsync();
    }
}
