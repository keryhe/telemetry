using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>The retention windows the collector uses to drop records that retention would delete at once.</summary>
internal interface IRetentionWindows
{
    /// <summary>The oldest timestamp worth storing for a signal ("traces", "logs" or "metrics"), or null when the windows are unknown.</summary>
    DateTime? OldestAllowedUtc(string signal);

    /// <summary>Re-reads the windows once; a failure keeps the previous ones.</summary>
    Task RefreshAsync(CancellationToken ct);

    int RefreshSeconds { get; }
}

/// <summary>
/// Reads the retention windows from the control plane's <c>retention_settings</c> row and caches them
/// (<see cref="ClickHouseIngestionOptions.RetentionRefreshSeconds"/>). The collector registers no
/// <c>IRetentionSettingsRepository</c> (it is API-side) and this project references no relational driver, so the
/// connection is created by type name from <c>ControlPlane:Provider</c>: the host has loaded that provider's driver
/// for its API-key lookup. Fails open: when the windows cannot be read, nothing is dropped as out-of-retention.
/// </summary>
internal sealed class RetentionWindowCache(
    IConfiguration configuration,
    IOptions<ClickHouseIngestionOptions> options,
    TimeProvider time,
    ILogger<RetentionWindowCache> logger) : IRetentionWindows
{
    private static readonly Dictionary<string, string> ConnectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PostgreSQL"] = "Npgsql.NpgsqlConnection, Npgsql",
        ["SqlServer"] = "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
        ["MySql"] = "MySqlConnector.MySqlConnection, MySqlConnector"
    };

    private volatile Windows? _windows;

    private sealed class RetentionRow { public int Trace { get; set; } public int Log { get; set; } public int Metric { get; set; } }

    private sealed record Windows(int TraceDays, int LogDays, int MetricDays);

    public DateTime? OldestAllowedUtc(string signal)
    {
        if (_windows is not { } w) return null;
        var days = signal switch { "traces" => w.TraceDays, "logs" => w.LogDays, _ => w.MetricDays };
        return days <= 0 ? null : time.GetUtcNow().UtcDateTime.AddDays(-days);
    }

    /// <summary>Re-reads the row once; a failure is logged and the previous windows (if any) are kept.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var provider = configuration["ControlPlane:Provider"] ?? "";
            var connectionString = configuration.GetConnectionString("ControlPlane");
            if (!ConnectionTypes.TryGetValue(provider, out var typeName) || string.IsNullOrEmpty(connectionString))
                return;
            var type = Type.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} is not loaded.");
            await using var connection = (DbConnection)Activator.CreateInstance(type, connectionString)!;
            await connection.OpenAsync(ct);
            var row = await connection.QuerySingleOrDefaultAsync<RetentionRow>(new CommandDefinition(
                "SELECT trace_retention_days AS Trace, log_retention_days AS Log, metric_retention_days AS Metric FROM retention_settings WHERE id = 1",
                cancellationToken: ct));
            if (row is not null) _windows = new Windows(row.Trace, row.Log, row.Metric);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the retention windows; records outside retention are not dropped at ingest until it can");
        }
    }

    public int RefreshSeconds => options.Value.RetentionRefreshSeconds;
}
