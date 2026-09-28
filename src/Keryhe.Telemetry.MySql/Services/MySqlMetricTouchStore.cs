using MySqlConnector;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="IMetricTouchStore"/> (list-pages-server-side plan, Phase 5,
/// decision 27). One <c>INSERT ... ON DUPLICATE KEY UPDATE ... GREATEST(...)</c> per batch (each
/// already capped and sorted by metric id by <see cref="MetricTouchWorker"/>), chunked to stay
/// well under a reasonable single-statement parameter/placeholder count.
/// </summary>
public class MySqlMetricTouchStore(IConfiguration configuration) : IMetricTouchStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    private const int ChunkSize = 1000;

    public async Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken)
    {
        if (touches.Count == 0) return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var list = touches as IReadOnlyList<KeyValuePair<long, long>> ?? touches.ToList();
        for (var offset = 0; offset < list.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, list.Count - offset);
            var values = string.Join(",", Enumerable.Range(0, count).Select(i => $"(@id{i}, @ts{i})"));
            var sql = $"""
                INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
                VALUES {values}
                ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));
                """;

            await using var cmd = new MySqlCommand(sql, conn);
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue($"@id{i}", list[offset + i].Key);
                cmd.Parameters.AddWithValue($"@ts{i}", list[offset + i].Value);
            }
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
