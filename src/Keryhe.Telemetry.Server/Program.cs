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
        // gRPC, the bounded ingestion channel, and the background worker that drains it.
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

        // ── TELEMETRY API (read path) ─────────────────────────────────────────────
        // API controllers (via application part) and tenant context.
        builder.Services.AddKeryheTelemetryApi(builder.Configuration);

        // ── DATABASE PROVIDER ─────────────────────────────────────────────────────
        // The active provider's write services (ConnectionStrings:Collector) and read
        // services (ConnectionStrings:Api), both selected by Database:Provider.
        switch (builder.Configuration["Database:Provider"])
        {
            case "SqlServer":
                builder.Services.AddSqlServerCollectorServices(builder.Configuration);
                builder.Services.AddSqlServerApiServices(builder.Configuration);
                break;
            case "PostgreSQL":
                builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);
                builder.Services.AddPostgreSqlApiServices(builder.Configuration);
                break;
            case "Timescale":
                builder.Services.AddTimescaleCollectorServices(builder.Configuration);
                builder.Services.AddTimescaleApiServices(builder.Configuration);
                break;
            case "ClickHouse":
                builder.Services.AddClickHouseCollectorServices(builder.Configuration);
                builder.Services.AddClickHouseApiServices(builder.Configuration);
                break;
            case "MySql":
                builder.Services.AddMySqlCollectorServices(builder.Configuration);
                builder.Services.AddMySqlApiServices(builder.Configuration);
                break;
            default:
                throw new InvalidOperationException(
                    "Unknown or missing Database:Provider (expected SqlServer, PostgreSQL, Timescale, ClickHouse, or MySql).");
        }

        // ── ALERTING ──────────────────────────────────────────────────────────────
        // Alert evaluation plus the periodic background worker that drives it.
        // Depends on the read repositories and tenant context registered above.
        builder.Services.AddAlerting(builder.Configuration);

        // ── RETENTION ─────────────────────────────────────────────────────────────
        // Periodic background worker that sweeps old telemetry per the DB-backed
        // retention_settings row. Depends on IRetentionSettingsRepository, registered
        // above by AddKeryheTelemetryApi.
        builder.Services.AddRetention(builder.Configuration);

        var app = builder.Build();

        // ── MIDDLEWARE ────────────────────────────────────────────────────────────
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        // No UseHttpsRedirection() here (unlike Keryhe.Telemetry.Api.Server): it would
        // redirect the plaintext h2c ingestion endpoint and break OTLP exporters.

        // ── ANGULAR CLIENT ────────────────────────────────────────────────────────
        // Serves the prebuilt SPA from the referenced Keryhe.Telemetry.Ui package's static web
        // assets, at "/" rather than the RCL default of /_content/Keryhe.Telemetry.Ui/. Placed
        // before the tenant middleware so asset requests — including GET /config.json, answered
        // here with this host's API location — skip scoped tenant resolution entirely. Static web
        // assets flow through a plain ProjectReference/PackageReference at build time, not only at
        // publish, so unlike the old per-host BuildAngularClient/IncludeAngularClient targets this
        // also serves the UI under `dotnet run` — see plans/ui-packaging-runtime-config.md
        // Decision 7. The Angular dev server (`npm start`, proxying /api to this host) remains the
        // tool for UI development; this is what a consumer following the README will actually run.
        app.UseKeryheTelemetryUi();

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
        app.MapKeryheTelemetryUiFallback();

        app.Run();
    }

    /// <summary>
    /// The Npgsql-backed providers register a singleton <c>NpgsqlDataSource</c> from
    /// <c>ConnectionStrings:Collector</c> (write services) and again from
    /// <c>ConnectionStrings:Api</c> (read services). In this combined host both
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

        var api = configuration.GetConnectionString("Api");
        var collector = configuration.GetConnectionString("Collector");

        if (!string.IsNullOrWhiteSpace(api) &&
            !string.IsNullOrWhiteSpace(collector) &&
            !string.Equals(api, collector, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database:Provider '{provider}' registers a single shared NpgsqlDataSource in this " +
                "all-in-one host, so ConnectionStrings:Api and ConnectionStrings:Collector must be " +
                "identical (they may differ only when using the split Collector.Server / Api.Server " +
                "hosts). Set both to the same value, or run the split hosts instead.");
        }
    }
}
