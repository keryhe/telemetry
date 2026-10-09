using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Converts an OTLP attribute set to the <c>Map(LowCardinality(String), String)</c> every table stores (row model,
/// "Conventions"). One class for every table's row builder, so spans, logs, points and resources encode alike.
/// A value is written as: string as-is; int, double and bool as their text (<c>42</c>, <c>0.5</c>,
/// <c>true</c>); array and kvlist as JSON text; bytes as base64. The original type is not kept, so a numeric
/// comparison casts (<c>toFloat64OrNull(attributes['k'])</c>).
/// </summary>
internal static class ClickHouseAttributes
{
    public static Dictionary<string, string> ToMap(Dictionary<string, object>? attributes)
    {
        if (attributes is not { Count: > 0 }) return new Dictionary<string, string>(0);

        var map = new Dictionary<string, string>(attributes.Count, StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
            map[key] = ValueToString(value);
        return map;
    }

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
