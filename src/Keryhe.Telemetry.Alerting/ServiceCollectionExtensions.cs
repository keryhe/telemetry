using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Alerting;
using Keryhe.Telemetry.Alerting.Evaluators;
using Keryhe.Telemetry.Alerting.Notifications;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registration for the alerting subsystem. <see cref="AddAlerting"/> wires up the evaluation
/// service, the four rule evaluators, the webhook notification channel, and the periodic
/// <see cref="AlertEvaluationWorker"/> hosted service.
///
/// The caller must have already registered the provider read repositories
/// (<c>ITraceReadRepository</c>, <c>IMetricReadRepository</c>, <c>ILogReadRepository</c>,
/// <c>IAlertRuleRepository</c>) and an <c>ITenantContext</c> — in the API host these come from
/// <c>AddKeryheTelemetryApi</c>.
/// </summary>
public static class AlertingServiceCollectionExtensions
{
    /// <summary>
    /// Registers alert evaluation and the background worker that drives it.
    /// </summary>
    /// <param name="configuration">Host configuration; options bind from the
    /// <c>AlertEvaluation</c> section.</param>
    /// <param name="configure">Optional overrides applied after configuration binding.</param>
    public static IServiceCollection AddAlerting(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AlertingOptions>? configure = null)
    {
        services.Configure<AlertingOptions>(configuration.GetSection(AlertingOptions.SectionName));
        if (configure is not null)
            services.Configure(configure);

        // Webhook delivery uses a named HttpClient ("AlertWebhook").
        services.AddHttpClient("AlertWebhook");
        services.AddScoped<INotificationChannel, WebhookNotificationChannel>();

        // One evaluator per AlertRuleType; AlertService keys them by SupportedType.
        services.AddScoped<IAlertEvaluator, MetricThresholdEvaluator>();
        services.AddScoped<IAlertEvaluator, ErrorRateEvaluator>();
        services.AddScoped<IAlertEvaluator, SlowTraceEvaluator>();
        services.AddScoped<IAlertEvaluator, LogSeveritySpikeEvaluator>();

        services.AddScoped<IAlertService, AlertService>();

        services.AddHostedService<AlertEvaluationWorker>();

        return services;
    }
}
