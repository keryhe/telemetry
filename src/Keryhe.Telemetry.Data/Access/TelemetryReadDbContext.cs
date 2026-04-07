using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Data.Access.Models;

namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Read-only DbContext. Change tracking is disabled globally — all queries run as AsNoTracking.
/// SaveChanges will throw to prevent accidental writes through this context.
/// </summary>
public class TelemetryReadDbContext : DbContext
{
    public TelemetryReadDbContext(DbContextOptions<TelemetryReadDbContext> options) : base(options)
    {
    }

    public DbSet<Resource> Resources { get; set; }
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
