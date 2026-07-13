using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Collector.Services;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Collector.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Host.UseWindowsService();

        // Add services to the container.

        builder.Services.AddGrpc();
        builder.Services.AddLogging();

        // Singletons shared across all gRPC requests and the background worker.
        builder.Services.AddSingleton<TelemetryIngestionChannel>();
        builder.Services.AddSingleton<ResourceScopeCache>();

        // Write path: the generic worker drains the ingestion channel and delegates each
        // batch flush to the active provider's ITelemetryBulkWriter. The provider — and with
        // it ITelemetryBulkWriter, ITenantResolver, and ITelemetryWriteStore — is selected by
        // the Database:Provider config key.
        switch (builder.Configuration["Database:Provider"])
        {
            case "SqlServer":  builder.Services.AddSqlServerWriteServices(builder.Configuration);  break;
            case "PostgreSQL": builder.Services.AddPostgreSqlWriteServices(builder.Configuration); break;
            case "Timescale":  builder.Services.AddTimescaleWriteServices(builder.Configuration);  break;
            case "ClickHouse": builder.Services.AddClickHouseWriteServices(builder.Configuration); break;
            default: throw new InvalidOperationException("Unknown or missing Database:Provider (expected SqlServer, PostgreSQL, Timescale, or ClickHouse).");
        }

        builder.Services.AddHostedService<TelemetryIngestionWorker>();

        builder.Services
            .AddScoped<ILogWriteRepository, LogWriteRepository>()
            .AddScoped<IMetricWriteRepository, MetricWriteRepository>()
            .AddScoped<ITraceWriteRepository, TraceWriteRepository>();
        
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
        // Configure the HTTP request pipeline
        app.UseCors();
        app.UseRouting();

        // Map gRPC services
        app.MapGrpcService<LogService>();
        app.MapGrpcService<TraceService>();
        app.MapGrpcService<MetricService>();
        

        app.MapGet("/",
            () =>
                "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

        app.Run();
    }
}