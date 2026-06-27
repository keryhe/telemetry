using Keryhe.Telemetry.Api.Middleware;
using Keryhe.Telemetry.Api.Services;
using Keryhe.Telemetry.Core;

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

// ── REPOSITORIES ─────────────────────────────────────────────────────────────
// Read path: the active provider's Dapper read/alert repositories are selected by the
// Database:Provider config key (connection string comes from ConnectionStrings:Read).
switch (builder.Configuration["Database:Provider"])
{
    case "SqlServer":  builder.Services.AddSqlServerReadServices(builder.Configuration);  break;
    case "PostgreSQL": builder.Services.AddPostgreSqlReadServices(builder.Configuration); break;
    case "Timescale":  builder.Services.AddTimescaleReadServices(builder.Configuration);  break;
    default: throw new InvalidOperationException("Unknown or missing Database:Provider (expected SqlServer, PostgreSQL, or Timescale).");
}

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
