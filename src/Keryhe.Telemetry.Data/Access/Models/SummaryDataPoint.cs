using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Keryhe.Telemetry.Core;
using System.Text.Json;

namespace Keryhe.Telemetry.Data.Access.Models;

public class SummaryDataPoint
{
    public long Id { get; set; }
    
    public long MetricId { get; set; }
    
    public long? StartTimeUnixNano { get; set; }
    
    public long TimeUnixNano { get; set; }
    
    public long Count { get; set; }
    
    public double SumValue { get; set; }
    
    public string? QuantileValues { get; set; } // JSON array of {quantile, value} objects
    
    public int Flags { get; set; } = 0;
    
    public string? AttributesJson { get; set; }

    // Navigation properties
    public virtual Metric Metric { get; set; } = null!;

    // Helper property for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? Attributes
    {
        get => string.IsNullOrEmpty(AttributesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(AttributesJson);
        set => AttributesJson = value == null ? null : JsonSerializer.Serialize(value);
    }
    
    [NotMapped]
    public QuantileValue[]? QuantileValuesArray
    {
        get => string.IsNullOrEmpty(QuantileValues) ? null : JsonSerializer.Deserialize<QuantileValue[]>(QuantileValues);
        set => QuantileValues = value == null ? null : JsonSerializer.Serialize(value);
    }
}

public class QuantileValue
{
    public double Quantile { get; set; }
    public double Value { get; set; }
}