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
// Registers the API controllers (via application part), tenant context, and the
// active provider's read services (Database:Provider + ConnectionStrings:Read).
builder.Services.AddKeryheTelemetryApi(builder.Configuration);

// ── ALERTING ──────────────────────────────────────────────────────────────────
// Registers alert evaluation and the periodic background worker that drives it.
// Depends on the read repositories and tenant context registered above.
builder.Services.AddAlerting(builder.Configuration);

var app = builder.Build();

// ── MIDDLEWARE ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// ── ANGULAR CLIENT ────────────────────────────────────────────────────────────
// Serves the compiled SPA from wwwroot (populated at publish time from
// src/telemetry-client). Placed before the tenant middleware so asset requests
// skip scoped tenant resolution entirely. In development wwwroot is empty and
// these are no-ops — the UI runs on the Angular dev server instead.
app.UseDefaultFiles();
app.UseStaticFiles();

// CORS before tenant middleware so OPTIONS preflight requests pass through.
app.UseCors("Angular");

app.UseKeryheTelemetryApi();

app.MapControllers();

// Anything not matched by an api/* controller or a real file is an Angular
// client-side route (/traces/:id, /metrics/:name, ...) — serve the SPA shell so
// deep links and hard reloads work.
app.MapFallbackToFile("index.html");

app.Run();
