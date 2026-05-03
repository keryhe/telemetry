using System.ComponentModel.DataAnnotations;

namespace Keryhe.Telemetry.Data.Access.Models;

public class Tenant
{
    public long Id { get; set; }

    [Required]
    [StringLength(255)]
    public string Name { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}