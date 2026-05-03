using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Data.Access.Models;

namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Read-only DbContext. Change tracking is disabled globally — all queries run as AsNoTracking.
/// SaveChanges will throw to prevent accidental writes through this context.
/// </summary>
public class TelemetryReadDbContext : DbContext
{
    private readonly long _tenantId;

    public TelemetryReadDbContext(DbContextOptions<TelemetryReadDbContext> options, ITenantContext tenantContext) : base(options)
    {
        _tenantId = tenantContext?.GetRequiredTenantId() ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    public DbSet<Resource> Resources { get; set; }
    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<InstrumentationScope> InstrumentationScopes { get; set; }

    public DbSet<Span> Spans { get; set; }
    public DbSet<SpanEvent> SpanEvents { get; set; }
    public DbSet<SpanLink> SpanLinks { get; set; }

    public DbSet<Metric> Metrics { get; set; }
    public DbSet<GaugeDataPoint> GaugeDataPoints { get; set; }
    public DbSet<SumDataPoint> SumDataPoints { get; set; }
    public DbSet<HistogramDataPoint> HistogramDataPoints { get; set; }
    public DbSet<ExponentialHistogramDataPoint> ExponentialHistogramDataPoints { get; set; }
    public DbSet<SummaryDataPoint> SummaryDataPoints { get; set; }
    public DbSet<Exemplar> Exemplars { get; set; }

    public DbSet<LogRecord> LogRecords { get; set; }

    public DbSet<SchemaVersion> SchemaVersions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TelemetryModelConfiguration.Configure(modelBuilder);

        modelBuilder.Entity<Resource>().HasQueryFilter(resource => resource.TenantId == _tenantId);
        modelBuilder.Entity<Span>().HasQueryFilter(span => span.Resource.TenantId == _tenantId);
        modelBuilder.Entity<SpanEvent>().HasQueryFilter(spanEvent => spanEvent.Span.Resource.TenantId == _tenantId);
        modelBuilder.Entity<SpanLink>().HasQueryFilter(spanLink => spanLink.Span.Resource.TenantId == _tenantId);
        modelBuilder.Entity<Metric>().HasQueryFilter(metric => metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<GaugeDataPoint>().HasQueryFilter(dataPoint => dataPoint.Metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<SumDataPoint>().HasQueryFilter(dataPoint => dataPoint.Metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<HistogramDataPoint>().HasQueryFilter(dataPoint => dataPoint.Metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<ExponentialHistogramDataPoint>().HasQueryFilter(dataPoint => dataPoint.Metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<SummaryDataPoint>().HasQueryFilter(dataPoint => dataPoint.Metric.Resource.TenantId == _tenantId);
        modelBuilder.Entity<LogRecord>().HasQueryFilter(logRecord => logRecord.Resource.TenantId == _tenantId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<string>().HaveMaxLength(255);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new InvalidOperationException("TelemetryReadDbContext is read-only. Use TelemetryWriteDbContext for write operations.");

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("TelemetryReadDbContext is read-only. Use TelemetryWriteDbContext for write operations.");
}
