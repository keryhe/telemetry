using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Keryhe.Telemetry.Collector.Http;

/// <summary>
/// Reads OTLP/JSON. The OTLP JSON mapping is the protobuf JSON mapping with two differences that matter here: trace, span and parent-span
/// ids are <b>hex</b> strings (protobuf JSON would expect base64 for a bytes field), and enum values are integers (which the protobuf parser
/// accepts as well as names). Other bytes fields (<c>bytesValue</c>) stay base64, 64-bit integers may be strings or numbers, and unknown
/// fields are ignored. So: the id fields are rewritten from hex to base64 in a DOM pass, then Google.Protobuf's parser does the rest.
/// </summary>
public static class OtlpJsonReader
{
    private static readonly HashSet<string> IdFields = new(StringComparer.Ordinal) { "traceId", "spanId", "parentSpanId" };

    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>Parses <paramref name="json"/> as the OTLP/JSON form of <typeparamref name="T"/>; throws <see cref="InvalidOtlpBodyException"/> for malformed input.</summary>
    public static T Parse<T>(ReadOnlySpan<byte> json) where T : IMessage<T>, new()
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
            if (root is null) throw new InvalidOtlpBodyException("The JSON body is empty.");
            RewriteIds(root);
            return Parser.Parse<T>(root.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidJsonException or InvalidProtocolBufferException or FormatException or InvalidOperationException)
        {
            throw new InvalidOtlpBodyException("The JSON body is not a valid OTLP export: " + ex.Message, ex);
        }
    }

    private static void RewriteIds(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToList())
                {
                    if (value is JsonValue v && IdFields.Contains(name) && v.TryGetValue<string>(out var hex))
                        obj[name] = HexToBase64(name, hex);
                    else if (value is not null)
                        RewriteIds(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null) RewriteIds(item);
                break;
        }
    }

    private static string HexToBase64(string field, string hex)
    {
        if (hex.Length == 0) return "";
        try { return Convert.ToBase64String(Convert.FromHexString(hex)); }
        catch (FormatException) { throw new InvalidOtlpBodyException($"'{field}' must be a hex string (was '{(hex.Length > 40 ? hex[..40] + "..." : hex)}')."); }
    }
}

/// <summary>The request body could not be read as an OTLP export (a 400 to the client).</summary>
public sealed class InvalidOtlpBodyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The body is larger than <c>MaxReceiveMessageSizeBytes</c> once decompressed (a 413 to the client).</summary>
public sealed class PayloadTooLargeException(long limit) : Exception($"The request body is larger than {limit} bytes after decompression.");
