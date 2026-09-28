using System.Text.Json;
using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>Turns a captured <c>/api</c> URL into a stable template and pulls the summary markers out of a response body. Pure.</summary>
public static partial class ApiRequestNormalizer
{
    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex Hex32();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();

    /// <summary>
    /// <c>/telemetry/../api/traces/4bf9...c1/spans?x=1</c> becomes <c>traces/{id}/spans</c>: everything before <c>api/</c> and the query
    /// string are dropped, trace ids and numbers become <c>{id}</c>/<c>{n}</c>, and the metric name after <c>by-name</c> or <c>labels</c> becomes <c>{name}</c>.
    /// </summary>
    public static string Template(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Split('?')[0];
        var at = path.IndexOf("/api/", StringComparison.Ordinal);
        if (at >= 0) path = path[(at + 5)..];
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToArray();
        for (var i = 0; i < segments.Length; i++)
        {
            if (Hex32().IsMatch(segments[i])) segments[i] = "{id}";
            else if (Digits().IsMatch(segments[i])) segments[i] = "{n}";
            else if (i > 0 && segments[i - 1] is "by-name" or "labels") segments[i] = "{name}";
        }
        return string.Join('/', segments);
    }

    public static bool IsApi(string url) => url.Contains("/api/", StringComparison.Ordinal);

    /// <summary>Endpoints whose body says whether a summary came from a rollup, was cut short, or timed out, which is <em>why</em> a call was slow.</summary>
    public static bool CarriesMarkers(string template) =>
        template.EndsWith("summary", StringComparison.Ordinal) || template.EndsWith("page", StringComparison.Ordinal) ||
        template.EndsWith("series", StringComparison.Ordinal) || template.EndsWith("catalog", StringComparison.Ordinal);

    /// <summary><c>source</c> (rollup or raw), <c>totalIsLowerBound</c> (the "≥ N" marker) and <c>timedOut</c> from a JSON body, each null when absent or the body is not JSON.</summary>
    public static (string? Source, bool? LowerBound, bool? TimedOut) Markers(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? source = null; bool? lower = null, timedOut = null;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("source", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) source = p.Value.GetString();
                else if (p.Name.Equals("totalIsLowerBound", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) lower = p.Value.GetBoolean();
                else if (p.Name.Equals("timedOut", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) timedOut = p.Value.GetBoolean();
            }
            return (source, lower, timedOut);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}
