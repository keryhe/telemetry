using Microsoft.AspNetCore.Authorization;

namespace Keryhe.Telemetry.Api.Authorization;

/// <summary>What a request does. Each library action carries exactly one.</summary>
public enum TelemetryOperation
{
    Read,
    Export,
    ManageAlerts,
    ManageSettings,
}

/// <summary>Declares the <see cref="TelemetryOperation"/> an action performs (never inferred from the HTTP method).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TelemetryOperationAttribute(TelemetryOperation operation) : Attribute
{
    public TelemetryOperation Operation { get; } = operation;
}

/// <summary>
/// Marks an action that is served without authorization (and so carries no <see cref="TelemetryOperationAttribute"/>):
/// <c>TelemetryAuthorizationFilter</c> skips it. Only for an action that answers about the caller's own identity and
/// reveals nothing else (<c>GET /me</c>). Exactly one of the two attributes belongs on every library action.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TelemetryAnonymousAttribute : Attribute;

/// <summary>Marks a controller whose routes live under <c>tenants/{tenantId}</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class TenantScopedAttribute : Attribute;

/// <summary>The authorization resource: the tenant the route names, or null on a global route.</summary>
public sealed record TelemetryResource(long? TenantId);

/// <summary>Satisfied when the caller may perform <see cref="Operation"/>.</summary>
public sealed class TelemetryOperationRequirement(TelemetryOperation operation) : IAuthorizationRequirement
{
    public TelemetryOperation Operation { get; } = operation;
}

/// <summary>Satisfied when the caller may access the tenant in <see cref="TelemetryResource"/>.</summary>
public sealed class TenantAccessRequirement : IAuthorizationRequirement;
