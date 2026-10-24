using System.Text;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Sqlite.Services;

/// <summary>
/// SQLite implementation of <see cref="IApiKeyTouchStore"/>: one <c>UPDATE ... WHERE key_hash IN (...)</c> per chunk of distinct
/// keys <see cref="ApiKeyTouchWorker"/> drained for this interval, all in one transaction so a touch takes the file's write
/// lock once. 500 hashes per statement leaves SQLite's variable limit (32,766) far behind.
/// </summary>
public sealed class SqliteApiKeyTouchStore(SqliteControlPlaneConnection controlPlane) : IApiKeyTouchStore
{
    private const int ChunkSize = 500;

    public async Task TouchAsync(IReadOnlyCollection<string> keyHashes, CancellationToken cancellationToken)
    {
        if (keyHashes.Count == 0) return;

        await using var conn = await controlPlane.OpenAsync(cancellationToken);
        // IMMEDIATE: the first statement writes, so take the write lock (and wait for it) up front.
        await using var tx = conn.BeginTransaction(deferred: false);
        var list = keyHashes as IReadOnlyList<string> ?? keyHashes.ToList();
        for (var offset = 0; offset < list.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, list.Count - offset);
            var sb = new StringBuilder($"UPDATE api_keys SET last_used_at = {SqliteTime.NowSql} WHERE key_hash IN (");
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
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
        await tx.CommitAsync(cancellationToken);
    }
}
