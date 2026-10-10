namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Caps applied while an OTLP export is converted, bound from <c>Telemetry:Ingestion:Limits</c>. Nothing is rejected for
/// exceeding one: values are truncated and lists cut, the record's own dropped-attributes/events/links count is raised by
/// what was cut (as an SDK would), and each cut is counted on <c>records_truncated</c>. 0 means unlimited for any of them.
/// </summary>
public sealed class IngestionLimitsOptions
{
    public const string SectionName = "Telemetry:Ingestion:Limits";

    /// <summary>Attributes kept per span, log record, event, link, data point, resource or scope; the rest are dropped.</summary>
    public int MaxAttributes { get; set; } = 128;

    /// <summary>Characters of an attribute key (longer keys are truncated).</summary>
    public int MaxAttributeKeyLength { get; set; } = 256;

    /// <summary>Characters of a string attribute value, and bytes of a bytes value.</summary>
    public int MaxAttributeValueLength { get; set; } = 16_384;

    /// <summary>Events kept per span.</summary>
    public int MaxEventsPerSpan { get; set; } = 128;

    /// <summary>Links kept per span.</summary>
    public int MaxLinksPerSpan { get; set; } = 128;

    /// <summary>Characters of a log body.</summary>
    public int MaxLogBodyLength { get; set; } = 65_536;

    /// <summary>Levels of nested array/map values; deeper values are dropped.</summary>
    public int MaxNestingDepth { get; set; } = 32;

    /// <summary>Elements kept at each level of an array or map value.</summary>
    public int MaxElementsPerLevel { get; set; } = 1_024;

    public void Validate()
    {
        void NonNegative(int v, string name)
        {
            if (v < 0) throw new InvalidOperationException($"{SectionName}:{name} must not be negative (was {v}); 0 means unlimited.");
        }
        NonNegative(MaxAttributes, nameof(MaxAttributes));
        NonNegative(MaxAttributeKeyLength, nameof(MaxAttributeKeyLength));
        NonNegative(MaxAttributeValueLength, nameof(MaxAttributeValueLength));
        NonNegative(MaxEventsPerSpan, nameof(MaxEventsPerSpan));
        NonNegative(MaxLinksPerSpan, nameof(MaxLinksPerSpan));
        NonNegative(MaxLogBodyLength, nameof(MaxLogBodyLength));
        NonNegative(MaxNestingDepth, nameof(MaxNestingDepth));
        NonNegative(MaxElementsPerLevel, nameof(MaxElementsPerLevel));
    }
}
