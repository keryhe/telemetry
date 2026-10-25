using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>The retention windows the collector uses to drop records that retention would delete at once.</summary>
internal interface IRetentionWindows
{
    /// <summary>
    /// The oldest timestamp worth storing for a signal ("traces", "logs" or "metrics") of a tenant: its own window, or the
    /// built-in one (<see cref="RetentionDefaults"/>) when it has no control-plane row. Null when the windows were never loaded.
    /// </summary>
    DateTime? OldestAllowedUtc(string signal, long tenantId);

    /// <summary>Re-reads the windows once; a failure keeps the previous ones.</summary>
    Task RefreshAsync(CancellationToken ct);

    int RefreshSeconds { get; }
}

/// <summary>
/// Reads every tenant's retention windows from the control plane's <c>tenants</c> table and caches them
/// (re-read every <c>refreshSeconds</c>, a provider option). The collector registers no
/// <c>IRetentionSettingsRepository</c> (it is API-side), so the connection comes from <see cref="ControlPlaneDbConnection"/>.
/// Fails open: when the windows cannot be read, nothing is dropped as out-of-retention.
/// </summary>
internal sealed class RetentionWindowCache(
    IConfiguration configuration,
    int refreshSeconds,
    TimeProvider time,
    ILogger<RetentionWindowCache> logger) : IRetentionWindows
{
    private volatile Dictionary<long, Windows>? _windows;

    private sealed class RetentionRow
    {
        public long Id { get; set; }
        public int Trace { get; set; }
        public int Log { get; set; }
        public int Metric { get; set; }
    }

    private sealed record Windows(int TraceDays, int LogDays, int MetricDays);

    private static readonly Windows BuiltIn = new(RetentionDefaults.TraceDays, RetentionDefaults.LogDays, RetentionDefaults.MetricDays);

    public DateTime? OldestAllowedUtc(string signal, long tenantId)
    {
        if (_windows is not { } tenants) return null;
        var w = tenants.TryGetValue(tenantId, out var own) ? own : BuiltIn;
        var days = signal switch { "traces" => w.TraceDays, "logs" => w.LogDays, _ => w.MetricDays };
        return days <= 0 ? null : time.GetUtcNow().UtcDateTime.AddDays(-days);
    }

    /// <summary>Re-reads the windows once; a failure is logged and the previous windows (if any) are kept.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = ControlPlaneDbConnection.TryCreate(configuration);
            if (connection is null)
                return;
            await connection.OpenAsync(ct);
            var rows = await connection.QueryAsync<RetentionRow>(new CommandDefinition(
                "SELECT id AS Id, trace_retention_days AS Trace, log_retention_days AS Log, metric_retention_days AS Metric FROM tenants",
                cancellationToken: ct));
            _windows = rows.ToDictionary(row => row.Id, row => new Windows(row.Trace, row.Log, row.Metric));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the retention windows; records outside retention are not dropped at ingest until it can");
        }
    }

    public int RefreshSeconds => refreshSeconds;
}
