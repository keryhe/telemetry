using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Timescale.Services;

/// <summary>
/// Timescale implementation of <see cref="IApiKeyTouchStore"/>. One <c>UPDATE ... WHERE key_hash =
/// ANY($1)</c> per flush, regardless of how many distinct keys <see cref="ApiKeyTouchWorker"/>
/// drained for this interval — the same <c>= ANY(array)</c> idiom the bulk writer uses for
/// set-based writes.
/// </summary>
public class TimescaleApiKeyTouchStore(NpgsqlDataSource dataSource) : IApiKeyTouchStore
{
    public async Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken)
    {
        if (keyHashes.Count == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        const string sql = """
            UPDATE api_keys
            SET last_used_at = NOW()
            WHERE key_hash = ANY($1);
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = keyHashes.ToArray(),
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
