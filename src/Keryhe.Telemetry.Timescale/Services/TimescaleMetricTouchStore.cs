using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Timescale.Services;

/// <summary>
/// Timescale implementation of <see cref="IMetricTouchStore"/> (list-pages-server-side plan,
/// Phase 5, decision 27). Identical shape to
/// <see cref="Keryhe.Telemetry.PostgreSQL.Services.PostgreSqlMetricTouchStore"/> —
/// <c>metric_last_seen</c> is a plain (non-hypertable) control table on this provider too.
/// </summary>
public class TimescaleMetricTouchStore(NpgsqlDataSource dataSource) : IMetricTouchStore
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
