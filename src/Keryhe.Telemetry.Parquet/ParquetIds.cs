using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Id conversions for the Parquet row model (README D3). A trace id is two <c>UBIGINT</c> columns, the big-endian
/// halves of its 16 bytes (<c>trace_hi</c> is the first 16 hex characters), because DuckDB prunes row groups on
/// integer statistics but not on <c>UUID</c> ones; a span id is one <c>UBIGINT</c> (big-endian). Hex ids exist only at
/// the edges of the application. An absent or malformed id on the write path is zero, never an exception: one bad
/// record must not fail its whole batch.
/// </summary>
internal static class ParquetIds
{
    public static (ulong Hi, ulong Lo) TraceId(string? hex)
        => TryTraceId(hex, out var id) ? id : (0, 0);

    /// <summary>The read-path conversion: false for anything but 32 hex characters (an empty id is zero).</summary>
    public static bool TryTraceId(string? hex, out (ulong Hi, ulong Lo) id)
    {
        id = (0, 0);
        if (string.IsNullOrEmpty(hex)) return true;
        if (hex.Length != 32) return false;
        if (!TryHex64(hex.AsSpan(0, 16), out var hi) || !TryHex64(hex.AsSpan(16, 16), out var lo)) return false;
        id = (hi, lo);
        return true;
    }

    public static string TraceIdHex(ulong hi, ulong lo)
        => hi.ToString("x16", CultureInfo.InvariantCulture) + lo.ToString("x16", CultureInfo.InvariantCulture);

    public static ulong SpanId(string? hex)
        => TrySpanId(hex, out var id) ? id : 0UL;

    public static bool TrySpanId(string? hex, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(hex)) return true;
        return hex.Length == 16 && TryHex64(hex, out id);
    }

    public static string SpanIdHex(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);

    private static bool TryHex64(ReadOnlySpan<char> hex, out ulong value)
        => ulong.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    // ---- hashes (the ClickHouse R6 rule) ----

    /// <summary>A 64-bit hash of <paramref name="key"/>: the first 8 bytes of its SHA-256 digest.</summary>
    public static ulong Hash64(string key)
        => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// A point's series identity: tenant, resource, scope, metric name and type (<paramref name="prefix"/>, computed once per metric)
    /// and the point's own attributes, whose keys are sorted before hashing.
    /// </summary>
    public static ulong SeriesId(string prefix, Dictionary<string, object>? pointAttributes)
        => Hash64(string.Concat(prefix, "\n", Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers.SerializeDeterministicJson(pointAttributes)));

    public static string SeriesPrefix(long tenantId, string resourceKey, string scopeKey, string metricName, MetricType type)
        => $"{tenantId}\n{resourceKey}\n{scopeKey}\n{metricName}\n{type}";

    /// <summary>
    /// The id a metric has in the API (<c>MetricInfo.Id</c>, <c>MetricSeriesQuery.MetricId</c>): a hash of the catalog key (tenant,
    /// service, metric name, type), the same in the writer and the reader, as a signed 64-bit integer.
    /// </summary>
    public static long MetricId(long tenantId, string? serviceName, string metricName, MetricType type)
        => unchecked((long)Hash64($"{tenantId}\n{serviceName ?? ""}\n{metricName}\n{type}"));
}
