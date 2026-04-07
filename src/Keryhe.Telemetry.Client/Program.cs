using Keryhe.Telemetry.Client.Components;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Data.Access;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add read DbContext — the Client only reads telemetry data
builder.Services.AddDbContext<TelemetryReadDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Read");
    options.UseNpgsql(connectionString, dbOptions =>
    {
                
    });
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            
    // Enable sensitive data logging in development
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services
    .AddScoped<ILogReadRepository, LogReadRepository>()
    .AddScoped<IMetricReadRepository, MetricReadRepository>()
    .AddScoped<ITraceReadRepository, TraceReadRepository>()
    .AddScoped<ILogService, LogService>()
    .AddScoped<IMetricService, MetricService>()
    .AddScoped<ITraceService, TraceService>()
    .AddScoped<TimeRangeState>()
    .AddScoped<MetricsPageState>()
    .AddScoped<MetricDetailPageState>()
    .AddScoped<ServiceMetricsPageState>()
    .AddScoped<LogsPageState>()
    .AddScoped<TracesPageState>()
    .AddScoped<DashboardPageState>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();