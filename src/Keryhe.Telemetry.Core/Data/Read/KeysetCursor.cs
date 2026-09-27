using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Opaque decoded cursor for keyset (not offset) paging (list-pages-server-side plan, Phase 1,
/// decision 1). <c>K</c>/<c>Id</c> are the sort-key/tiebreak pair every list's default order pages
/// on — <c>(created_at, id)</c>-shaped, always <see cref="long"/> here (nanosecond timestamps and
/// database-generated bigint ids on every table this applies to). <c>F</c> is a hash of the
/// request's filters plus <c>asOf</c>, checked by <see cref="KeysetCursor.Decode"/> callers via
/// <see cref="KeysetCursor.ComputeFilterHash"/> so a stale or cross-query cursor is rejected rather
/// than silently returning rows from a different filter set. <c>V</c> is a format version, fixed
/// at 1 for now.
/// </summary>
public sealed record DecodedCursor(long K, long Id, string F, int V = 1);

/// <summary>
/// Encode/decode and predicate generation for keyset cursors (list-pages-server-side plan, Phase
/// 1). One repository per list; every list this plan touches has exactly one <c>(sort key,
/// tiebreak)</c> pair, its default order (decision 4), so there is no per-request choice of sort
/// column to thread through here.
/// </summary>
public static class KeysetCursor
{
    private sealed class CursorDto
    {
        [JsonPropertyName("k")] public long K { get; set; }
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("f")] public string F { get; set; } = "";
        [JsonPropertyName("v")] public int V { get; set; } = 1;
    }

    /// <summary>Encodes a cursor as opaque base64url JSON: <c>{ k, id, f, v }</c> (Target API's exact documented shape).</summary>
    public static string Encode(long sortKey, long tiebreak, string filterHash)
    {
        var dto = new CursorDto { K = sortKey, Id = tiebreak, F = filterHash, V = 1 };
        var json = JsonSerializer.Serialize(dto);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Decodes an opaque cursor, or returns null for anything malformed — callers treat a null
    /// decode as an invalid-cursor error, same as a filter-hash mismatch.
    /// </summary>
    public static DecodedCursor? Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
            var dto = JsonSerializer.Deserialize<CursorDto>(json);
            return dto == null ? null : new DecodedCursor(dto.K, dto.Id, dto.F, dto.V);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stable hash of the request's filters plus <c>asOf</c> (decision 3/Target API's cursor
    /// format note: "the filter hash covers every filter AND asOf"). The caller builds
    /// <paramref name="canonicalFilterText"/> from its own filter set in a fixed field order
    /// (e.g. <c>$"{start}|{end}|{asOf}|{service}|{q}|..."</c>) — this method just hashes it, so it
    /// stays agnostic of which endpoint's filter shape is being hashed.
    /// </summary>
    public static string ComputeFilterHash(string canonicalFilterText)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonicalFilterText));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>True when a decoded cursor's filter hash matches the current request's — a mismatch means a stale or cross-query cursor.</summary>
    public static bool MatchesFilterHash(DecodedCursor cursor, string canonicalFilterText)
        => string.Equals(cursor.F, ComputeFilterHash(canonicalFilterText), StringComparison.Ordinal);

    /// <summary>
    /// The keyset predicate for "rows after this cursor", in the expanded form
    /// <c>k &lt;= @k AND (k &lt; @k OR id &lt; @id)</c> for descending order (the newest-first
    /// default every list in this plan uses) or its ascending mirror — NOT a row-value comparison
    /// <c>(k, id) &lt; (@k, @id)</c>, since SQL Server has no row-value syntax and MySQL optimizes
    /// row values poorly. <paramref name="sortColumn"/>/<paramref name="tiebreakColumn"/> are
    /// literal column references (e.g. <c>"created_at"</c>/<c>"id"</c>), and the caller binds
    /// <c>@{keyParam}</c>/<c>@{idParam}</c> to the decoded cursor's <see cref="DecodedCursor.K"/>/
    /// <see cref="DecodedCursor.Id"/>.
    /// </summary>
    public static string Predicate(string sortColumn, string tiebreakColumn, string keyParam, string idParam, bool descending)
    {
        var lt = descending ? "<" : ">";
        var lte = descending ? "<=" : ">=";
        return $"({sortColumn} {lte} @{keyParam} AND ({sortColumn} {lt} @{keyParam} OR {tiebreakColumn} {lt} @{idParam}))";
    }

    /// <summary>
    /// Last-page row count (decision 2): with an exact total, the last page holds
    /// <c>total mod size</c> rows (or <paramref name="pageSize"/> itself when that's 0), so it
    /// never overlaps the previous page. With a lower-bound total (the count timed out — decision
    /// 11), there is no exact remainder to compute; the caller instead runs the page query in
    /// reverse (<c>nav=last</c>) and takes <paramref name="pageSize"/> rows, labelling the page
    /// "last N rows" rather than by position.
    /// </summary>
    public static int LastPageRowCount(long? exactTotal, int pageSize)
    {
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be positive.");
        if (exactTotal is not { } total || total <= 0)
            return pageSize;

        var remainder = total % pageSize;
        return remainder == 0 ? pageSize : (int)remainder;
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url string.");
        }
        return Convert.FromBase64String(s);
    }
}
