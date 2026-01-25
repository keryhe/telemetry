using System.ComponentModel.DataAnnotations;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Access.Models;

public class Metric
{
    public long Id { get; set; }
    
    public long ResourceId { get; set; }
    
    public long ScopeId { get; set; }
    
    [Required]
    [StringLength(255)]
    public string Name { get; set; } = null!;
    
    public string? Description { get; set; }
    
    [StringLength(63)]
    public string? Unit { get; set; }
    
    public MetricType Type { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual Resource Resource { get; set; } = null!;
    public virtual InstrumentationScope Scope { get; set; } = null!;
    public virtual ICollection<GaugeDataPoint> GaugeDataPoints { get; set; } = new List<GaugeDataPoint>();
    public virtual ICollection<SumDataPoint> SumDataPoints { get; set; } = new List<SumDataPoint>();
    public virtual ICollection<HistogramDataPoint> HistogramDataPoints { get; set; } = new List<HistogramDataPoint>();
    public virtual ICollection<ExponentialHistogramDataPoint> ExponentialHistogramDataPoints { get; set; } = new List<ExponentialHistogramDataPoint>();
    public virtual ICollection<SummaryDataPoint> SummaryDataPoints { get; set; } = new List<SummaryDataPoint>();
}