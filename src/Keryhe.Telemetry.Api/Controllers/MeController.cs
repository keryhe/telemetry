using System.Security.Claims;
using Keryhe.Telemetry.Api.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace Keryhe.Telemetry.Api.Controllers;

/// <summary>
/// Who the caller is, for the UI's user menu in cookie mode (OIDC mode reads its own token). Anonymous-allowed and
/// always 200: <c>{ signedIn: false }</c> when nobody is signed in, so the UI's auth interceptor never redirects to sign
/// in because of this call. Returns only the caller's own claims, read with a fixed fallback list (no configuration).
/// </summary>
[ApiController]
[Route("me")]
public class MeController(TelemetryPolicyResolver policies, IPolicyEvaluator policyEvaluator) : ControllerBase
{
    // GET /api/me
    [TelemetryAnonymous]
    [HttpGet]
    public async Task<ActionResult<MeResponse>> GetMe()
    {
        Response.Headers.CacheControl = "no-store";

        // As the authorization filter does: when the Read operation's policy names schemes, the caller is whoever
        // those schemes authenticate (no authorization step); otherwise the default scheme's principal.
        var policy = await policies.ResolveAsync(TelemetryOperation.Read);
        if (policy.AuthenticationSchemes.Count > 0)
            await policyEvaluator.AuthenticateAsync(policy, HttpContext);

        var user = HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) return Ok(new MeResponse(false, null, null, null, null));

        return Ok(new MeResponse(
            true,
            First(user, "name", ClaimTypes.Name) ?? NonEmpty(user.Identity.Name),
            First(user, "given_name", ClaimTypes.GivenName),
            First(user, "email", ClaimTypes.Email) ?? PreferredUsernameEmail(user),
            Picture(user)));
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? First(ClaimsPrincipal user, params string[] types)
        => types.Select(t => NonEmpty(user.FindFirst(t)?.Value)).FirstOrDefault(v => v is not null);

    // preferred_username is often the email, but only a value that looks like one is used as such.
    private static string? PreferredUsernameEmail(ClaimsPrincipal user)
        => NonEmpty(user.FindFirst("preferred_username")?.Value) is { } v && v.Contains('@') ? v : null;

    // An absolute http(s) URL only: anything else (javascript:, data:, a relative path) is dropped.
    private static string? Picture(ClaimsPrincipal user)
        => NonEmpty(user.FindFirst("picture")?.Value) is { } v
           && Uri.TryCreate(v, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? v : null;
}

public sealed record MeResponse(bool SignedIn, string? Name, string? GivenName, string? Email, string? Picture);
