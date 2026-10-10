namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// What one tenant may take of the shared ingestion queue, bound from <c>Telemetry:Ingestion:TenantQuota</c> and reloadable
/// (<c>IOptionsMonitor</c>), so an override can change without a restart. The queue is shared by every tenant; without a quota a tenant
/// that sends more than the database can absorb fills it and every other tenant's exports are refused too.
/// </summary>
public sealed class TenantQuotaOptions
{
    public const string SectionName = "Telemetry:Ingestion:TenantQuota";

    /// <summary>
    /// The most of each signal's queue (records, and bytes where the byte budget is on) one tenant may hold, as a fraction. An export that
    /// would take its tenant over the share is refused like a full queue (<c>UNAVAILABLE</c> + <c>RetryInfo</c>, reason
    /// <c>tenant_quota</c>), except that a tenant holding nothing is always admitted. 1 turns the quota off.
    /// </summary>
    public double MaxShare { get; set; } = 0.5;

    /// <summary>
    /// The most records per second one tenant may send per signal (a token bucket with a one-second burst), protecting the database's
    /// throughput as <see cref="MaxShare"/> protects the queue. Over it, an export is refused with <c>UNAVAILABLE</c> and the time until
    /// enough tokens return (reason <c>tenant_rate</c>). 0 (the default) turns the rate limit off.
    /// </summary>
    public double RecordsPerSecond { get; set; }

    /// <summary>Per-tenant exceptions, keyed by tenant id (the key may be written as a plain number).</summary>
    public Dictionary<string, TenantQuotaOverride> Overrides { get; set; } = [];

    public double ShareFor(long tenantId) =>
        Overrides.TryGetValue(tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var o) && o.MaxShare is { } s ? s : MaxShare;

    public double RateFor(long tenantId) =>
        Overrides.TryGetValue(tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var o) && o.RecordsPerSecond is { } r ? r : RecordsPerSecond;

    public void Validate()
    {
        static void Share(double v, string where)
        {
            if (!(v > 0 && v <= 1)) throw new InvalidOperationException($"{SectionName}:{where} must be greater than 0 and at most 1 (was {v}).");
        }
        static void Rate(double v, string where)
        {
            if (!(v >= 0)) throw new InvalidOperationException($"{SectionName}:{where} must not be negative (was {v}).");
        }
        Share(MaxShare, nameof(MaxShare));
        Rate(RecordsPerSecond, nameof(RecordsPerSecond));
        foreach (var (tenant, o) in Overrides)
        {
            if (!long.TryParse(tenant, out _)) throw new InvalidOperationException($"{SectionName}:Overrides:{tenant}: the key must be a tenant id.");
            if (o.MaxShare is { } s) Share(s, $"Overrides:{tenant}:MaxShare");
            if (o.RecordsPerSecond is { } r) Rate(r, $"Overrides:{tenant}:RecordsPerSecond");
        }
    }
}

public sealed class TenantQuotaOverride
{
    public double? MaxShare { get; set; }
    public double? RecordsPerSecond { get; set; }
}
