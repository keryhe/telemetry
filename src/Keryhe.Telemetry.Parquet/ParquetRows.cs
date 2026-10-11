using System.Text.Json;

namespace Keryhe.Telemetry.Parquet;

/// <summary>Helpers for the repositories that read rows with nested columns as JSON text (<c>to_json(col)</c>).</summary>
internal static class ParquetRows
{
    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>A <c>MAP(VARCHAR, VARCHAR)</c> read as JSON: a dictionary of strings (never null).</summary>
    public static Dictionary<string, object> ToObjects(string? json)
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrEmpty(json)) return result;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject()) result[p.Name] = p.Value.GetString() ?? "";
        return result;
    }

    public static Dictionary<string, string> ToStrings(string? json)
        => ToObjects(json).ToDictionary(kv => kv.Key, kv => (string)kv.Value);

    /// <summary>A numeric <c>LIST</c> read as JSON.</summary>
    public static double[] ToDoubles(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.GetDouble())] : [];
    }

    public static ulong[] ToULongs(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? [.. doc.RootElement.EnumerateArray().Select(e => e.GetUInt64())] : [];
    }
}
