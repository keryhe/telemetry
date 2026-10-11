using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

/// <summary>The three retention windows a tenant can set.</summary>
public enum RetentionSignal
{
    Traces,
    Logs,
    Metrics
}

/// <summary>
/// Every tenant's retention windows for one sweep, with one fixed "now" so every cutoff in the sweep is
/// computed from the same instant. Built from <see cref="IRetentionSettingsRepository.GetAllAsync"/>; a
/// tenant that is not in it (data whose tenant row is gone) expires on <see cref="RetentionDefaults"/>.
/// </summary>
public sealed class RetentionPolicy
{
    private readonly Dictionary<long, RetentionSettings> _tenants;

    public RetentionPolicy(IReadOnlyDictionary<long, RetentionSettings> tenants, TimeProvider? time = null)
        : this(tenants, (time ?? TimeProvider.System).GetUtcNow().UtcDateTime)
    {
    }

    public RetentionPolicy(IReadOnlyDictionary<long, RetentionSettings> tenants, DateTime nowUtc)
    {
        _tenants = new Dictionary<long, RetentionSettings>(tenants);
        Now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        // The negative guard is load-bearing, not defensive noise: a negative period puts the cutoff in
        // the FUTURE, at which point every delete matches every row. Fail loudly instead of erasing data.
        foreach (var (id, s) in _tenants)
        {
            if (s.TraceRetentionDays < 0 || s.LogRetentionDays < 0 || s.MetricRetentionDays < 0)
                throw new ArgumentOutOfRangeException(nameof(tenants),
                    $"Tenant {id} has a negative retention window; that would delete all of its telemetry.");
        }
    }

    /// <summary>A policy with no tenants: everything expires on the built-in windows.</summary>
    public static RetentionPolicy BuiltIn(TimeProvider? time = null) => new(new Dictionary<long, RetentionSettings>(), time);

    /// <summary>The instant every cutoff of this policy is measured from.</summary>
    public DateTime Now { get; }

    /// <summary>Tenants that have a control-plane row (and so their own windows).</summary>
    public IReadOnlyCollection<long> KnownTenants => _tenants.Keys;

    public bool IsKnown(long tenantId) => _tenants.ContainsKey(tenantId);

    /// <summary>The tenant's window for a signal; the built-in window when the tenant is unknown.</summary>
    public TimeSpan WindowFor(RetentionSignal signal, long tenantId)
        => TimeSpan.FromDays(_tenants.TryGetValue(tenantId, out var s) ? Days(signal, s) : BuiltInDays(signal));

    /// <summary>Data older than this is expired for the tenant.</summary>
    public DateTime CutoffFor(RetentionSignal signal, long tenantId) => Now - WindowFor(signal, tenantId);

    /// <summary>The built-in window for a signal.</summary>
    public TimeSpan BuiltInWindow(RetentionSignal signal) => TimeSpan.FromDays(BuiltInDays(signal));

    /// <summary>
    /// Every window in effect for a signal, ascending. The built-in window is always included, because
    /// data may exist for tenants this policy does not know.
    /// </summary>
    public IReadOnlyList<TimeSpan> DistinctWindows(RetentionSignal signal)
        => _tenants.Values.Select(s => TimeSpan.FromDays(Days(signal, s)))
            .Append(BuiltInWindow(signal)).Distinct().OrderBy(w => w).ToList();

    /// <summary>The longest window in effect for a signal (the built-in one included).</summary>
    public TimeSpan LongestWindow(RetentionSignal signal) => DistinctWindows(signal)[^1];

    /// <summary>The known tenants whose window for a signal is exactly <paramref name="window"/>.</summary>
    public IReadOnlyList<long> TenantsWithWindow(RetentionSignal signal, TimeSpan window)
        => _tenants.Where(kv => TimeSpan.FromDays(Days(signal, kv.Value)) == window).Select(kv => kv.Key).OrderBy(id => id).ToList();

    private static int Days(RetentionSignal signal, RetentionSettings s) => signal switch
    {
        RetentionSignal.Traces => s.TraceRetentionDays,
        RetentionSignal.Logs => s.LogRetentionDays,
        _ => s.MetricRetentionDays
    };

    private static int BuiltInDays(RetentionSignal signal) => signal switch
    {
        RetentionSignal.Traces => RetentionDefaults.TraceDays,
        RetentionSignal.Logs => RetentionDefaults.LogDays,
        _ => RetentionDefaults.MetricDays
    };
}
