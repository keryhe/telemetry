using Keryhe.Telemetry.Alerting;
using Keryhe.Telemetry.Alerting.Evaluators;
using Keryhe.Telemetry.Alerting.Notifications;
using Keryhe.Telemetry.Client.Components;
using Keryhe.Telemetry.Client.Services;
using Keryhe.Telemetry.Client.Services.State;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;
using Keryhe.Telemetry.Data.Access;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add read DbContext factory — using a factory ensures each repository method gets its
// own short-lived context instance, preventing concurrent-operation errors in Blazor Server.
builder.Services.AddDbContextFactory<TelemetryReadDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Read");
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

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
    .AddScoped<LocalStorageService>()
    .AddScoped<TimeRangeState>()
    .AddScoped<MetricsPageState>()
    .AddScoped<MetricDetailPageState>()
    .AddScoped<ServiceMetricsPageState>()
    .AddScoped<LogsPageState>()
    .AddScoped<TracesPageState>()
    .AddScoped<DashboardPageState>();

// Alert feature
builder.Services.AddDbContext<AlertDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Read");
    options.UseNpgsql(connectionString);
});
builder.Services.AddHttpClient("AlertWebhook");
builder.Services
    .AddScoped<IAlertRuleRepository, AlertRuleRepository>()
    .AddScoped<INotificationChannel, WebhookNotificationChannel>()
    .AddScoped<IAlertEvaluator, MetricThresholdEvaluator>()
    .AddScoped<IAlertEvaluator, ErrorRateEvaluator>()
    .AddScoped<IAlertEvaluator, SlowTraceEvaluator>()
    .AddScoped<IAlertEvaluator, LogSeveritySpikeEvaluator>()
    .AddScoped<IAlertService, AlertService>();
builder.Services.AddHostedService<AlertEvaluationWorker>();

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