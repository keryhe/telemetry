using Google.Protobuf.Collections;
using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// The one place OTLP attributes and values become .NET values (it used to be copied into each of the three services), and
/// where the configured limits (<see cref="IngestionLimitsOptions"/>) are applied: attribute count, key and value length,
/// nesting depth and elements per level. Nothing is rejected for exceeding a limit; the value is cut, the cut is counted on
/// <c>records_truncated</c>, and the caller raises the record's own dropped-attributes count by what <c>dropped</c> reports.
/// Stateless and thread-safe: a singleton shared by the services (and, later, other transports).
/// </summary>
public sealed class OtlpAttributeConverter(IOptions<IngestionLimitsOptions> limits, IngestionMetrics metrics)
{
    private readonly IngestionLimitsOptions _limits = limits.Value;

    public IngestionLimitsOptions Limits => _limits;

    public Dictionary<string, object> ConvertAttributes(RepeatedField<KeyValue>? attributes, string signal) =>
        ConvertAttributes(attributes, signal, out _);

    /// <param name="dropped">Attributes beyond <see cref="IngestionLimitsOptions.MaxAttributes"/> that were not kept.</param>
    public Dictionary<string, object> ConvertAttributes(RepeatedField<KeyValue>? attributes, string signal, out int dropped)
    {
        dropped = 0;
        var result = new Dictionary<string, object>();
        if (attributes == null) return result;

        var max = _limits.MaxAttributes;
        foreach (var attr in attributes)
        {
            if (string.IsNullOrEmpty(attr.Key) || attr.Value == null) continue;

            if (max > 0 && result.Count >= max)
            {
                dropped++;
                continue;
            }

            var value = ConvertAnyValue(attr.Value, signal, 1);
            if (value == null) continue;
            result[Clip(attr.Key, _limits.MaxAttributeKeyLength, signal, "attribute_key")!] = value;
        }

        if (dropped > 0) metrics.RecordTruncated(signal, "attributes", dropped);
        return result;
    }

    /// <summary>Converts a value found at nesting <paramref name="depth"/> (1 = an attribute's own value); null when it is cut by the depth limit or empty.</summary>
    public object? ConvertAnyValue(AnyValue anyValue, string signal, int depth = 1) => anyValue.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => Clip(anyValue.StringValue, _limits.MaxAttributeValueLength, signal, "attribute_value"),
        AnyValue.ValueOneofCase.BoolValue => anyValue.BoolValue,
        AnyValue.ValueOneofCase.IntValue => anyValue.IntValue,
        AnyValue.ValueOneofCase.DoubleValue => anyValue.DoubleValue,
        AnyValue.ValueOneofCase.BytesValue => ConvertBytes(anyValue.BytesValue, signal),
        AnyValue.ValueOneofCase.ArrayValue => TooDeep(depth, signal) ? null : ConvertArrayValue(anyValue.ArrayValue, signal, depth),
        AnyValue.ValueOneofCase.KvlistValue => TooDeep(depth, signal) ? null : ConvertKeyValueList(anyValue.KvlistValue, signal, depth),
        _ => anyValue.ToString()
    };

    private byte[] ConvertBytes(Google.Protobuf.ByteString bytes, string signal)
    {
        var max = _limits.MaxAttributeValueLength;
        if (max > 0 && bytes.Length > max)
        {
            metrics.RecordTruncated(signal, "attribute_value");
            return bytes.Span[..max].ToArray();
        }
        return bytes.ToByteArray();
    }

    private bool TooDeep(int depth, string signal)
    {
        if (_limits.MaxNestingDepth <= 0 || depth <= _limits.MaxNestingDepth) return false;
        metrics.RecordTruncated(signal, "depth");
        return true;
    }

    public object[] ConvertArrayValue(ArrayValue arrayValue, string signal, int depth = 1)
    {
        var result = new List<object>(Math.Min(arrayValue.Values.Count, 64));
        var max = _limits.MaxElementsPerLevel;
        foreach (var element in arrayValue.Values)
        {
            if (max > 0 && result.Count >= max)
            {
                metrics.RecordTruncated(signal, "elements");
                break;
            }
            var value = ConvertAnyValue(element, signal, depth + 1);
            if (value != null) result.Add(value);
        }
        return result.ToArray();
    }

    public Dictionary<string, object> ConvertKeyValueList(KeyValueList kvList, string signal, int depth = 1)
    {
        var result = new Dictionary<string, object>();
        var max = _limits.MaxElementsPerLevel;
        foreach (var kv in kvList.Values)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
            if (max > 0 && result.Count >= max)
            {
                metrics.RecordTruncated(signal, "elements");
                break;
            }
            var value = ConvertAnyValue(kv.Value, signal, depth + 1);
            if (value != null) result[Clip(kv.Key, _limits.MaxAttributeKeyLength, signal, "attribute_key")!] = value;
        }
        return result;
    }

    /// <summary>Clips <paramref name="value"/> to <paramref name="max"/> characters (0 = unlimited), counting a clip as <paramref name="limit"/> on <paramref name="signal"/>.</summary>
    public string? Clip(string? value, int max, string signal, string limit)
    {
        var clipped = ColumnLimits.Clip(value, max, out var was);
        if (was) metrics.RecordTruncated(signal, limit);
        return clipped;
    }

    /// <summary>Clips a value stored in a sized column to <paramref name="columnSize"/> (always applied, whatever the configured limits).</summary>
    public string? ClipColumn(string? value, int columnSize, string signal, string column) => Clip(value, columnSize, signal, column);

    /// <summary>
    /// How many of <paramref name="count"/> items (events or links of one span) may be kept under <paramref name="max"/> (0 = all),
    /// counting the rest as truncated; <paramref name="dropped"/> is how many were cut.
    /// </summary>
    public int Allow(int count, int max, string signal, string limit, out int dropped)
    {
        dropped = 0;
        if (max <= 0 || count <= max) return count;
        dropped = count - max;
        metrics.RecordTruncated(signal, limit, dropped);
        return max;
    }

    /// <summary>
    /// The resource's attributes with <c>service.name</c> clipped to its column (it becomes the <c>service_name</c> columns and
    /// the rollup keys), so the same oversized service always maps to the same value.
    /// </summary>
    public Dictionary<string, object> ClipServiceName(Dictionary<string, object> resourceAttributes, string signal)
    {
        if (resourceAttributes.TryGetValue("service.name", out var v) && v is string s)
            resourceAttributes["service.name"] = ClipColumn(s, ColumnLimits.ServiceName, signal, "service_name")!;
        return resourceAttributes;
    }
}
