using Keryhe.Telemetry.Api.Middleware;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Data.Access;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── CORS ──────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader());
});

// ── CONTROLLERS + SWAGGER ─────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── TENANT CONTEXT ────────────────────────────────────────────────────────────
// Scoped: one ApiTenantContext per request. TenantMiddleware calls SetTenantId
// when the Angular app sends X-Tenant-Id; otherwise defaults to tenant 1.
builder.Services.AddScoped<ApiTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<ApiTenantContext>());

// ── EF CORE: READ CONTEXT FACTORY ────────────────────────────────────────────
// Scoped factory so each CreateDbContextAsync() call uses the current request's
// ITenantContext (already populated by TenantMiddleware).
builder.Services.AddDbContextFactory<TelemetryReadDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Read");
    options.UseSqlServer(connectionString);
    options.UseSnakeCaseNamingConvention();
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
}, ServiceLifetime.Scoped);

// ── EF CORE: ALERT CONTEXT ───────────────────────────────────────────────────
builder.Services.AddDbContext<AlertDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Read");
    options.UseSqlServer(connectionString);
    options.UseSnakeCaseNamingConvention();
});

// ── REPOSITORIES ─────────────────────────────────────────────────────────────
builder.Services
    .AddScoped<ITraceReadRepository, TraceReadRepository>()
    .AddScoped<IMetricReadRepository, MetricReadRepository>()
    .AddScoped<ILogReadRepository, LogReadRepository>()
    .AddScoped<IAlertRuleRepository, AlertRuleRepository>();

var app = builder.Build();

// ── MIDDLEWARE ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// CORS before tenant middleware so OPTIONS preflight requests pass through.
app.UseCors("Angular");

app.UseMiddleware<TenantMiddleware>();

app.MapControllers();

app.Run();
