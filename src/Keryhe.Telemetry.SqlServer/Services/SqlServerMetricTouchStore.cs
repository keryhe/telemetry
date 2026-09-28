using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SQL Server implementation of <see cref="IMetricTouchStore"/> (list-pages-server-side plan,
/// Phase 5, decision 27). <see cref="MetricTouchWorker"/> already caps each call at
/// <c>MetricTouchOptions.MaxBatchSize</c> (default 4,000, below SQL Server's lock-escalation
/// point) and sorts by metric id before calling, so concurrent collector instances (scale-out
/// deployments) always lock rows in the same order and can't deadlock each other on
/// <c>metric_last_seen</c> — the only other writer of that table is another touch worker;
/// ingestion's own metrics upsert never touches it. <c>DEADLOCK_PRIORITY LOW</c> plus one retry on
/// error 1205 makes this batch the deadlock victim rather than whatever it collided with: a lost
/// touch batch is harmless, since the next interval carries a value at least as new.
/// </summary>
public class SqlServerMetricTouchStore(IConfiguration configuration) : IMetricTouchStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    // Two parameters per row; kept comfortably under SQL Server's 2,100-parameter-per-statement
    // limit (mirrors SqlServerApiKeyTouchStore's own chunking, one parameter per row there).
    private const int ChunkSize = 1000;

    public async Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken)
    {
        if (touches.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using (var priorityCmd = new SqlCommand("SET DEADLOCK_PRIORITY LOW", conn))
            await priorityCmd.ExecuteNonQueryAsync(cancellationToken);

        var list = touches as IReadOnlyList<KeyValuePair<long, long>> ?? touches.ToList();
        for (var offset = 0; offset < list.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, list.Count - offset);
            try
            {
                await TouchChunkAsync(conn, list, offset, count, cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                await Task.Delay(Random.Shared.Next(50, 200), cancellationToken);
                await TouchChunkAsync(conn, list, offset, count, cancellationToken);
            }
        }
    }

    private static async Task TouchChunkAsync(SqlConnection conn, IReadOnlyList<KeyValuePair<long, long>> list, int offset, int count, CancellationToken cancellationToken)
    {
        var values = string.Join(",", Enumerable.Range(0, count).Select(i => $"(@id{i}, @ts{i})"));
        var sql = $"""
            MERGE metric_last_seen WITH (HOLDLOCK) AS t
            USING (VALUES {values}) AS s (metric_id, last_seen_unix_nano)
            ON t.metric_id = s.metric_id
            WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
            WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);
            """;

        await using var cmd = new SqlCommand(sql, conn);
        for (var i = 0; i < count; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", list[offset + i].Key);
            cmd.Parameters.AddWithValue($"@ts{i}", list[offset + i].Value);
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
