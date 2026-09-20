var builder = WebApplication.CreateBuilder(args);

// ── CORS ──────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4201"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// ── OPENAPI ───────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── TELEMETRY API ─────────────────────────────────────────────────────────────
// Registers the API controllers (via application part) and tenant context.
builder.Services.AddKeryheTelemetryApi(builder.Configuration);

// The active provider's read services (Database:Provider + ConnectionStrings:Api).
switch (builder.Configuration["Database:Provider"])
{
    case "SqlServer":  builder.Services.AddSqlServerApiServices(builder.Configuration);  break;
    case "PostgreSQL": builder.Services.AddPostgreSqlApiServices(builder.Configuration); break;
    case "Timescale":  builder.Services.AddTimescaleApiServices(builder.Configuration);  break;
    case "ClickHouse": builder.Services.AddClickHouseApiServices(builder.Configuration); break;
    case "MySql":      builder.Services.AddMySqlApiServices(builder.Configuration);      break;
    default: throw new InvalidOperationException(
        "Unknown or missing Database:Provider (expected SqlServer, PostgreSQL, Timescale, ClickHouse, or MySql).");
}

// ── ALERTING ──────────────────────────────────────────────────────────────────
// Registers alert evaluation and the periodic background worker that drives it.
// Depends on the read repositories and tenant context registered above.
builder.Services.AddAlerting(builder.Configuration);

// ── RETENTION ─────────────────────────────────────────────────────────────────
// Registers the periodic background worker that sweeps old telemetry per the
// DB-backed retention_settings row. Depends on IRetentionSettingsRepository,
// registered above by AddKeryheTelemetryApi.
builder.Services.AddRetention(builder.Configuration);

var app = builder.Build();

// ── MIDDLEWARE ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ── ANGULAR CLIENT ────────────────────────────────────────────────────────────
// Serves the prebuilt SPA from the referenced Keryhe.Telemetry.Ui package's static web assets, at
// "/" rather than the RCL default of /_content/Keryhe.Telemetry.Ui/. Placed before the tenant
// middleware so asset requests — including GET /config.json, answered here with this host's API
// location — skip scoped tenant resolution entirely. Static web assets flow through a plain
// ProjectReference/PackageReference at build time, not only at publish, so unlike the old
// per-host BuildAngularClient/IncludeAngularClient targets this also serves the UI under
// `dotnet run` — see plans/ui-packaging-runtime-config.md Decision 7. The Angular dev server
// (`npm start`, proxying /api to this host) remains the tool for UI development; this is what a
// consumer following the README will actually run.
app.UseKeryheTelemetryUi();

// Explicit, rather than relying on WebApplication's implicit UseRouting() insertion: with no
// explicit call, that insertion lands ahead of the middleware above, so routing pre-selects an
// endpoint for "/" (MapKeryheTelemetryUiFallback's route pattern matches any extensionless path,
// "/" included) before UseKeryheTelemetryUi's own static-file middleware ever runs — and static
// file middleware, on seeing an endpoint already selected, defers to it rather than serving,
// silently discarding this method's compression negotiation for exactly the one route ("/") that
// most needed it. Placed here so UseKeryheTelemetryUi's static files still run first and
// short-circuit whatever they can actually serve, matching Keryhe.Telemetry.Server's Program.cs,
// which already calls this explicitly in the same position.
app.UseRouting();

// CORS before tenant middleware so OPTIONS preflight requests pass through.
app.UseCors("Angular");

app.UseKeryheTelemetryApi();

app.MapControllers();

// Anything not matched by an api/* controller or a real file is an Angular
// client-side route (/traces/:id, /metrics/:name, ...) — serve the SPA shell so
// deep links and hard reloads work.
app.MapKeryheTelemetryUiFallback();

app.Run();
