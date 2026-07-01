using Keryhe.Telemetry.Api.Services;
using Microsoft.AspNetCore.Http;

namespace Keryhe.Telemetry.Api.Middleware;

public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiTenantContext tenantContext)
    {
        if (context.Request.Headers.TryGetValue("X-Tenant-Id", out var value)
            && long.TryParse(value, out var tenantId)
            && tenantId > 0)
        {
            tenantContext.SetTenantId(tenantId);
        }

        await next(context);
    }
}
