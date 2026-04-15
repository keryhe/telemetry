using System.Text.Json;
using Keryhe.Telemetry.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace Keryhe.Telemetry.Client.Components.Pages;

public partial class Alerts : ComponentBase
{
    private List<AlertRule> _rules = new();
    private List<AlertEvent> _events = new();
    private bool _loading = true;
    private bool _showDialog;
    private bool _isEditing;
    private bool _saving;
    private string _dialogError = "";
    private AlertRuleForm _form = new();

    [Inject]
    private IAlertRuleRepository AlertRepo { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        _loading = true;
        StateHasChanged();
        try
        {
            _rules = await AlertRepo.GetAllRulesAsync();
            _events = await AlertRepo.GetRecentAlertEventsAsync(50);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void OpenCreateDialog()
    {
        _form = new AlertRuleForm();
        _isEditing = false;
        _dialogError = "";
        _showDialog = true;
    }

    private void OpenEditDialog(AlertRule rule)
    {
        _form = AlertRuleForm.FromRule(rule);
        _isEditing = true;
        _dialogError = "";
        _showDialog = true;
    }

    private async Task SaveRuleAsync()
    {
        _dialogError = "";

        if (string.IsNullOrWhiteSpace(_form.Name))
        {
            _dialogError = "Rule name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_form.WebhookUrl))
        {
            _dialogError = "Webhook URL is required.";
            return;
        }

        if (_form.Type == AlertRuleType.MetricThreshold && string.IsNullOrWhiteSpace(_form.MetricName))
        {
            _dialogError = "Metric name is required for MetricThreshold rules.";
            return;
        }

        _saving = true;
        StateHasChanged();

        try
        {
            var rule = new AlertRule
            {
                Id = _form.Id,
                Name = _form.Name.Trim(),
                Type = _form.Type,
                ServiceName = string.IsNullOrWhiteSpace(_form.ServiceName) ? null : _form.ServiceName.Trim(),
                ConditionJson = _form.SerializeCondition(),
                WebhookUrl = _form.WebhookUrl.Trim(),
                CooldownMinutes = _form.CooldownMinutes,
                Enabled = _form.Enabled
            };

            if (_isEditing)
                await AlertRepo.UpdateRuleAsync(rule);
            else
                await AlertRepo.CreateRuleAsync(rule);

            _showDialog = false;
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _dialogError = $"Failed to save rule: {ex.Message}";
        }
        finally
        {
            _saving = false;
            StateHasChanged();
        }
    }

    private async Task DeleteRuleAsync(int id)
    {
        await AlertRepo.DeleteRuleAsync(id);
        await LoadDataAsync();
    }

    private async Task ToggleRuleAsync(AlertRule rule, bool enabled)
    {
        var updated = new AlertRule
        {
            Id = rule.Id,
            Name = rule.Name,
            Type = rule.Type,
            ServiceName = rule.ServiceName,
            ConditionJson = rule.ConditionJson,
            WebhookUrl = rule.WebhookUrl,
            CooldownMinutes = rule.CooldownMinutes,
            Enabled = enabled,
            CreatedAt = rule.CreatedAt,
            LastFiredAt = rule.LastFiredAt
        };
        await AlertRepo.UpdateRuleAsync(updated);
        await LoadDataAsync();
    }

    private static Color GetTypeColor(AlertRuleType type) => type switch
    {
        AlertRuleType.MetricThreshold  => Color.Info,
        AlertRuleType.ErrorRate        => Color.Warning,
        AlertRuleType.SlowTrace        => Color.Secondary,
        AlertRuleType.LogSeveritySpike => Color.Error,
        _                              => Color.Default
    };

    private static string FormatCondition(AlertRule rule)
    {
        try
        {
            return rule.Type switch
            {
                AlertRuleType.MetricThreshold => FormatMetricThreshold(rule.ConditionJson),
                AlertRuleType.ErrorRate       => FormatErrorRate(rule.ConditionJson),
                AlertRuleType.SlowTrace       => FormatSlowTrace(rule.ConditionJson),
                AlertRuleType.LogSeveritySpike => FormatLogSeveritySpike(rule.ConditionJson),
                _                             => rule.ConditionJson
            };
        }
        catch
        {
            return rule.ConditionJson;
        }
    }

    private static string FormatMetricThreshold(string json)
    {
        var c = JsonSerializer.Deserialize<MetricThresholdCondition>(json)!;
        return $"{c.MetricName} {c.Operator} {c.Threshold}";
    }

    private static string FormatErrorRate(string json)
    {
        var c = JsonSerializer.Deserialize<ErrorRateCondition>(json)!;
        return $"Error rate > {c.ThresholdPercent}% in {c.WindowMinutes} min";
    }

    private static string FormatSlowTrace(string json)
    {
        var c = JsonSerializer.Deserialize<SlowTraceCondition>(json)!;
        return $"Duration > {c.MinDurationMs}ms in {c.WindowMinutes} min";
    }

    private static string FormatLogSeveritySpike(string json)
    {
        var c = JsonSerializer.Deserialize<LogSeveritySpikeCondition>(json)!;
        var severityLabel = c.MinSeverity switch
        {
            <= 5  => "DEBUG",
            <= 9  => "INFO",
            <= 13 => "WARN",
            <= 17 => "ERROR",
            _     => "FATAL"
        };
        return $"> {c.CountThreshold} {severityLabel}+ logs in {c.WindowMinutes} min";
    }

    private static string ParseDetails(string detailsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return doc.RootElement.TryGetProperty("details", out var d) ? d.GetString() ?? detailsJson : detailsJson;
        }
        catch
        {
            return detailsJson;
        }
    }

    // -------------------------------------------------------------------------
    // Form model
    // -------------------------------------------------------------------------

    private class AlertRuleForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public AlertRuleType Type { get; set; } = AlertRuleType.ErrorRate;
        public string? ServiceName { get; set; }
        public string WebhookUrl { get; set; } = "";
        public int CooldownMinutes { get; set; } = 60;
        public bool Enabled { get; set; } = true;

        // MetricThreshold
        public string MetricName { get; set; } = "";
        public string Operator { get; set; } = ">";
        public double Threshold { get; set; } = 90.0;

        // ErrorRate
        public double ThresholdPercent { get; set; } = 5.0;

        // SlowTrace / ErrorRate / LogSeveritySpike
        public int WindowMinutes { get; set; } = 5;

        // SlowTrace
        public int MinDurationMs { get; set; } = 1000;

        // LogSeveritySpike
        public int MinSeverity { get; set; } = 17;
        public int CountThreshold { get; set; } = 10;

        public string SerializeCondition() => Type switch
        {
            AlertRuleType.MetricThreshold => JsonSerializer.Serialize(new MetricThresholdCondition
            {
                MetricName = MetricName,
                Operator = Operator,
                Threshold = Threshold
            }),
            AlertRuleType.ErrorRate => JsonSerializer.Serialize(new ErrorRateCondition
            {
                ThresholdPercent = ThresholdPercent,
                WindowMinutes = WindowMinutes
            }),
            AlertRuleType.SlowTrace => JsonSerializer.Serialize(new SlowTraceCondition
            {
                MinDurationMs = MinDurationMs,
                WindowMinutes = WindowMinutes
            }),
            AlertRuleType.LogSeveritySpike => JsonSerializer.Serialize(new LogSeveritySpikeCondition
            {
                MinSeverity = MinSeverity,
                CountThreshold = CountThreshold,
                WindowMinutes = WindowMinutes
            }),
            _ => "{}"
        };

        public static AlertRuleForm FromRule(AlertRule rule)
        {
            var form = new AlertRuleForm
            {
                Id = rule.Id,
                Name = rule.Name,
                Type = rule.Type,
                ServiceName = rule.ServiceName,
                WebhookUrl = rule.WebhookUrl,
                CooldownMinutes = rule.CooldownMinutes,
                Enabled = rule.Enabled
            };

            try
            {
                switch (rule.Type)
                {
                    case AlertRuleType.MetricThreshold:
                        var mt = JsonSerializer.Deserialize<MetricThresholdCondition>(rule.ConditionJson)!;
                        form.MetricName = mt.MetricName;
                        form.Operator = mt.Operator;
                        form.Threshold = mt.Threshold;
                        break;
                    case AlertRuleType.ErrorRate:
                        var er = JsonSerializer.Deserialize<ErrorRateCondition>(rule.ConditionJson)!;
                        form.ThresholdPercent = er.ThresholdPercent;
                        form.WindowMinutes = er.WindowMinutes;
                        break;
                    case AlertRuleType.SlowTrace:
                        var st = JsonSerializer.Deserialize<SlowTraceCondition>(rule.ConditionJson)!;
                        form.MinDurationMs = st.MinDurationMs;
                        form.WindowMinutes = st.WindowMinutes;
                        break;
                    case AlertRuleType.LogSeveritySpike:
                        var ls = JsonSerializer.Deserialize<LogSeveritySpikeCondition>(rule.ConditionJson)!;
                        form.MinSeverity = ls.MinSeverity;
                        form.CountThreshold = ls.CountThreshold;
                        form.WindowMinutes = ls.WindowMinutes;
                        break;
                }
            }
            catch { /* leave defaults */ }

            return form;
        }
    }
}
