using System.Text.Json.Serialization;
using Keryhe.Telemetry.Api;
using Keryhe.Telemetry.Api.Authorization;
using Keryhe.Telemetry.Api.Routing;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry API (controllers, tenant
/// context, and the active provider's read services) into a host application.
/// </summary>
public static class TelemetryApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the telemetry API controllers and services. The host is responsible
    /// for CORS, Swagger, HTTPS redirection, authentication/authorization middleware, and calling
    /// <c>MapKeryheTelemetryApi()</c>.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration">
    /// Host configuration. This does not register a database provider — the host must also
    /// call the active provider's <c>Add&lt;Provider&gt;ApiServices(configuration)</c> (e.g.
    /// <c>AddPostgreSqlApiServices</c>), which supplies the Dapper read repositories and the retention
    /// sweeper (connection string from <c>ConnectionStrings:Api</c>), and the control plane's
    /// <c>Add&lt;Provider&gt;ControlPlaneApiServices(configuration)</c>, which supplies the alert-rule,
    /// tenant-catalog and retention-settings repositories (<c>ConnectionStrings:ControlPlane</c>).
    /// </param>
    /// <param name="configure">Optional overrides applied after binding the <c>Telemetry:Api</c> section.</param>
    public static IServiceCollection AddKeryheTelemetryApi(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TelemetryApiOptions>? configure = null)
    {
        // Base path and authorization settings (Telemetry:Api). An invalid base path fails startup.
        var apiOptions = services.AddOptions<TelemetryApiOptions>()
            .Bind(configuration.GetSection(TelemetryApiOptions.SectionName));
        if (configure is not null) apiOptions.Configure(configure);
        apiOptions
            .Validate(o =>
            {
                try { TelemetryApiOptions.NormalizeBasePath(o.BasePath); return true; }
                catch (InvalidOperationException) { return false; }
            }, $"{TelemetryApiOptions.SectionName}:BasePath is not a usable API path prefix (use e.g. '/api' or '/telemetry/api'; '/' is not allowed).")
            .ValidateOnStart();

        // Prefix the library's routes with the base path (and tenants/{tenantId}) and add the
        // authorization filter; applies to this assembly's controllers only.
        services.AddOptions<MvcOptions>().Configure<IOptions<TelemetryApiOptions>>((mvc, o) =>
            mvc.Conventions.Add(new TelemetryApiConvention(o.Value.NormalizedBasePath)));
        services.AddAuthorization();
        services.AddMemoryCache();
        services.AddScoped<TelemetryAuthorizationFilter>();
        services.AddScoped<TelemetryPolicyResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, PolicyMappedOperationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, ClaimMappedTenantAccessHandler>());
        services.AddHostedService<TelemetryApiStartupValidator>();

        // Controllers live in this class library, so the host will not discover them
        // unless this assembly is registered as an MVC application part.
        // WhenWritingNull: MetricDataPoint is effectively a union across five metric types, so most
        // of its properties are null on any given row — omitting them was ~3.8 MB of an 18.4 MB
        // one-hour histogram response (metric-detail-performance plan §5.2). Every JSON consumer in
        // the Angular client already treats these fields as optional (?? / == null), so the
        // resulting undefined-vs-null difference is safe.
        services.AddControllers()
            .AddApplicationPart(typeof(TelemetryApiServiceCollectionExtensions).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        // Scoped: one ApiTenantContext per request; TelemetryAuthorizationFilter sets the tenant id from the route.
        services.AddScoped<ApiTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ApiTenantContext>());

        // TraceQueryCache (the list-page-scale plan's short-TTL memo of the trace read
        // repositories' span scan) was retired in the list-pages-server-side plan's Phase 3: the
        // traces list page's summary/page/samples endpoints are SQL-aggregated over trace anchors
        // rather than an in-memory full-window scan, so there is no longer a per-request scan worth
        // memoizing.

        // Query timeout / raw-search-window override settings (list-pages-server-side plan, Phase
        // 1) — bound here so phases 2/3's summary endpoints and ProviderCapabilities.FromConfiguration
        // (read directly from IConfiguration at provider registration time, not through this
        // IOptions) agree on the same Telemetry:Query section.
        services.Configure<QueryOptions>(configuration.GetSection(QueryOptions.SectionName));
        services.Configure<ExportOptions>(configuration.GetSection(ExportOptions.SectionName));

        // Summary rollups (plans/summary-rollups.md): the API reads FlushIntervalSeconds, CloseGraceSeconds and
        // ArrivalMarginSeconds to compute writtenThrough, so these must agree with the collector's section.
        services.Configure<RollupOptions>(configuration.GetSection(RollupOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        // Phase 8: caps concurrent /api/*/export streams per API instance (decision 17) — one gate
        // shared across logs/traces/metrics exports, not per-signal.
        services.AddSingleton<ExportConcurrencyGate>();

        return services;
    }
}
