using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access.Models;

namespace Keryhe.Telemetry.Data.Access;

public static class OpenTelemetryDbContextExtensions
{
    
    /// <summary>
    /// Converts Unix nanoseconds to DateTime
    /// </summary>
    public static DateTime UnixNanoToDateTime(long unixNano)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(unixNano / 1_000_000).DateTime;
    }

    /// <summary>
    /// Converts DateTime to Unix nanoseconds
    /// </summary>
    public static long DateTimeToUnixNano(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeMilliseconds() * 1_000_000;
    }
}

public class OpenTelemetryDbContext : DbContext
{
    public OpenTelemetryDbContext(DbContextOptions<OpenTelemetryDbContext> options) : base(options)
    {
    }

    // DbSets for all entities
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

        // Configure enum to string conversions
        var attributeTypeConverter = new EnumToStringConverter<AttributeType>();
        var spanKindConverter = new EnumToStringConverter<SpanKind>();
        var spanStatusConverter = new EnumToStringConverter<SpanStatusCode>();
        var metricTypeConverter = new EnumToStringConverter<MetricType>();
        var aggregationTemporalityConverter = new EnumToStringConverter<AggregationTemporality>();

        // Resources
        modelBuilder.Entity<Resource>(entity =>
        {
            entity.ToTable("resources");
            entity.HasIndex(e => e.ResourceHash).IsUnique().HasDatabaseName("uk_resource_hash");
            entity.HasIndex(e => e.CreatedAt).HasDatabaseName("idx_created_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // Instrumentation Scopes
        modelBuilder.Entity<InstrumentationScope>(entity =>
        {
            entity.ToTable("instrumentation_scopes");
            entity.HasIndex(e => e.ScopeHash).IsUnique().HasDatabaseName("uk_scope_hash");
            entity.HasIndex(e => new { e.Name, e.Version }).HasDatabaseName("idx_name_version");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // Spans
        modelBuilder.Entity<Span>(entity =>
        {
            entity.ToTable("spans");
            entity.Property(e => e.TraceId).HasMaxLength(16).IsFixedLength();
            entity.Property(e => e.SpanId).HasMaxLength(8).IsFixedLength();
            entity.Property(e => e.ParentSpanId).HasMaxLength(8).IsFixedLength();
            entity.Property(e => e.Kind).HasConversion(spanKindConverter).HasDefaultValue(SpanKind.UNSPECIFIED);
            entity.Property(e => e.StatusCode).HasConversion(spanStatusConverter).HasDefaultValue(SpanStatusCode.UNSET);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            
            entity.HasIndex(e => new { e.TraceId, e.SpanId }).IsUnique().HasDatabaseName("uk_trace_span");
            entity.HasIndex(e => e.TraceId).HasDatabaseName("idx_trace_id");
            entity.HasIndex(e => e.SpanId).HasDatabaseName("idx_span_id");
            entity.HasIndex(e => e.ParentSpanId).HasDatabaseName("idx_parent_span");
            entity.HasIndex(e => e.StartTimeUnixNano).HasDatabaseName("idx_start_time");
            entity.HasIndex(e => e.EndTimeUnixNano).HasDatabaseName("idx_end_time");
            entity.HasIndex(e => e.Name).HasDatabaseName("idx_name");
            entity.HasIndex(e => e.Kind).HasDatabaseName("idx_kind");
            entity.HasIndex(e => e.StatusCode).HasDatabaseName("idx_status");
        });

        modelBuilder.Entity<SpanEvent>(entity =>
        {
            entity.ToTable("span_events");
            entity.HasIndex(e => new { e.SpanId, e.TimeUnixNano }).HasDatabaseName("idx_span_time");
        });

        modelBuilder.Entity<SpanLink>(entity =>
        {
            entity.ToTable("span_links");
            entity.Property(e => e.LinkedTraceId).HasMaxLength(16).IsFixedLength();
            entity.Property(e => e.LinkedSpanId).HasMaxLength(8).IsFixedLength();
            entity.HasIndex(e => new { e.SpanId, e.LinkedTraceId, e.LinkedSpanId }).HasDatabaseName("idx_span_link");
        });

        // Metrics
        modelBuilder.Entity<Metric>(entity =>
        {
            entity.ToTable("metrics");
            entity.Property(e => e.Type).HasConversion(metricTypeConverter);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasIndex(e => e.Name).HasDatabaseName("idx_name");
            entity.HasIndex(e => e.Type).HasDatabaseName("idx_type");
        });

        modelBuilder.Entity<GaugeDataPoint>(entity =>
        {
            entity.ToTable("gauge_data_points");
            entity.HasIndex(e => new { e.MetricId, e.TimeUnixNano }).HasDatabaseName("idx_metric_time");
            entity.HasIndex(e => e.TimeUnixNano).HasDatabaseName("idx_time");
        });

        modelBuilder.Entity<SumDataPoint>(entity =>
        {
            entity.ToTable("sum_data_points");
            entity.Property(e => e.AggregationTemporality).HasConversion(aggregationTemporalityConverter).HasDefaultValue(AggregationTemporality.UNSPECIFIED);
            entity.HasIndex(e => new { e.MetricId, e.TimeUnixNano }).HasDatabaseName("idx_metric_time");
            entity.HasIndex(e => e.AggregationTemporality).HasDatabaseName("idx_temporality");
        });

        modelBuilder.Entity<HistogramDataPoint>(entity =>
        {
            entity.ToTable("histogram_data_points");
            entity.Property(e => e.AggregationTemporality).HasConversion(aggregationTemporalityConverter).HasDefaultValue(AggregationTemporality.UNSPECIFIED);
            entity.HasIndex(e => new { e.MetricId, e.TimeUnixNano }).HasDatabaseName("idx_metric_time");
        });

        modelBuilder.Entity<ExponentialHistogramDataPoint>(entity =>
        {
            entity.ToTable("exponential_histogram_data_points");
            entity.Property(e => e.AggregationTemporality).HasConversion(aggregationTemporalityConverter).HasDefaultValue(AggregationTemporality.UNSPECIFIED);
            entity.HasIndex(e => new { e.MetricId, e.TimeUnixNano }).HasDatabaseName("idx_metric_time");
        });

        modelBuilder.Entity<SummaryDataPoint>(entity =>
        {
            entity.ToTable("summary_data_points");
            entity.HasIndex(e => new { e.MetricId, e.TimeUnixNano }).HasDatabaseName("idx_metric_time");
        });

        modelBuilder.Entity<Exemplar>(entity =>
        {
            entity.ToTable("exemplars");
            entity.Property(e => e.SpanId).HasMaxLength(8).IsFixedLength();
            entity.Property(e => e.TraceId).HasMaxLength(16).IsFixedLength();
            entity.HasIndex(e => e.TimeUnixNano).HasDatabaseName("idx_time");
            entity.HasIndex(e => new { e.TraceId, e.SpanId }).HasDatabaseName("idx_trace_span");
        });

        // Log Records
        modelBuilder.Entity<LogRecord>(entity =>
        {
            entity.ToTable("log_records");
            entity.Property(e => e.BodyType).HasConversion(attributeTypeConverter);
            entity.Property(e => e.TraceId).HasMaxLength(16).IsFixedLength();
            entity.Property(e => e.SpanId).HasMaxLength(8).IsFixedLength();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            
            entity.HasIndex(e => e.TimeUnixNano).HasDatabaseName("idx_time");
            entity.HasIndex(e => e.ObservedTimeUnixNano).HasDatabaseName("idx_observed_time");
            entity.HasIndex(e => e.SeverityNumber).HasDatabaseName("idx_severity");
            entity.HasIndex(e => new { e.TraceId, e.SpanId }).HasDatabaseName("idx_trace_span");
            entity.HasIndex(e => new { e.ResourceId, e.TimeUnixNano }).HasDatabaseName("idx_resource_time");
        });

        // Schema Version
        modelBuilder.Entity<SchemaVersion>(entity =>
        {
            entity.ToTable("schema_version");
            entity.Property(e => e.AppliedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // Configure relationships
        ConfigureRelationships(modelBuilder);
    }

    private static void ConfigureRelationships(ModelBuilder modelBuilder)
    {
        // InstrumentationScope relationships

        // Span relationships
        modelBuilder.Entity<Span>()
            .HasOne(s => s.Resource)
            .WithMany(r => r.Spans)
            .HasForeignKey(s => s.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Span>()
            .HasOne(s => s.Scope)
            .WithMany(sc => sc.Spans)
            .HasForeignKey(s => s.ScopeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SpanEvent>()
            .HasOne(se => se.Span)
            .WithMany(s => s.Events)
            .HasForeignKey(se => se.SpanId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SpanLink>()
            .HasOne(sl => sl.Span)
            .WithMany(s => s.Links)
            .HasForeignKey(sl => sl.SpanId)
            .OnDelete(DeleteBehavior.Cascade);

        // Metric relationships
        modelBuilder.Entity<Metric>()
            .HasOne(m => m.Resource)
            .WithMany(r => r.Metrics)
            .HasForeignKey(m => m.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Metric>()
            .HasOne(m => m.Scope)
            .WithMany(sc => sc.Metrics)
            .HasForeignKey(m => m.ScopeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<GaugeDataPoint>()
            .HasOne(gdp => gdp.Metric)
            .WithMany(m => m.GaugeDataPoints)
            .HasForeignKey(gdp => gdp.MetricId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GaugeDataPoint>()
            .HasOne(gdp => gdp.Exemplar)
            .WithMany()
            .HasForeignKey(gdp => gdp.ExemplarId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SumDataPoint>()
            .HasOne(sdp => sdp.Metric)
            .WithMany(m => m.SumDataPoints)
            .HasForeignKey(sdp => sdp.MetricId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SumDataPoint>()
            .HasOne(sdp => sdp.Exemplar)
            .WithMany()
            .HasForeignKey(sdp => sdp.ExemplarId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<HistogramDataPoint>()
            .HasOne(hdp => hdp.Metric)
            .WithMany(m => m.HistogramDataPoints)
            .HasForeignKey(hdp => hdp.MetricId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<HistogramDataPoint>()
            .HasOne(hdp => hdp.Exemplar)
            .WithMany()
            .HasForeignKey(hdp => hdp.ExemplarId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ExponentialHistogramDataPoint>()
            .HasOne(ehdp => ehdp.Metric)
            .WithMany(m => m.ExponentialHistogramDataPoints)
            .HasForeignKey(ehdp => ehdp.MetricId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExponentialHistogramDataPoint>()
            .HasOne(ehdp => ehdp.Exemplar)
            .WithMany()
            .HasForeignKey(ehdp => ehdp.ExemplarId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SummaryDataPoint>()
            .HasOne(sdp => sdp.Metric)
            .WithMany(m => m.SummaryDataPoints)
            .HasForeignKey(sdp => sdp.MetricId)
            .OnDelete(DeleteBehavior.Cascade);

        // Data point attributes - note: no direct FK relationship due to polymorphic nature
        // You would need to handle the relationship in your application logic

        // Log record relationships
        modelBuilder.Entity<LogRecord>()
            .HasOne(lr => lr.Resource)
            .WithMany(r => r.LogRecords)
            .HasForeignKey(lr => lr.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LogRecord>()
            .HasOne(lr => lr.Scope)
            .WithMany(sc => sc.LogRecords)
            .HasForeignKey(lr => lr.ScopeId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        
        // Configure default string lengths
        configurationBuilder.Properties<string>().HaveMaxLength(255);
    }
}