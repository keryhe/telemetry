using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SQL Server implementation of <see cref="IApiKeyTouchStore"/>. One <c>UPDATE ... WHERE key_hash
/// IN (...)</c> per chunk of distinct keys <see cref="ApiKeyTouchWorker"/> drained for this
/// interval, chunked to stay comfortably under SQL Server's 2100-parameter-per-statement limit —
/// the count of distinct keys touched in one flush interval is realistically small, but this keeps
/// a pathological tenant count safe without a special case.
/// </summary>
public class SqlServerApiKeyTouchStore(IConfiguration configuration) : IApiKeyTouchStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    private const int ChunkSize = 1000;

    public async Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken)
    {
        if (keyHashes.Count == 0) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var list = keyHashes as IReadOnlyList<string> ?? keyHashes.ToList();
        for (var offset = 0; offset < list.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, list.Count - offset);
            var sb = new StringBuilder("UPDATE api_keys SET last_used_at = SYSDATETIME() WHERE key_hash IN (");
            await using var cmd = new SqlCommand { Connection = conn };
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
