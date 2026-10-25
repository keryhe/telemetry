using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Converts an OTLP attribute set to the <c>MAP(VARCHAR, VARCHAR)</c> every table stores (README D4), the ClickHouse
/// provider's R7 rule: a string as is; int, double and bool as their text; array and kvlist as JSON text; bytes as base64.
/// The original type is not kept, so a numeric comparison casts. The map is serialized to JSON text for the staging
/// table (the appender cannot write a <c>MAP</c>) and converted by the <c>COPY</c>.
/// </summary>
internal static class ParquetAttributes
{
    public static Dictionary<string, string> ToMap(Dictionary<string, object>? attributes)
    {
        if (attributes is not { Count: > 0 }) return new Dictionary<string, string>(0);

        var map = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
            map[key] = ValueToString(value);
        return map;
    }

    /// <summary>The staging form of an attribute set: a JSON object of string values.</summary>
    public static string ToJson(Dictionary<string, object>? attributes)
        => attributes is not { Count: > 0 } ? "{}" : JsonSerializer.Serialize(ToMap(attributes));

    public static string ValueToString(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
        JsonElement je => je.GetRawText(),
        IDictionary or IEnumerable => JsonSerializer.Serialize(value),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}
