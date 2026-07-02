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

app.UseHttpsRedirection();

// CORS before tenant middleware so OPTIONS preflight requests pass through.
app.UseCors("Angular");

app.UseKeryheTelemetryApi();

app.MapControllers();

app.Run();
