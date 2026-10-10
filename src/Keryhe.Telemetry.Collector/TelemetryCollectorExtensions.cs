using Keryhe.Telemetry.Collector;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Collector.Health;
using Keryhe.Telemetry.Collector.Services;
using Grpc.AspNetCore.Server;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Write;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry collector (gRPC OTLP ingestion and
/// the provider-agnostic ingestion channel + worker) into a host application. Mirrors
/// <c>AddKeryheTelemetryApi</c> on the read side.
/// </summary>
public static class TelemetryCollectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the write path: gRPC, the ingestion channel (gated on resident record/span
    /// count — see <see cref="TelemetryIngestionOptions"/>), and the background worker that
    /// drains it. This does not register a database provider — the host must also call the
    /// active provider's <c>Add&lt;Provider&gt;CollectorServices(configuration)</c> (e.g.
    /// <c>AddPostgreSqlCollectorServices</c>), which supplies <c>ITelemetryBulkWriter</c>
    /// (connection string from <c>ConnectionStrings:Collector</c>), and the control plane's
    /// <c>Add&lt;Provider&gt;ControlPlaneCollectorServices(configuration)</c>, which supplies
    /// <c>IApiKeyLookup</c> and <c>IApiKeyTouchStore</c> (<c>ConnectionStrings:ControlPlane</c>). The host still owns CORS, Kestrel configuration, and
    /// calling <c>MapKeryheTelemetryCollector()</c>.
    /// </summary>
    public static IServiceCollection AddKeryheTelemetryCollector(this IServiceCollection services, IConfiguration configuration)
    {
        // The receive limit is configuration (Telemetry:Collector:MaxReceiveMessageSizeBytes), so it is applied through options rather
        // than a delegate here, where the configuration section is not yet bound.
        services.AddGrpc();
        services.AddOptions<GrpcServiceOptions>().Configure<IOptions<TelemetryCollectorOptions>>((grpc, collector) =>
        {
            var max = collector.Value.MaxReceiveMessageSizeBytes;
            grpc.MaxReceiveMessageSize = max > 0 ? max : null;
            // grpc-dotnet checks the limit against the size on the wire only; replace its gzip with one that bounds the decompressed size.
            grpc.CompressionProviders = [new BoundedGzipCompressionProvider(max)];
        });

        services.AddLogging();

        // Ingestion queue/batch limits — see TelemetryIngestionOptions for why records (spans for
        // traces) rather than batches are what these bound. Bound from Telemetry:Ingestion.
        services.AddOptions<TelemetryIngestionOptions>()
            .Bind(configuration.GetSection(TelemetryIngestionOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        // Attribute / field limits applied while an export is converted (Telemetry:Ingestion:Limits), and the converter that applies them.
        services.AddOptions<IngestionLimitsOptions>()
            .Bind(configuration.GetSection(IngestionLimitsOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        services.AddSingleton<OtlpAttributeConverter>();
        // What one tenant may take of the shared queue (Telemetry:Ingestion:TenantQuota); reloadable.
        services.AddOptions<TenantQuotaOptions>()
            .Bind(configuration.GetSection(TenantQuotaOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        // Every error is transient unless the provider registers a classifier that knows its driver's permanent ones.
        services.TryAddSingleton<IFlushErrorClassifier>(DefaultFlushErrorClassifier.Instance);

        // Singletons shared across all gRPC requests and the background worker.
        services.AddSingleton<TelemetryIngestionChannel>();
        services.AddSingleton<ResourceScopeCache>();
        services.AddSingleton<IngestionMetrics>();

        // Tenant resolution: CachingTenantResolver / ApiKeyTouchWorker are registered once, here,
        // provider-agnostically — every provider's ITenantResolver used to do its own SELECT *and*
        // UPDATE on every gRPC export against the one api_keys row a whole tenant's agents share.
        // Now every provider registers only the raw IApiKeyLookup / IApiKeyTouchStore this wraps
        // (via the host's Add<Provider>CollectorServices call). Bound from Telemetry:TenantResolution.
        services.AddOptions<TenantResolutionOptions>()
            .Bind(configuration.GetSection(TenantResolutionOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        services.AddMemoryCache();
        services.AddSingleton<ControlPlaneHealth>();
        services.AddSingleton<ApiKeyLookupCoordinator>();
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, ConnectionAgeStartupFilter>();
        services.AddSingleton<AuthFailureLimiter>();
        services.AddSingleton<ApiKeyTouchTracker>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITenantResolver, CachingTenantResolver>();
        services.AddHostedService<ApiKeyTouchWorker>();

        // Authentication: the API key check runs in the pipeline, before the protobuf body is read. No
        // default scheme is set here and the collector policy names its scheme explicitly; the handler
        // acts only on endpoints carrying CollectorEndpointMetadata (see ApiKeyAuthenticationHandler).
        services.AddAuthentication()
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(TelemetryAuthenticationSchemes.ApiKey, null);
        services.AddAuthorization(o => o.AddPolicy(CollectorAuthorization.CollectorPolicy, p => p
            .AddAuthenticationSchemes(TelemetryAuthenticationSchemes.ApiKey)
            .RequireAuthenticatedUser()
            .RequireClaim(TelemetryClaimTypes.TenantId)));

        // Refuses plaintext transport outside Development (decision 3). Bound from Telemetry:Collector.
        services.AddOptions<TelemetryCollectorOptions>()
            .Bind(configuration.GetSection(TelemetryCollectorOptions.SectionName))
            .Validate(o => { o.AuthFailureLimit.Validate(); return true; })
            .ValidateOnStart();
        services.AddHostedService<PlaintextTransportGuard>();

        // Health: the same readiness check answers gRPC grpc.health.v1 on the OTLP endpoints (Kubernetes gRPC probes,
        // gRPC-aware balancers) and HTTP /healthz/ready on the management endpoint. Liveness is "the process answers".
        services.AddHealthChecks().AddCheck<CollectorReadinessCheck>("ready", tags: ["ready"]);
        services.AddGrpcHealthChecks().AddCheck<CollectorReadinessCheck>("collector");
        // The gRPC health service reports what the publisher last saw; the defaults (30 s period) are too slow to steer a balancer.
        services.Configure<HealthCheckPublisherOptions>(o => { o.Delay = TimeSpan.FromSeconds(1); o.Period = TimeSpan.FromSeconds(5); });

        // metric_last_seen maintenance (list-pages-server-side plan, Phase 5, decision 27):
        // registered unconditionally on every provider, same shape as ApiKeyTouchWorker above —
        // ClickHouse opts out via a no-op IMetricTouchStore (it has no such table),
        // not by this worker knowing which provider is active. Bound from
        // Telemetry:MetricTouch.
        services.Configure<MetricTouchOptions>(configuration.GetSection(MetricTouchOptions.SectionName));
        services.AddSingleton<MetricTouchTracker>();
        services.AddHostedService<MetricTouchWorker>();

        // Summary rollups (plans/summary-rollups.md): registered BEFORE the ingestion worker so the
        // host stops it after the ingestion drain and its final flush includes everything drained.
        // Providers register IRollupStore (ClickHouse's is a no-op: its ingestion worker writes
        // the rollup tables). Bound from Telemetry:Rollup.
        services.Configure<RollupOptions>(configuration.GetSection(RollupOptions.SectionName));
        services.AddSingleton<RollupAccumulator>();
        services.AddHostedService<RollupWorker>();

        // Write path: the generic worker drains the ingestion channel and delegates each
        // batch flush to the active provider's ITelemetryBulkWriter. The host is responsible
        // for registering that provider (ITelemetryBulkWriter, IApiKeyLookup,
        // IApiKeyTouchStore) via the matching Add<Provider>CollectorServices call.
        services.AddHostedService<TelemetryIngestionWorker>();

        services
            .AddScoped<ILogWriteRepository, LogWriteRepository>()
            .AddScoped<IMetricWriteRepository, MetricWriteRepository>()
            .AddScoped<ITraceWriteRepository, TraceWriteRepository>();

        // Conversion and enqueueing, shared by the gRPC services and the HTTP endpoints.
        services
            .AddScoped<OtlpLogIngestor>()
            .AddScoped<OtlpMetricIngestor>()
            .AddScoped<OtlpTraceIngestor>();

        return services;
    }
}
