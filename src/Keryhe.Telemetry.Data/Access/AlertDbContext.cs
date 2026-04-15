using Microsoft.EntityFrameworkCore;
using Keryhe.Telemetry.Data.Access.Models;

namespace Keryhe.Telemetry.Data.Access;

/// <summary>
/// Dedicated DbContext for alert_rules and alert_events tables. Supports both reads and writes.
/// </summary>
public class AlertDbContext : DbContext
{
    public AlertDbContext(DbContextOptions<AlertDbContext> options) : base(options)
    {
    }

    public DbSet<AlertRuleEntity> AlertRules { get; set; }
    public DbSet<AlertEventEntity> AlertEvents { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AlertRuleEntity>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.Property(e => e.Name).HasMaxLength(255);
            entity.Property(e => e.Type).HasMaxLength(50);
            entity.Property(e => e.ServiceName).HasMaxLength(255);
            entity.Property(e => e.ConditionJson).HasColumnType("jsonb");
            entity.Property(e => e.WebhookUrl).HasColumnType("text");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("NOW()");

            entity.HasMany(e => e.Events)
                  .WithOne(e => e.Rule)
                  .HasForeignKey(e => e.RuleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AlertEventEntity>(entity =>
        {
            entity.ToTable("alert_events");
            entity.Property(e => e.DetailsJson).HasColumnType("jsonb");
            entity.Property(e => e.FiredAt).HasDefaultValueSql("NOW()");
            entity.HasIndex(e => e.RuleId).HasDatabaseName("idx_alert_events_rule_id");
            entity.HasIndex(e => e.FiredAt).HasDatabaseName("idx_alert_events_fired_at");
        });
    }
}
