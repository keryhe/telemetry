using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Keryhe.Telemetry.Data.Access.Models;

public class SchemaVersion
{
    [Key]
    [StringLength(20)]
    public string Version { get; set; } = null!;
    
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}