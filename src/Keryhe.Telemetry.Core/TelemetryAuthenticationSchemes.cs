namespace Keryhe.Telemetry.Core;

/// <summary>Authentication scheme names registered by the Keryhe Telemetry collector.</summary>
public static class TelemetryAuthenticationSchemes
{
    /// <summary>
    /// The collector's per-tenant API key scheme: <c>Authorization: Bearer &lt;key&gt;</c>, checked
    /// against <c>api_keys</c> by <c>ApiKeyAuthenticationHandler</c>.
    /// </summary>
    public const string ApiKey = "KeryheTelemetryApiKey";
}

/// <summary>Claim types the collector's authentication handler puts on the principal.</summary>
public static class TelemetryClaimTypes
{
    /// <summary>The tenant that owns the authenticated API key (a positive <c>long</c>, as text).</summary>
    public const string TenantId = "keryhe:tenant_id";

    /// <summary>The <c>api_keys.id</c> of the authenticated key (as text).</summary>
    public const string ApiKeyId = "keryhe:api_key_id";
}
