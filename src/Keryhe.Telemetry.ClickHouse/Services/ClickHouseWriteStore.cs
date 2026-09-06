using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="ITelemetryWriteStore"/> — the write-path retention
/// sweeps. Two things differ from the relational providers:
///
/// There are no foreign keys and so no cascades. Child rows are deleted explicitly, and the trace
/// sweep must remove <c>span_events</c> and <c>span_links</c> before the spans they hang off,
/// because once the parent rows are gone the subquery that identifies the children matches nothing.
///
/// A lightweight <c>DELETE</c> is an asynchronous mutation that reports no row count, so every
/// sweep pre-counts what it is about to remove. That count is the return value; it is taken before
/// the mutation is issued and is therefore a snapshot, not a receipt.
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

    public async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenAsync(cancellationToken);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM spans WHERE start_time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        // Children first: these subqueries resolve against spans, so they must run while the
        // parent rows still exist.
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM span_events WHERE span_id IN (SELECT id FROM spans WHERE start_time_unix_nano < @cutoff)",
            args, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM span_links WHERE span_id IN (SELECT id FROM spans WHERE start_time_unix_nano < @cutoff)",
            args, cancellationToken: cancellationToken));
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM spans WHERE start_time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        return count;
    }

    public async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenAsync(cancellationToken);

        var count = 0;
        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            count += await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                $"SELECT count() FROM {table} WHERE time_unix_nano < @cutoff",
                args, cancellationToken: cancellationToken));
        }

        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE time_unix_nano < @cutoff",
                args, cancellationToken: cancellationToken));
        }

        return count;
    }

    public async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var args = new { cutoff = CutoffNano(retentionPeriod) };

        await using var conn = await OpenAsync(cancellationToken);

        var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count() FROM log_records WHERE time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano < @cutoff",
            args, cancellationToken: cancellationToken));

        return count;
    }

    /// <summary>
    /// The retention cutoff as unix nanoseconds.
    ///
    /// The negative guard is load-bearing, not defensive noise: a negative period puts the cutoff
    /// in the FUTURE, at which point every predicate here matches every row in the table. On a
    /// retention API that is the difference between a no-op and erasing the telemetry store, so it
    /// fails loudly rather than quietly succeeding.
    /// </summary>
    private static long CutoffNano(TimeSpan retentionPeriod)
    {
        if (retentionPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retentionPeriod),
                "Retention period cannot be negative; that would delete all telemetry.");

        return TimeConversion.DateTimeToUnixNano(DateTime.UtcNow - retentionPeriod);
    }
}
