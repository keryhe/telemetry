namespace Keryhe.Telemetry.Server;

/// <summary>
/// All-in-one host: gRPC OTLP ingestion (write path), the REST API (read path), and the
/// compiled Angular client, in a single process. The split hosts
/// (Keryhe.Telemetry.Collector.Server / Keryhe.Telemetry.Api.Server) remain available for
/// scale-out deployments.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        // ContentRootPath = BaseDirectory so appsettings.json and wwwroot resolve next to the
        // executable, which is what Windows-service hosting requires.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Host.UseWindowsService();

        EnsureSingleNpgsqlDataSource(builder.Configuration);

        // ── CORS ──────────────────────────────────────────────────────────────────
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:4201"];

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("Angular", policy =>
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader());
        });

        // ── OPENAPI ───────────────────────────────────────────────────────────────
        builder.Services.AddOpenApi();

        // ── TELEMETRY COLLECTOR (write path) ──────────────────────────────────────
        // gRPC, the bounded ingestion channel, the background worker that drains it, and
        // the active provider's write services (Database:Provider + ConnectionStrings:Write).
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

        // ── TELEMETRY API (read path) ─────────────────────────────────────────────
        // API controllers (via application part), tenant context, and the active
        // provider's read services (Database:Provider + ConnectionStrings:Read).
        builder.Services.AddKeryheTelemetryApi(builder.Configuration);

        // ── ALERTING ──────────────────────────────────────────────────────────────
        // Alert evaluation plus the periodic background worker that drives it.
        // Depends on the read repositories and tenant context registered above.
        builder.Services.AddAlerting(builder.Configuration);

        var app = builder.Build();

        // ── MIDDLEWARE ────────────────────────────────────────────────────────────
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        // No UseHttpsRedirection() here (unlike Keryhe.Telemetry.Api.Server): it would
        // redirect the plaintext h2c ingestion endpoint and break OTLP exporters.

        // ── ANGULAR CLIENT ────────────────────────────────────────────────────────
        // Serves the compiled SPA from wwwroot (populated at publish time from
        // src/telemetry-client). Placed before the tenant middleware so asset requests
        // skip scoped tenant resolution entirely. In development wwwroot is empty and
        // these are no-ops — the UI runs on the Angular dev server instead.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseRouting();

        // CORS before tenant middleware so OPTIONS preflight requests pass through.
        app.UseCors("Angular");

        app.UseKeryheTelemetryApi();

        // gRPC ingestion. Concrete route matches, so the SPA fallback below never
        // shadows them.
        app.MapKeryheTelemetryCollector();

        app.MapControllers();

        // Anything not matched by an api/* controller, a gRPC service, or a real file is
        // an Angular client-side route (/traces/:id, /metrics/:name, ...) — serve the SPA
        // shell so deep links and hard reloads work.
        app.MapFallbackToFile("index.html");

        app.Run();
    }

    /// <summary>
    /// The Npgsql-backed providers register a singleton <c>NpgsqlDataSource</c> from
    /// <c>ConnectionStrings:Write</c> (write services) and again from
    /// <c>ConnectionStrings:Read</c> (read services). In this combined host both
    /// registrations land in one container and the last one silently wins for both paths,
    /// so differing connection strings would point half the app at the wrong database with
    /// no error. Fail fast instead. SqlServer/ClickHouse/MySql read their connection string
    /// per class and are unaffected.
    /// </summary>
    private static void EnsureSingleNpgsqlDataSource(IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"];
        if (provider is not ("PostgreSQL" or "Timescale"))
        {
            return;
        }

        var read = configuration.GetConnectionString("Read");
        var write = configuration.GetConnectionString("Write");

        if (!string.IsNullOrWhiteSpace(read) &&
            !string.IsNullOrWhiteSpace(write) &&
            !string.Equals(read, write, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database:Provider '{provider}' registers a single shared NpgsqlDataSource in this " +
                "all-in-one host, so ConnectionStrings:Read and ConnectionStrings:Write must be " +
                "identical (they may differ only when using the split Collector.Server / Api.Server " +
                "hosts). Set both to the same value, or run the split hosts instead.");
        }
    }
}
