using System;
using System.ComponentModel.DataAnnotations;

namespace Keryhe.Telemetry.Data.Access.Models;

public class ApiKey
{
    public long Id { get; set; }

    [Required]
    public long TenantId { get; set; }

    [Required]
    [StringLength(64)]
    public required string KeyHash { get; set; }

    [Required]
    [StringLength(255)]
    public required string Name { get; set; }

    [Required]
    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
