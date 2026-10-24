using Dapper;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IRetentionSettingsRepository"/>. Each tenant's windows are plain
/// int columns on <c>tenants</c>, so unlike <see cref="AlertRuleRepositoryBase"/> there is no
/// dialect-specific SQL to abstract here -- every provider reads/writes them with the same statements.
/// The sweeps that enforce them are <see cref="RetentionSweeperBase"/>.
/// </summary>
public abstract class RetentionSettingsRepositoryBase : ControlPlaneRepositoryBase, IRetentionSettingsRepository
{
    private const string Columns = """
        trace_retention_days AS TraceRetentionDays,
        log_retention_days AS LogRetentionDays,
        metric_retention_days AS MetricRetentionDays,
        retention_updated_at AS UpdatedAt
        """;

    public async Task<RetentionSettings?> GetSettingsAsync(long tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<RetentionSettingsRow>(new CommandDefinition(
            $"SELECT {Columns} FROM tenants WHERE id = @tenantId",
            new { tenantId },
            cancellationToken: ct));

        return row?.ToSettings();
    }

    public async Task<bool> UpdateSettingsAsync(long tenantId, RetentionSettings settings, CancellationToken ct = default)
    {
        var updatedAt = DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync(ct);
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tenants
            SET trace_retention_days = @traceRetentionDays,
                log_retention_days = @logRetentionDays,
                metric_retention_days = @metricRetentionDays,
                retention_updated_at = @updatedAt
            WHERE id = @tenantId
            """,
            new
            {
                tenantId,
                traceRetentionDays = settings.TraceRetentionDays,
                logRetentionDays = settings.LogRetentionDays,
                metricRetentionDays = settings.MetricRetentionDays,
                updatedAt = TimestampParam(updatedAt)
            },
            cancellationToken: ct));

        if (updated == 0) return false;
        settings.UpdatedAt = updatedAt;
        return true;
    }

    public async Task<IReadOnlyDictionary<long, RetentionSettings>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RetentionSettingsRow>(new CommandDefinition(
            $"SELECT id AS TenantId, {Columns} FROM tenants",
            cancellationToken: ct));

        return rows.ToDictionary(r => r.TenantId, r => r.ToSettings());
    }

    private sealed class RetentionSettingsRow
    {
        public long TenantId { get; set; }
        public int TraceRetentionDays { get; set; }
        public int LogRetentionDays { get; set; }
        public int MetricRetentionDays { get; set; }
        public DateTime UpdatedAt { get; set; }

        public RetentionSettings ToSettings() => new()
        {
            TraceRetentionDays = TraceRetentionDays,
            LogRetentionDays = LogRetentionDays,
            MetricRetentionDays = MetricRetentionDays,
            UpdatedAt = UpdatedAt
        };
    }
}
