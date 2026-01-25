using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Access.Models;

public class Span
{
    public long Id { get; set; }
    
    [Required]
    public string TraceId { get; set; } = null!; // 16 bytes
    
    [Required]
    public string SpanId { get; set; } = null!; // 8 bytes
    
    public string? ParentSpanId { get; set; } // 8 bytes
    
    public long ResourceId { get; set; }
    
    public long ScopeId { get; set; }
    
    [Required]
    [StringLength(255)]
    public string Name { get; set; } = null!;
    
    public SpanKind Kind { get; set; } = SpanKind.UNSPECIFIED;
    
    public long StartTimeUnixNano { get; set; }
    
    public long EndTimeUnixNano { get; set; }
    
    public int DroppedAttributesCount { get; set; } = 0;
    
    public int DroppedEventsCount { get; set; } = 0;
    
    public int DroppedLinksCount { get; set; } = 0;
    
    public string? TraceState { get; set; }
    
    public SpanStatusCode StatusCode { get; set; } = SpanStatusCode.UNSET;
    
    public string? StatusMessage { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    public string? AttributesJson { get; set; }

    // Navigation properties
    public virtual Resource Resource { get; set; } = null!;
    public virtual InstrumentationScope Scope { get; set; } = null!;
    public virtual ICollection<SpanEvent> Events { get; set; } = new List<SpanEvent>();
    public virtual ICollection<SpanLink> Links { get; set; } = new List<SpanLink>();
    
    // Helper properties for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? Attributes
    {
        get => string.IsNullOrEmpty(AttributesJson) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(AttributesJson);
        set => AttributesJson = value == null ? null : JsonSerializer.Serialize(value);
    }
}
