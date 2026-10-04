using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL's hour tier (plans/summary-rollups.md, Phase 4): re-folds closed hours of the minute rollup into
/// <c>request_rollup_hour</c> / <c>log_rollup_hour</c>. Runs under <c>GET_LOCK</c> on its own connection, so two API
/// instances never compact at once. For each signal it re-folds every closed hour from
/// <c>min(compacted_through, now - RecompactHours)</c> to the last closed hour -- <c>min</c>, not <c>max</c>: in steady
/// state <c>compacted_through</c> is the last closed hour, so <c>max</c> would only ever compact new hours and never fold
/// the late minute rows of the last <c>RecompactHours</c>; <c>min</c> re-folds those hours every run and, after downtime,
/// catches up from where it stopped. Each hour is one transaction (delete that hour's rows, insert the minute rows'
/// <c>SUM</c>/<c>MAX</c> by key), and readers on their own snapshot see the old rows until it commits. A minute row later
/// than <c>RecompactHours</c> stays in the minute tier only. The last closed hour is the hour containing
/// <c>writtenThrough</c>, so a closed hour has had its minutes written.
/// </summary>
public class MySqlRollupCompactor(IConfiguration configuration, IOptions<RollupOptions> options, ILogger<MySqlRollupCompactor> logger)
    : IRollupCompactor
{
    private const long HourNanos = 3_600_000_000_000L;
    private const string LockName = "keryhe_rollup_compaction";
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;
    private readonly RollupOptions _options = options.Value;

    private const string RequestInsert = """
        INSERT INTO request_rollup_hour (tenant_id, service_name, bucket_start_unix_nano, request_count, error_count,
            sum_duration_nanos, max_duration_nanos, h00, h01, h02, h03, h04, h05, h06, h07, h08, h09, h10, h11, h12, h13, h14, h15, h16, h17, h18, h19, h20, h21, h22, h23)
        SELECT tenant_id, service_name, @hour, SUM(request_count), SUM(error_count), SUM(sum_duration_nanos),
            MAX(max_duration_nanos), SUM(h00), SUM(h01), SUM(h02), SUM(h03), SUM(h04), SUM(h05), SUM(h06), SUM(h07), SUM(h08), SUM(h09), SUM(h10), SUM(h11), SUM(h12), SUM(h13), SUM(h14), SUM(h15), SUM(h16), SUM(h17), SUM(h18), SUM(h19), SUM(h20), SUM(h21), SUM(h22), SUM(h23)
        FROM request_rollup_minute
        WHERE bucket_start_unix_nano >= @hour AND bucket_start_unix_nano < @hour + @hourNanos
        GROUP BY tenant_id, service_name
        """;

    private const string LogInsert = """
        INSERT INTO log_rollup_hour (tenant_id, service_name, severity_number, bucket_start_unix_nano, record_count)
        SELECT tenant_id, service_name, severity_number, @hour, SUM(record_count)
        FROM log_rollup_minute
        WHERE bucket_start_unix_nano >= @hour AND bucket_start_unix_nano < @hour + @hourNanos
        GROUP BY tenant_id, service_name, severity_number
        """;

    public async Task<int> CompactAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // GET_LOCK is held by this connection; 0 = another instance is compacting right now.
        var got = await conn.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT GET_LOCK(@name, 0)", new { name = LockName }, cancellationToken: cancellationToken));
        if (got != 1) return 0;
        try
        {
            var writtenThrough = TimeConversion.DateTimeToUnixNano(RollupSummaryBuilder.WrittenThrough(nowUtc, _options));
            var closedTo = FloorHour(writtenThrough);
            var horizon = FloorHour(TimeConversion.DateTimeToUnixNano(nowUtc) - _options.RecompactHours * HourNanos);

            var folded = 0;
            folded += await CompactSignalAsync(conn, "request", "request_rollup_minute", "request_rollup_hour", RequestInsert, closedTo, horizon, cancellationToken);
            folded += await CompactSignalAsync(conn, "log", "log_rollup_minute", "log_rollup_hour", LogInsert, closedTo, horizon, cancellationToken);
            return folded;
        }
        finally
        {
            await conn.ExecuteScalarAsync<int?>(new CommandDefinition("SELECT RELEASE_LOCK(@name)", new { name = LockName }, cancellationToken: CancellationToken.None));
        }
    }

    private async Task<int> CompactSignalAsync(MySqlConnection conn, string signal, string minuteTable, string hourTable,
        string insertSql, long closedTo, long horizon, CancellationToken ct)
    {
        var compactedThrough = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT compacted_through_unix_nano FROM rollup_compaction WHERE signal_kind = @signal", new { signal }, cancellationToken: ct));

        long from;
        if (compactedThrough is { } done)
            from = Math.Min(done, horizon);
        else
        {
            // First run: start at the oldest minute row there is.
            var oldest = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
                $"SELECT MIN(bucket_start_unix_nano) FROM {minuteTable}", cancellationToken: ct));
            if (oldest is not { } first) return 0;
            from = FloorHour(first);
        }

        var hours = 0;
        for (var hour = from; hour < closedTo; hour += HourNanos)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {hourTable} WHERE bucket_start_unix_nano = @hour", new { hour }, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(insertSql, new { hour, hourNanos = HourNanos }, tx, commandTimeout: 120, cancellationToken: ct));
            await tx.CommitAsync(ct);
            hours++;
        }

        // Never move the boundary backwards (a clock step must not un-compact hours the reads already trust).
        var through = Math.Max(closedTo, compactedThrough ?? 0);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO rollup_compaction (signal_kind, compacted_through_unix_nano) VALUES (@signal, @through)
            ON DUPLICATE KEY UPDATE compacted_through_unix_nano = GREATEST(compacted_through_unix_nano, @through)
            """, new { signal, through }, cancellationToken: ct));
        if (hours > 0)
            logger.LogDebug("Rollup compaction ({Signal}): folded {Hours} hour(s) up to {Through}", signal, hours, TimeConversion.UnixNanoToDateTime(through));
        return hours;
    }

    private static long FloorHour(long nanos) => nanos - ((nanos % HourNanos) + HourNanos) % HourNanos;
}
