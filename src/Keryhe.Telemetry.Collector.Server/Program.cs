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

        // Registers gRPC, the ingestion channel + worker, the write repositories, and the
        // active provider's write services (Database:Provider + ConnectionStrings:Write).
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

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

        // Map gRPC services
        app.MapKeryheTelemetryCollector();

        app.MapGet("/",
            () =>
                "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

        app.Run();
    }
}
