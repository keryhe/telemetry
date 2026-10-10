using System.Globalization;
using Grpc.Core;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Collector.Authentication;

/// <summary>Reads the tenant the authentication handler put on the call's principal.</summary>
public static class TenantClaims
{
    /// <summary>
    /// The authenticated tenant of this call. The collector policy guarantees a tenant claim before a
    /// service method runs, so a miss here means the endpoint was mapped without the policy: refuse
    /// rather than ingest under a guessed tenant.
    /// </summary>
    public static long GetRequiredTenantId(ServerCallContext context)
    {
        var claim = context.GetHttpContext().User.FindFirst(TelemetryClaimTypes.TenantId)?.Value;
        if (long.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) && tenantId > 0)
            return tenantId;
        throw new RpcException(new Status(StatusCode.Unauthenticated, "Not authenticated."));
    }

    /// <summary>The authenticated tenant of an HTTP request, or null when there is none (the endpoint was mapped without the policy).</summary>
    public static long? GetTenantId(Microsoft.AspNetCore.Http.HttpContext context) =>
        long.TryParse(context.User.FindFirst(TelemetryClaimTypes.TenantId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tenantId) && tenantId > 0
            ? tenantId
            : null;
}
