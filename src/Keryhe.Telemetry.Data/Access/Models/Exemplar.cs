using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Keryhe.Telemetry.Core;
using System.Text.Json;

namespace Keryhe.Telemetry.Data.Access.Models;

public class Exemplar
{
    public long Id { get; set; }
    
    public string? FilteredAttributes { get; set; } // JSON key-value pairs
    
    public long TimeUnixNano { get; set; }
    
    public double? ValueDouble { get; set; }
    
    public long? ValueInt { get; set; }
    
    public string? SpanId { get; set; } // 8 bytes
    
    public string? TraceId { get; set; } // 16 bytes

    // Helper property for JSON deserialization
    [NotMapped]
    public Dictionary<string, object>? FilteredAttributesDictionary
    {
        get => string.IsNullOrEmpty(FilteredAttributes) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(FilteredAttributes);
        set => FilteredAttributes = value == null ? null : JsonSerializer.Serialize(value);
    }
}