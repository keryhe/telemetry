namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>
/// The <c>Telemetry:Api:Authorization</c> section. With <see cref="Enabled"/> false (the default)
/// no authorization runs and every tenant in a route is readable.
/// </summary>
public sealed class TelemetryApiAuthorizationOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Operation level to the name of a policy the host registered with
    /// <c>AddAuthorization(o =&gt; o.AddPolicy(...))</c>. Keys: <c>Read</c>, <c>Admin</c>, <c>Export</c>,
    /// <c>ManageAlerts</c>, <c>ManageSettings</c>. Unset entries fall back: <c>Export</c> to
    /// <c>Read</c>; <c>ManageAlerts</c>/<c>ManageSettings</c> to <c>Admin</c>; <c>Read</c>/<c>Admin</c> to
    /// the host's default authorization policy.
    /// </summary>
    public Dictionary<string, string> Policies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Claim-to-tenant grants used by the built-in tenant access handler.</summary>
    public List<TenantMapping> TenantMappings { get; set; } = [];
}

/// <summary>
/// Callers holding the claim <see cref="ClaimType"/> = <see cref="ClaimValue"/> may access
/// <see cref="Tenants"/>: tenant names or ids, or <c>"*"</c> for every tenant.
/// </summary>
public sealed class TenantMapping
{
    public string ClaimType { get; set; } = "";
    public string ClaimValue { get; set; } = "";
    public List<string> Tenants { get; set; } = [];
}
