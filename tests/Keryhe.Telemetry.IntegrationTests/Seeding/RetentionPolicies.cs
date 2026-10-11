using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.IntegrationTests.Seeding;

/// <summary>Builds the <see cref="RetentionPolicy"/> the retention sweeps take: each listed tenant has the same window for all three signals.</summary>
public static class RetentionPolicies
{
    public static RetentionPolicy For(params (long TenantId, int Days)[] tenants)
        => new(tenants.ToDictionary(t => t.TenantId, t => new RetentionSettings
        {
            TraceRetentionDays = t.Days,
            LogRetentionDays = t.Days,
            MetricRetentionDays = t.Days
        }));
}
