using System.Text.Json.Serialization;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods that register the Keryhe Telemetry API (controllers, tenant
/// context, and the active provider's read services) into a host application.
/// </summary>
public static class TelemetryApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the telemetry API controllers and services. The host is responsible
    /// for CORS, Swagger, HTTPS redirection, and calling <c>MapControllers()</c>.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration">
    /// Host configuration. This does not register a database provider — the host must also
    /// call the active provider's <c>Add&lt;Provider&gt;ApiServices(configuration)</c> (e.g.
    /// <c>AddPostgreSqlApiServices</c>), which supplies the Dapper read/alert repositories
    /// (connection string comes from <c>ConnectionStrings:Api</c>).
    /// </param>
    public static IServiceCollection AddKeryheTelemetryApi(this IServiceCollection services, IConfiguration configuration)
    {
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

        // Scoped: one ApiTenantContext per request; TenantMiddleware sets the tenant id.
        services.AddScoped<ApiTenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ApiTenantContext>());

        // TraceQueryCache (the list-page-scale plan's short-TTL memo of the trace read
        // repositories' span scan) was retired in the list-pages-server-side plan's Phase 3: the
        // traces list page's summary/page/samples endpoints are SQL-aggregated (rollup tables plus
        // an anchor-bounded raw path) rather than an in-memory full-window scan, so there is no
        // longer a per-request scan worth memoizing.

        // Query timeout / raw-search-window override settings (list-pages-server-side plan, Phase
        // 1) — bound here so phases 2/3's summary endpoints and ProviderCapabilities.FromConfiguration
        // (read directly from IConfiguration at provider registration time, not through this
        // IOptions) agree on the same Telemetry:Query section.
        services.Configure<QueryOptions>(configuration.GetSection(QueryOptions.SectionName));
        services.Configure<ExportOptions>(configuration.GetSection(ExportOptions.SectionName));

        // Phase 8: caps concurrent /api/*/export streams per API instance (decision 17) — one gate
        // shared across logs/traces/metrics exports, not per-signal.
        services.AddSingleton<ExportConcurrencyGate>();

        return services;
    }
}
