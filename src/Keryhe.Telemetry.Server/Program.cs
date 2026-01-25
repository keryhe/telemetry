using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Server.Services;
using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Server;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddGrpc();
        builder.Services.AddLogging();
        
        // Add Entity Framework
        builder.Services.AddDbContext<OpenTelemetryDbContext>(options =>
        {
            var connectionString = builder.Configuration.GetConnectionString("Default");
            options.UseSqlServer(connectionString, dbOptions =>
            {
            });
            
            // Enable sensitive data logging in development
            if (builder.Environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        });

        builder.Services
            .AddScoped<ILogRepository, LogRepository>()
            .AddScoped<IMetricRepository, MetricRepository>()
            .AddScoped<ITraceRepository, TraceRepository>();
        
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