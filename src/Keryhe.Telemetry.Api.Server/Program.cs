using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

// ── RESPONSE COMPRESSION ──────────────────────────────────────────────────────
// Nothing compressed API responses before this (metric-detail-performance plan §5.1); this JSON is
// highly repetitive and compresses roughly 10-20x. EnableForHttps is safe here: BREACH needs a
// secret plus attacker-controlled input reflected in the same response, and these responses carry
// neither (revisit if the API ever returns CSRF tokens or per-user secrets in a compressible body).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
});

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

// ── TELEMETRY UI ──────────────────────────────────────────────────────────────
// Binds the TelemetryUi section (BasePath, ApiBasePath, BrandName, BrandTagline) and
// registers the in-memory SPA shell. Required by UseKeryheTelemetryUi() below; keeping
// the UI's settings in configuration is what lets a deployment relocate or rebrand the
// prebuilt bundle without recompiling anything.
builder.Services.AddKeryheTelemetryUi(builder.Configuration);

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
// endpoint before UseKeryheTelemetryUi's own static-file middleware ever runs — and static file
// middleware, on seeing an endpoint already selected, defers to it rather than serving. The SPA
// shell no longer depends on this (UseKeryheTelemetryUi writes it from memory, ahead of routing
// and without consulting the selected endpoint), but every other asset it serves still does, and
// the implicit insertion would also silently move UseCors and the tenant middleware to the wrong
// side of routing. Placed here so UseKeryheTelemetryUi's static files still run first and
// short-circuit whatever they can actually serve, matching Keryhe.Telemetry.Server's Program.cs,
// which already calls this explicitly in the same position.
app.UseRouting();

// Only ever sees API responses and the SPA fallback — UseKeryheTelemetryUi() above negotiates
// .br/.gz for the packaged SPA itself and short-circuits before this middleware runs.
app.UseResponseCompression();

// CORS before tenant middleware so OPTIONS preflight requests pass through.
app.UseCors("Angular");

app.UseKeryheTelemetryApi();

app.MapControllers();

// Anything not matched by an api/* controller or a real file is an Angular
// client-side route (/traces/:id, /metrics/:name, ...) — serve the SPA shell so
// deep links and hard reloads work.
app.MapKeryheTelemetryUiFallback();

app.Run();
