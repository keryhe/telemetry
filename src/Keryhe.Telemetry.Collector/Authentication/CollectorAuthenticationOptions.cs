namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>How the collector decides which tenant an export belongs to.</summary>
public enum CollectorAuthenticationMode
{
    /// <summary>Every export carries a per-tenant API key (<c>Authorization: Bearer &lt;key&gt;</c>), resolved against <c>api_keys</c>.</summary>
    ApiKey,

    /// <summary>
    /// No API key: every export on a collector endpoint is stored under <see cref="CollectorAuthenticationOptions.FixedTenantId"/>, and an
    /// <c>Authorization</c> header is ignored. For a single-customer install where the network, not a key, is the boundary.
    /// </summary>
    FixedTenant,
}

/// <summary>Collector authentication options. Bound from <c>Telemetry:Collector:Authentication</c>.</summary>
public sealed class CollectorAuthenticationOptions
{
    public const string SectionName = TelemetryCollectorOptions.SectionName + ":Authentication";

    public CollectorAuthenticationMode Mode { get; set; } = CollectorAuthenticationMode.ApiKey;

    /// <summary>The tenant every export is stored under in <see cref="CollectorAuthenticationMode.FixedTenant"/> mode; ignored otherwise.</summary>
    public long FixedTenantId { get; set; }

    /// <summary>
    /// Outside Development, <see cref="CollectorAuthenticationMode.FixedTenant"/> mode refuses to start unless this is true: anyone who
    /// can reach the OTLP endpoints can then write telemetry, so the choice must be explicit.
    /// </summary>
    public bool AllowUnauthenticatedIngestion { get; set; }

    public void Validate()
    {
        if (Mode == CollectorAuthenticationMode.FixedTenant && FixedTenantId <= 0)
            throw new InvalidOperationException(
                $"{SectionName}:FixedTenantId must be a tenant id greater than 0 when {SectionName}:Mode is FixedTenant (was {FixedTenantId}).");
    }
}
