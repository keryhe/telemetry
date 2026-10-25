using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Authentication;

public sealed class FixedTenantAuthenticationOptions : AuthenticationSchemeOptions
{
    public long TenantId { get; set; }
}

/// <summary>
/// The collector's scheme in <see cref="CollectorAuthenticationMode.FixedTenant"/> mode, registered under the same name as
/// <see cref="ApiKeyAuthenticationHandler"/> so the collector policy and every endpoint mapping stay as they are. On a collector endpoint
/// (<see cref="CollectorEndpointMetadata"/>) every request succeeds as the configured tenant, with no API-key claim; an
/// <c>Authorization</c> header is ignored. Any other endpoint gets <c>NoResult</c>, as with the key handler.
/// </summary>
public sealed class FixedTenantAuthenticationHandler(
    IOptionsMonitor<FixedTenantAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<FixedTenantAuthenticationOptions>(options, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<CollectorEndpointMetadata>() is null)
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(
            [new Claim(TelemetryClaimTypes.TenantId, Options.TenantId.ToString(CultureInfo.InvariantCulture))],
            Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
