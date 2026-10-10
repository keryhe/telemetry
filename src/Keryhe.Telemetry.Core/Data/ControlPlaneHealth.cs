namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Whether the control-plane database (API-key lookup) is answering, for the collector's readiness check.
/// <see cref="CachingTenantResolver"/> is scoped, so what it learns lives here in a singleton. A collector with no traffic
/// never calls the control plane and is not marked unhealthy for it: only a lookup that actually failed counts.
/// </summary>
public sealed class ControlPlaneHealth(TimeProvider time)
{
    private long _failingSinceTicks; // UTC ticks of the first failure since the last success; 0 = healthy

    public void RecordSuccess() => Interlocked.Exchange(ref _failingSinceTicks, 0);

    public void RecordFailure() => Interlocked.CompareExchange(ref _failingSinceTicks, time.GetUtcNow().UtcTicks, 0);

    /// <summary>How long lookups have been failing without a success in between; zero when healthy.</summary>
    public TimeSpan FailingFor
    {
        get
        {
            var since = Interlocked.Read(ref _failingSinceTicks);
            if (since == 0) return TimeSpan.Zero;
            var elapsed = time.GetUtcNow().UtcTicks - since;
            return elapsed > 0 ? TimeSpan.FromTicks(elapsed) : TimeSpan.Zero;
        }
    }
}
