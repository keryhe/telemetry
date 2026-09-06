using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SQL Server implementation of <see cref="ITelemetryWriteStore"/> — the write-path retention
/// sweeps. Span events and links are removed by the schema's <c>ON DELETE CASCADE</c> foreign
/// keys, so the trace sweep targets only <c>spans</c>.
///
/// The metric sweep is the exception to that pattern: it targets the data-point tables directly
/// on <c>time_unix_nano</c> rather than cascading from <c>metrics</c>, because after the 2.7.0
/// dedup a catalog row's <c>created_at</c> is "first seen" and cascading from it would discard a
/// metric's entire history. See <see cref="ITelemetryWriteStore"/>.
/// </summary>
public sealed class SqlServerWriteStore(IConfiguration configuration) : ITelemetryWriteStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    private SqlConnection Connect() => new(_connectionString);

    /// <summary>
    /// Rows removed per statement by the retention sweeps. Bounded so a sweep cannot escalate to a
    /// table lock (SQL Server escalates past ~5000 row locks) while the ingest path is still appending.
    /// </summary>
    private const int DeleteBatchSize = 50_000;

    public Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(["spans"], "start_time_unix_nano", retentionPeriod, cancellationToken);

    public Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(TelemetryIngestionHelpers.TimePrunedMetricTables, "time_unix_nano", retentionPeriod, cancellationToken);

    public Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => SweepAsync(["log_records"], "time_unix_nano", retentionPeriod, cancellationToken);

    /// <summary>
    /// Deletes every row in <paramref name="tables"/> whose <paramref name="timeColumn"/> predates the
    /// cutoff, in bounded chunks. Returns the total rows removed.
    ///
    /// The table and column names are interpolated rather than parameterized because they are not
    /// user input: they are compile-time literals and the entries of
    /// <see cref="TelemetryIngestionHelpers.TimePrunedMetricTables"/>.
    /// </summary>
    private async Task<int> SweepAsync(
        IReadOnlyList<string> tables,
        string timeColumn,
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = Connect();

        var total = 0;
        foreach (var table in tables)
        {
            int batch;
            do
            {
                batch = await conn.ExecuteAsync(new CommandDefinition(
                    $"DELETE TOP ({DeleteBatchSize}) FROM {table} WHERE {timeColumn} < @cutoff",
                    new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
                total += batch;
            } while (batch == DeleteBatchSize);
        }

        return total;
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
