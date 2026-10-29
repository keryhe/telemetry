using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Collector.Server;

public class Program
{
    /// <summary>The fixed tenant of a fixed-tenant collector, or null in API-key mode.</summary>
    private static long? FixedTenantId(IConfiguration configuration)
    {
        var auth = configuration.GetSection(CollectorAuthenticationOptions.SectionName).Get<CollectorAuthenticationOptions>();
        return auth?.Mode == CollectorAuthenticationMode.FixedTenant ? auth.FixedTenantId : null;
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Host.UseWindowsService();

        // Add services to the container.

        // Registers gRPC, the ingestion channel + worker, and the write repositories.
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

        // The active provider's write services (Telemetry:Collector:Provider:Database + ConnectionStrings:Collector).
        switch (builder.Configuration[ProviderKeys.CollectorDatabase])
        {
            case "SqlServer":  builder.Services.AddSqlServerCollectorServices(builder.Configuration);  break;
            case "PostgreSQL": builder.Services.AddPostgreSqlCollectorServices(builder.Configuration); break;
            case "ClickHouse": builder.Services.AddClickHouseCollectorServices(builder.Configuration); break;
            case "MySql":      builder.Services.AddMySqlCollectorServices(builder.Configuration);      break;
            case "Parquet":    builder.Services.AddParquetCollectorServices(builder.Configuration);    break;
            default: throw new InvalidOperationException(
                $"Unknown or missing {ProviderKeys.CollectorDatabase} (expected SqlServer, PostgreSQL, ClickHouse, MySql, or Parquet).");
        }

        // The control plane (tenants and API keys): Telemetry:Collector:Provider:ControlPlane + ConnectionStrings:ControlPlane, both
        // required. It may be a different provider (and database) from the telemetry data above.
        switch (builder.Configuration[ProviderKeys.CollectorControlPlane])
        {
            case "SqlServer":  builder.Services.AddSqlServerControlPlaneCollectorServices(builder.Configuration);  break;
            case "PostgreSQL": builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration); break;
            case "MySql":      builder.Services.AddMySqlControlPlaneCollectorServices(builder.Configuration);      break;
            // A file either host creates and migrates at startup (Telemetry:ControlPlane:Sqlite:ApplySchema); a fixed-tenant
            // collector also creates its tenant there when it has no row.
            case "Sqlite":     builder.Services.AddSqliteApiKeyLookup(builder.Configuration, FixedTenantId(builder.Configuration)); break;
            default: throw new InvalidOperationException(
                $"Unknown or missing {ProviderKeys.CollectorControlPlane} (expected SqlServer, PostgreSQL, MySql, or Sqlite).");
        }

        // Add CORS for web clients if needed
        builder.Services.AddCors(o => o.AddPolicy("AllowAll", builder =>
        {
            builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders("Grpc-Status", "Grpc-Message", "Grpc-Encoding", "Grpc-Accept-Encoding");
        }));

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        app.UseCors();
        app.UseRouting();

        // API-key (or fixed-tenant) authentication for the OTLP endpoints (MapKeryheTelemetryCollector requires the collector policy).
        app.UseAuthentication();
        app.UseAuthorization();

        // Map gRPC services
        app.MapKeryheTelemetryCollector();

        app.MapGet("/",
            () =>
                "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

        app.Run();
    }
}
