using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Keryhe.Telemetry.Core.Models;
using System.Text.Json;

namespace Keryhe.Telemetry.Data.Access.Models;

public class LogRecord
{
    public long Id { get; set; }
    
    public long ResourceId { get; set; }
    
    public long ScopeId { get; set; }
    
    public long? TimeUnixNano { get; set; }
    
    public long? ObservedTimeUnixNano { get; set; }
    
    public int? SeverityNumber { get; set; } // 1-24
    
    [StringLength(32)]
    public string? SeverityText { get; set; }
    
    public AttributeType? BodyType { get; set; } = AttributeType.STRING;
    
    public string? BodyValue { get; set; }
    
    public int DroppedAttributesCount { get; set; } = 0;
    
    public int Flags { get; set; } = 0;
    
    public string? TraceId { get; set; } // 16 bytes
    
    public string? SpanId { get; set; } // 8 bytes
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public string? AttributesJson { get; set; }

    // Navigation properties
    public virtual Resource Resource { get; set; } = null!;
    public virtual InstrumentationScope Scope { get; set; } = null!;
    
    // Helper properties for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? Attributes
    {
        get => string.IsNullOrEmpty(AttributesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(AttributesJson);
        set => AttributesJson = value == null ? null : JsonSerializer.Serialize(value);
    }
}