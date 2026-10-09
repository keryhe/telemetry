using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Id conversions and hashes for the ClickHouse row model (plans/clickhouse-redesign README R6, R8).
///
/// Trace ids are stored as <c>UUID</c> and span ids as <c>UInt64</c>: <c>ClickHouse.Client</c> cannot write raw
/// bytes into a <c>FixedString</c>, so the 16 and 8 id bytes ride in a <see cref="Guid"/> and a <see cref="ulong"/>.
/// The mapping only has to be reversible and used everywhere; the OTLP hex form exists only at the edges of the app.
/// Nothing may read an id back with SQL-side <c>hex(trace_id)</c>, which does not give the OTLP hex id. A zero id
/// (absent, or a root's parent) is <see cref="Guid.Empty"/> / 0.
/// </summary>
internal static class ClickHouseIds
{
    // ---- trace id <-> Guid ----

    /// <summary>
    /// The write-path conversion: an absent or malformed id is <see cref="Guid.Empty"/>. A malformed id must never
    /// throw here, or one bad record would fail (and eventually drop) its whole batch.
    /// </summary>
    public static Guid TraceIdToGuid(string? hex)
        => TryTraceIdToGuid(hex, out var id) ? id : Guid.Empty;

    /// <summary>The read-path conversion: false for anything but 32 hex characters, which the caller answers with a 400.</summary>
    public static bool TryTraceIdToGuid(string? hex, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrEmpty(hex)) return true;
        if (hex.Length != 32) return false;
        Span<byte> bytes = stackalloc byte[16];
        if (Convert.FromHexString(hex.AsSpan(), bytes, out _, out _) != System.Buffers.OperationStatus.Done) return false;
        id = new Guid(bytes);
        return true;
    }

    public static string GuidToTraceIdHex(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    // ---- span id <-> UInt64 ----

    public static ulong SpanIdToUInt64(string? hex)
        => TrySpanIdToUInt64(hex, out var id) ? id : 0UL;

    public static bool TrySpanIdToUInt64(string? hex, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(hex)) return true;
        if (hex.Length != 16) return false;
        Span<byte> bytes = stackalloc byte[8];
        if (Convert.FromHexString(hex.AsSpan(), bytes, out _, out _) != System.Buffers.OperationStatus.Done) return false;
        id = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    public static string UInt64ToSpanIdHex(ulong id)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
        return Convert.ToHexStringLower(bytes);
    }

    // ---- hashes ----

    /// <summary>
    /// A 64-bit hash of <paramref name="key"/>: the first 8 bytes of its SHA-256 digest.
    /// </summary>
    public static ulong Hash64(string key)
        => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// A point's series identity: tenant, resource (schema URL and attributes), scope (name, version, attributes),
    /// metric name and type, and the point's own attributes. <paramref name="prefix"/> is
    /// <see cref="SeriesPrefix"/>, computed once per metric, so a point only adds its own attribute set. Attribute
    /// keys are sorted before hashing, so their order never changes the id.
    /// </summary>
    public static ulong SeriesId(string prefix, Dictionary<string, object>? pointAttributes)
        => Hash64(string.Concat(prefix, "\n", TelemetryAttributesJson(pointAttributes)));

    public static string SeriesPrefix(long tenantId, string resourceKey, string scopeKey, string metricName, MetricType type)
        => $"{tenantId}\n{resourceKey}\n{scopeKey}\n{metricName}\n{type}";

    /// <summary>
    /// The id a metric has in <c>metric_catalog</c> and in the API (<c>MetricInfo.Id</c>,
    /// <c>MetricSeriesQuery.MetricId</c>): a hash of the catalog key (tenant, service, metric name, type), the same
    /// in the writer and the reader (README R6). Reinterpreted as a signed 64-bit integer, the API's id type.
    /// </summary>
    public static long MetricId(long tenantId, string? serviceName, string metricName, MetricType type)
        => unchecked((long)Hash64($"{tenantId}\n{serviceName ?? ""}\n{metricName}\n{type}"));

    private static string TelemetryAttributesJson(Dictionary<string, object>? attributes)
        => Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers.SerializeDeterministicJson(attributes);
}
