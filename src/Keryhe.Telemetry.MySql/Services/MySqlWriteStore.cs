using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="ITelemetryWriteStore"/> — the write-path
/// <c>Delete*</c> operations. Child rows (span events/links, metric data points) are
/// removed by the schema's InnoDB <c>ON DELETE CASCADE</c> foreign keys, so the deletes
/// target only the parent tables.
/// </summary>
public sealed class MySqlWriteStore(IConfiguration configuration) : ITelemetryWriteStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    private MySqlConnection Connect() => new(_connectionString);

    // =========================================================================
    // TRACES
    // =========================================================================

    public async Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = Connect();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE trace_id = @traceId",
            new { traceId = traceIdHex }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<int> DeleteTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano   = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = Connect();

        // Match the prior EF behavior: report the number of distinct traces affected.
        var traceCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(DISTINCT trace_id) FROM spans WHERE start_time_unix_nano BETWEEN @start AND @end",
            new { start = startNano, end = endNano }, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE start_time_unix_nano BETWEEN @start AND @end",
            new { start = startNano, end = endNano }, cancellationToken: cancellationToken));

        return traceCount;
    }

    public async Task<bool> DeleteSpanAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex))
            throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));

        await using var conn = Connect();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE trace_id = @traceId AND span_id = @spanId",
            new { traceId = traceIdHex, spanId = spanIdHex }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    // =========================================================================
    // METRICS
    // =========================================================================

    public async Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = Connect();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM metrics WHERE id = @id",
            new { id }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        await using var conn = Connect();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM metrics WHERE name = @name",
            new { name }, cancellationToken: cancellationToken));
    }

    public async Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        await using var conn = Connect();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM metrics WHERE created_at BETWEEN @start AND @end",
            new { start = startTime, end = endTime }, cancellationToken: cancellationToken));
    }

    public async Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;

        await using var conn = Connect();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM metrics WHERE created_at < @cutoff",
            new { cutoff }, cancellationToken: cancellationToken));
    }

    // =========================================================================
    // LOGS
    // =========================================================================

    public async Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = Connect();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE id = @id",
            new { id }, cancellationToken: cancellationToken));
        return affected > 0;
    }

    public async Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano   = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = Connect();
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano BETWEEN @start AND @end",
            new { start = startNano, end = endNano }, cancellationToken: cancellationToken));
    }
}
