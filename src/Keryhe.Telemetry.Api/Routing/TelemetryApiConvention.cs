using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Keryhe.Telemetry.Api.Routing;

/// <summary>
/// Prefixes the attribute routes of this assembly's controllers with the configured base path (and
/// <c>tenants/{tenantId}</c> for <see cref="TenantScopedAttribute"/> controllers) and adds the
/// authorization filter. A consumer's own controllers are never touched.
/// </summary>
internal sealed class TelemetryApiConvention(string basePath) : IApplicationModelConvention
{
    private static readonly Type MarkerType = typeof(TelemetryApiConvention);

    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers.Where(c => c.ControllerType.Assembly == MarkerType.Assembly))
        {
            var prefix = basePath.TrimStart('/');
            if (controller.ControllerType.IsDefined(typeof(TenantScopedAttribute), false))
                prefix += "/tenants/{tenantId:long:min(1)}";
            var prefixModel = new AttributeRouteModel(new RouteAttribute(prefix));

            foreach (var selector in controller.Selectors.Where(s => s.AttributeRouteModel is not null))
                selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(prefixModel, selector.AttributeRouteModel);

            controller.Filters.Add(new ServiceFilterAttribute(typeof(TelemetryAuthorizationFilter)));
        }
    }
}
