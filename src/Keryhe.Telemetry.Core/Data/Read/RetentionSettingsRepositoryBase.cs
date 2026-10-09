using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IRetentionSettingsRepository"/>.
/// The <c>retention_settings</c> row is a fixed singleton (<c>id = 1</c>) with plain int
/// columns, so unlike <see cref="AlertRuleRepositoryBase"/> there is no dialect-specific SQL to
/// abstract here — every provider reads/writes it with the same statements. The sweeps that
/// enforce it are <see cref="RetentionSweeperBase"/>.
/// </summary>
public abstract class RetentionSettingsRepositoryBase : ControlPlaneRepositoryBase, IRetentionSettingsRepository
{
    public async Task<RetentionSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<RetentionSettingsRow>(new CommandDefinition(
            """
            SELECT trace_retention_days AS TraceRetentionDays,
                   log_retention_days AS LogRetentionDays,
                   metric_retention_days AS MetricRetentionDays,
                   updated_at AS UpdatedAt
            FROM retention_settings
            WHERE id = 1
            """,
            cancellationToken: ct));

        return new RetentionSettings
        {
            TraceRetentionDays = row.TraceRetentionDays,
            LogRetentionDays = row.LogRetentionDays,
            MetricRetentionDays = row.MetricRetentionDays,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        var updatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE retention_settings
            SET trace_retention_days = @traceRetentionDays,
                log_retention_days = @logRetentionDays,
                metric_retention_days = @metricRetentionDays,
                updated_at = @updatedAt
            WHERE id = 1
            """,
            new
            {
                traceRetentionDays = settings.TraceRetentionDays,
                logRetentionDays = settings.LogRetentionDays,
                metricRetentionDays = settings.MetricRetentionDays,
                updatedAt
            },
            cancellationToken: ct));

        settings.UpdatedAt = updatedAt;
    }

    private sealed class RetentionSettingsRow
    {
        public int TraceRetentionDays { get; set; }
        public int LogRetentionDays { get; set; }
        public int MetricRetentionDays { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
