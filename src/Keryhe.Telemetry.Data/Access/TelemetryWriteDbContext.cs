using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Data.Access.Models;

namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Write DbContext with full EF Core change tracking. Used by the ingestion server only.
/// </summary>
public class TelemetryWriteDbContext : DbContext
{
    public TelemetryWriteDbContext(DbContextOptions<TelemetryWriteDbContext> options) : base(options)
    {
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
        TelemetryModelConfiguration.Configure(modelBuilder, isPostgres: Database.ProviderName?.Contains("Npgsql") == true);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<string>().HaveMaxLength(255);
    }
}
