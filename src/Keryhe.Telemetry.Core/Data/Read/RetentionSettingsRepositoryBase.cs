using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IRetentionSettingsRepository"/>'s settings read/write.
/// The <c>retention_settings</c> row is a fixed singleton (<c>id = 1</c>) with plain int
/// columns, so unlike <see cref="AlertRuleRepositoryBase"/> there is no dialect-specific SQL to
/// abstract here — every provider reads/writes it with the same statements. The three
/// <c>Delete*</c> sweeps stay abstract: their DML differs per provider (batching strategy,
/// cascade vs. explicit child deletes) the same way it did on the former
/// <c>ITelemetryWriteStore</c> implementations.
/// </summary>
public abstract class RetentionSettingsRepositoryBase : IRetentionSettingsRepository
{
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

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

    public virtual async Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default)
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

    public abstract Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
    public abstract Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
    public abstract Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);

    /// <summary>
    /// The retention cutoff as unix nanoseconds.
    ///
    /// The negative guard is load-bearing, not defensive noise: a negative period puts the cutoff
    /// in the FUTURE, at which point every predicate here matches every row in the table. On a
    /// retention API that is the difference between a no-op and erasing the telemetry store, so it
    /// fails loudly rather than quietly succeeding.
    /// </summary>
    protected static long CutoffNano(TimeSpan retentionPeriod)
    {
        if (retentionPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod),
                "Retention period cannot be negative; that would delete all telemetry.");

        return TimeConversion.DateTimeToUnixNano(DateTime.UtcNow - retentionPeriod);
    }

    private sealed class RetentionSettingsRow
    {
        public int TraceRetentionDays { get; set; }
        public int LogRetentionDays { get; set; }
        public int MetricRetentionDays { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
