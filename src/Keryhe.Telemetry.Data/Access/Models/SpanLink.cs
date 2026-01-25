using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data.Access.Models;

public class SpanLink
{
    public long Id { get; set; }
    
    public long SpanId { get; set; }
    
    [Required]
    public string? LinkedTraceId { get; set; } // 16 bytes
    
    [Required]
    public required string LinkedSpanId { get; set; } // 8 bytes
    
    public required string TraceState { get; set; }
    
    public int DroppedAttributesCount { get; set; } = 0;
    
    public string? AttributesJson { get; set; }

    // Navigation properties
    public virtual Span Span { get; set; } = null!;
    
    // Helper properties for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? Attributes
    {
        get => string.IsNullOrEmpty(AttributesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(AttributesJson);
        set => AttributesJson = value == null ? null : JsonSerializer.Serialize(value);
    }
}
