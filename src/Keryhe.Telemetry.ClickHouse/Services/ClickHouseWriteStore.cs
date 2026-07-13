using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="ITelemetryWriteStore"/> — the write-path
/// <c>Delete*</c> operations. ClickHouse has no foreign-key cascades, so child rows
/// (span events/links, metric data points) are deleted explicitly. Deletes use ClickHouse's
/// lightweight <c>DELETE FROM</c>; they are applied asynchronously as background mutations, so
/// affected-row counts are not reliably returned — existence is checked up front where the
/// contract needs a boolean/count. Deletes are admin/retention operations here, so the async
/// semantics are acceptable.
/// </summary>
public sealed class ClickHouseWriteStore(IConfiguration configuration) : ITelemetryWriteStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    private async Task<ClickHouseConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // =========================================================================
    // TRACES
    // =========================================================================

    public async Task<bool> DeleteTraceAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenAsync(cancellationToken);

        var spanDbIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM spans WHERE trace_id = @traceId",
            new { traceId = traceIdHex }, cancellationToken: cancellationToken))).ToList();

        if (spanDbIds.Count == 0) return false;

        var inList = string.Join(",", spanDbIds);
        await conn.ExecuteAsync(new CommandDefinition($"DELETE FROM span_events WHERE span_id IN ({inList})", cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition($"DELETE FROM span_links  WHERE span_id IN ({inList})", cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM spans WHERE trace_id = @traceId",
            new { traceId = traceIdHex }, cancellationToken: cancellationToken));
        return true;
    }

    public async Task<int> DeleteTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano   = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = await OpenAsync(cancellationToken);

        var traceCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(DISTINCT trace_id) FROM spans WHERE start_time_unix_nano BETWEEN @start AND @end",
            new { start = startNano, end = endNano }, cancellationToken: cancellationToken));

        // Delete child rows for the affected spans via subquery, then the spans themselves.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM span_events WHERE span_id IN (SELECT id FROM spans WHERE start_time_unix_nano BETWEEN @start AND @end)",
            new { start = startNano, end = endNano }, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM span_links WHERE span_id IN (SELECT id FROM spans WHERE start_time_unix_nano BETWEEN @start AND @end)",
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

        var spanDbId = ClickHouseIds.FromKey($"{traceIdHex}__{spanIdHex}");

        await using var conn = await OpenAsync(cancellationToken);

        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM spans WHERE trace_id = @traceId AND span_id = @spanId",
            new { traceId = traceIdHex, spanId = spanIdHex }, cancellationToken: cancellationToken));
        if (exists == 0) return false;

        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM span_events WHERE span_id = @id", new { id = spanDbId }, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM span_links  WHERE span_id = @id", new { id = spanDbId }, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE trace_id = @traceId AND span_id = @spanId",
            new { traceId = traceIdHex, spanId = spanIdHex }, cancellationToken: cancellationToken));
        return true;
    }

    // =========================================================================
    // METRICS
    // =========================================================================

    public async Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);

        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM metrics WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        if (exists == 0) return false;

        await DeleteMetricDataPointsAsync(conn, $"metric_id = {id}", cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM metrics WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        return true;
    }

    public async Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        await using var conn = await OpenAsync(cancellationToken);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM metrics WHERE name = @name", new { name }, cancellationToken: cancellationToken));

        await DeleteMetricDataPointsAsync(conn, "metric_id IN (SELECT id FROM metrics WHERE name = @name)", cancellationToken, new { name });
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM metrics WHERE name = @name", new { name }, cancellationToken: cancellationToken));
        return count;
    }

    public async Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        await using var conn = await OpenAsync(cancellationToken);
        var args = new { start = startTime, end = endTime };

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM metrics WHERE created_at BETWEEN @start AND @end", args, cancellationToken: cancellationToken));

        await DeleteMetricDataPointsAsync(conn, "metric_id IN (SELECT id FROM metrics WHERE created_at BETWEEN @start AND @end)", cancellationToken, args);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM metrics WHERE created_at BETWEEN @start AND @end", args, cancellationToken: cancellationToken));
        return count;
    }

    public async Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;

        await using var conn = await OpenAsync(cancellationToken);
        var args = new { cutoff };

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM metrics WHERE created_at < @cutoff", args, cancellationToken: cancellationToken));

        await DeleteMetricDataPointsAsync(conn, "metric_id IN (SELECT id FROM metrics WHERE created_at < @cutoff)", cancellationToken, args);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM metrics WHERE created_at < @cutoff", args, cancellationToken: cancellationToken));
        return count;
    }

    private static readonly string[] DataPointTables =
    [
        "gauge_data_points", "sum_data_points", "histogram_data_points",
        "exponential_histogram_data_points", "summary_data_points"
    ];

    private static async Task DeleteMetricDataPointsAsync(
        ClickHouseConnection conn, string predicate, CancellationToken ct, object? args = null)
    {
        foreach (var table in DataPointTables)
            await conn.ExecuteAsync(new CommandDefinition($"DELETE FROM {table} WHERE {predicate}", args, cancellationToken: ct));
    }

    // =========================================================================
    // LOGS
    // =========================================================================

    public async Task<bool> DeleteLogRecordAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);

        var exists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM log_records WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        if (exists == 0) return false;

        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM log_records WHERE id = @id", new { id }, cancellationToken: cancellationToken));
        return true;
    }

    public async Task<int> DeleteLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano   = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = await OpenAsync(cancellationToken);
        var args = new { start = startNano, end = endNano };

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM log_records WHERE time_unix_nano BETWEEN @start AND @end", args, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano BETWEEN @start AND @end", args, cancellationToken: cancellationToken));
        return count;
    }
}
