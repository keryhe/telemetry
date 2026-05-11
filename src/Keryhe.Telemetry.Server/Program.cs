using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Server.Services;
using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Core;
using Npgsql;
using Keryhe.Telemetry.Timescale.Services;

namespace Keryhe.Telemetry.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddGrpc();
        builder.Services.AddLogging();

        var writeConnectionString = builder.Configuration.GetConnectionString("Write")!;

        // Raw NpgsqlDataSource for the background ingestion worker (bulk inserts).
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(writeConnectionString));

        // DbContextPool reuses DbContext instances across requests (delete operations only).
        builder.Services.AddDbContextPool<TelemetryWriteDbContext>(options =>
        {
            options.UseNpgsql(writeConnectionString);
            options.UseSnakeCaseNamingConvention();

            if (builder.Environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        // Singletons shared across all gRPC requests and the background worker.
        builder.Services.AddSingleton<TelemetryIngestionChannel>();
        builder.Services.AddSingleton<ResourceScopeCache>();
        builder.Services.AddHostedService<TelemetryIngestionWorker>();

        builder.Services
            .AddScoped<ITenantResolver, ApiKeyTenantResolver>()
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