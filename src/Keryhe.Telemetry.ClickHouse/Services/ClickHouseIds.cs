using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Application-side surrogate-key generation. ClickHouse has no auto-increment or
/// <c>RETURNING</c>, so the bulk writer computes the <c>Int64</c> ids that the relational
/// providers rely on the database to generate. Ids for deduplicated rows are DETERMINISTIC
/// (derived from the same key the table dedups on) so that re-inserting an identical
/// resource/scope/span produces an identical row, which <c>ReplacingMergeTree</c> collapses.
///
/// "the same key the table dedups on" is exact, not approximate. <c>resources</c> is
/// <c>ORDER BY (tenant_id, resource_hash)</c>, so its id must be derived from BOTH -- deriving it
/// from the hash alone gave two tenants with identical resources two rows sharing one id, which the
/// engine cannot collapse (id is not in the sort key) and which every read then joins to the wrong
/// tenant. <c>instrumentation_scopes</c> is <c>ORDER BY scope_hash</c> with no tenant column, so the
/// hash alone is genuinely its whole key.
/// </summary>
internal static class ClickHouseIds
{
    /// <summary>
    /// Deterministic <c>Int64</c> from a 64-char lowercase hex SHA-256 hash — the first
    /// 8 bytes reinterpreted as a signed 64-bit integer. Collision probability is negligible
    /// at realistic resource/scope cardinalities.
    /// </summary>
    public static long FromHash(string hash)
        => unchecked((long)ulong.Parse(hash.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    /// <summary>Deterministic <c>Int64</c> from an arbitrary natural key (e.g. trace_id + span_id).</summary>
    public static long FromKey(string key)
        => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

/// <summary>
/// Monotonic in-process id generator for rows that have no natural dedup key (span
/// events/links, alert events). Seeded from the clock so ids stay roughly increasing
/// across process restarts. Uniqueness is per-process, which is sufficient because these
/// rows are never deduplicated on re-ingest.
/// </summary>
internal static class RowId
{
    private static long _seq = DateTime.UtcNow.Ticks;
    public static long Next() => Interlocked.Increment(ref _seq);
}
