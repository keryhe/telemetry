using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data.Access.Models;

public class GaugeDataPoint
{
    public long Id { get; set; }
    
    public long MetricId { get; set; }
    
    public long? StartTimeUnixNano { get; set; }
    
    public long TimeUnixNano { get; set; }
    
    public double? ValueDouble { get; set; }
    
    public long? ValueInt { get; set; }
    
    public int Flags { get; set; } = 0;
    
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
}