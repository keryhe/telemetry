using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Keryhe.Telemetry.Core.Models;
using System.Text.Json;

namespace Keryhe.Telemetry.Data.Access.Models;

public class HistogramDataPoint
{
    public long Id { get; set; }

    public long MetricId { get; set; }

    public long? StartTimeUnixNano { get; set; }

    public long TimeUnixNano { get; set; }

    public long Count { get; set; }

    public double? SumValue { get; set; }

    public string? BucketCounts { get; set; } // JSON array of bucket counts

    public string? ExplicitBounds { get; set; } // JSON array of explicit bucket bounds

    public AggregationTemporality AggregationTemporality { get; set; } = AggregationTemporality.UNSPECIFIED;

    public int Flags { get; set; } = 0;

    [Column("Min_Value")] public double? MinValue { get; set; }

    [Column("Max_Value")] public double? MaxValue { get; set; }

    public long? ExemplarId { get; set; }

    public string? AttributesJson { get; set; }

    // Navigation properties
    public virtual Metric Metric { get; set; } = null!;
    public virtual Exemplar? Exemplar { get; set; }

    // Helper properties for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? Attributes
    {
        get => string.IsNullOrEmpty(AttributesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(AttributesJson);
        set => AttributesJson = value == null ? null : JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public long[]? BucketCountsArray
    {
        get => string.IsNullOrEmpty(BucketCounts) ? null : JsonSerializer.Deserialize<long[]>(BucketCounts);
        set => BucketCounts = value == null ? null : JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public double[]? ExplicitBoundsArray
    {
        get => string.IsNullOrEmpty(ExplicitBounds) ? null : JsonSerializer.Deserialize<double[]>(ExplicitBounds);
        set => ExplicitBounds = value == null ? null : JsonSerializer.Serialize(value);
    }
}