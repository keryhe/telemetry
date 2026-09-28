using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.PostgreSQL.Services;

/// <summary>
/// PostgreSQL implementation of <see cref="IMetricTouchStore"/> (list-pages-server-side plan,
/// Phase 5, decision 27). One <c>INSERT ... SELECT unnest(...) ON CONFLICT DO UPDATE</c> per
/// flush, keeping the greater <c>last_seen_unix_nano</c> per metric id — the same
/// <c>unnest()</c>-array idiom the bulk writer uses for its own set-based upserts.
/// </summary>
public class PostgreSqlMetricTouchStore(NpgsqlDataSource dataSource) : IMetricTouchStore
{
    public async Task TouchAsync(IReadOnlyCollection<KeyValuePair<long, long>> touches, CancellationToken cancellationToken)
    {
        if (touches.Count == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
            SELECT * FROM unnest($1::bigint[], $2::bigint[])
            ON CONFLICT (metric_id) DO UPDATE
            SET last_seen_unix_nano = GREATEST(metric_last_seen.last_seen_unix_nano, EXCLUDED.last_seen_unix_nano);
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = touches.Select(t => t.Key).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = touches.Select(t => t.Value).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
