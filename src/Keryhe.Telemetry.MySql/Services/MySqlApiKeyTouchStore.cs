using System.Text;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="IApiKeyTouchStore"/>. One <c>UPDATE ... WHERE key_hash IN
/// (...)</c> per chunk of distinct keys <see cref="ApiKeyTouchWorker"/> drained for this interval —
/// the same chunk size the bulk writer's <c>BulkInsertAsync</c> uses, for the same reason (stay
/// well under MySQL's placeholder limits without a special case for a pathological tenant count).
/// </summary>
public class MySqlApiKeyTouchStore(IConfiguration configuration) : IApiKeyTouchStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    private const int ChunkSize = 500;

    public async Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken)
    {
        if (keyHashes.Count == 0) return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var list = keyHashes as IReadOnlyList<string> ?? keyHashes.ToList();
        for (var offset = 0; offset < list.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, list.Count - offset);
            var sb = new StringBuilder("UPDATE api_keys SET last_used_at = UTC_TIMESTAMP(6) WHERE key_hash IN (");
            await using var cmd = new MySqlCommand { Connection = conn };
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                var p = $"@k{i}";
                sb.Append(p);
                cmd.Parameters.AddWithValue(p, list[offset + i]);
            }
            sb.Append(')');
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
