using System.Text.RegularExpressions;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Options for <see cref="TelemetryUiApplicationBuilderExtensions.UseKeryheTelemetryUi"/>.
///
/// Bound from the <c>TelemetryUi</c> configuration section by
/// <c>AddKeryheTelemetryUi(IConfiguration)</c>, then optionally overridden in code. Every setting
/// here is deliberately runtime rather than build-time configuration: this UI ships as a prebuilt
/// bundle, so anything baked into the compiled output is, by definition, not configurable by the
/// consumers the package exists to serve.
/// </summary>
public sealed partial class TelemetryUiOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "TelemetryUi";

    /// <summary>
    /// The URL path prefix the UI is mounted at, as the <em>browser</em> sees it — <c>"/"</c> (the
    /// default) to serve it at the origin root, or something like <c>"/telemetry"</c> to serve it
    /// beside another app. Written into the bundle's <c>&lt;base href&gt;</c> at startup, which is
    /// what re-roots its asset references, its <c>config.json</c> fetch and its client-side router
    /// in one go; see <c>TelemetryUiShell</c>.
    ///
    /// Three consequences worth knowing before setting this:
    /// <list type="bullet">
    /// <item>It must match what the browser requests. Behind a reverse proxy, that means the proxy
    /// must <em>forward</em> the prefix (<c>proxy_pass http://app;</c>), not strip it
    /// (<c>proxy_pass http://app/;</c>) — the value is baked into the served HTML at startup, so it
    /// cannot be recovered per request from <c>PathBase</c>.</item>
    /// <item>It moves the UI only. The API's location is its own setting
    /// (<c>Telemetry:Api:BasePath</c>, default <c>/api</c>), which <see cref="ApiBasePath"/> follows
    /// unless set explicitly.</item>
    /// <item>Once it is non-empty, the origin root stops being served: <c>GET /</c> returns 404,
    /// and so does every other path outside the prefix. That is the point — it is what lets another
    /// app own <c>/</c>.</item>
    /// </list>
    ///
    /// Read once at startup. A configuration reload does not move an already-running UI, since the
    /// value is baked into both the served HTML and the fallback endpoint's route pattern.
    /// </summary>
    public string BasePath { get; set; } = "/";

    /// <summary>
    /// Where the telemetry REST API lives, from the browser's point of view — same-origin
    /// (<c>/api</c>, the default) or an absolute URL when the UI and API are hosted on different
    /// origins. No trailing slash; every one of the client's API services appends its own path
    /// segment (<c>/traces</c>, <c>/logs</c>, ...) directly onto this value.
    ///
    /// Served to the browser at <c>GET {BasePath}/config.json</c> and read there by the SPA before
    /// it bootstraps — see <c>src/telemetry-client/src/app/core/config/load-config.ts</c> — rather
    /// than baked into the compiled bundle, which is what makes one published bundle usable by
    /// any host regardless of where it mounts the API.
    ///
    /// Independent of <see cref="BasePath"/>, and deliberately not derived from it: a deployment that
    /// moves only the UI leaves the API where it was. When <c>TelemetryUi:ApiBasePath</c> is not set,
    /// <c>AddKeryheTelemetryUi</c> takes <c>Telemetry:Api:BasePath</c> (the API's own base path) if
    /// present, else <c>/api</c>; an explicit value, including an absolute URL for a different origin,
    /// always wins.
    /// </summary>
    public string ApiBasePath { get; set; } = "/api";

    /// <summary>
    /// The product name shown in the UI's header bar and, prepended to each page's own title, in
    /// the browser tab. Defaults to this UI's own out-of-the-box branding, so a host that sets
    /// nothing sees exactly what it always has.
    /// </summary>
    public string BrandName { get; set; } = "Sentinel";

    /// <summary>The tagline shown under <see cref="BrandName"/> in the header bar.</summary>
    public string BrandTagline { get; set; } = "OpenTelemetry Visualization";

    /// <summary>
    /// Overrides for the thresholds the Dashboard uses to colour its Error Rate card. Every value is
    /// optional and has no default here: the
    /// defaults live in the SPA (<c>health-thresholds.ts</c>), and only what is set is sent to the
    /// browser to be merged over them, so the two sides cannot drift apart.
    /// Deployment-wide; there are no per-tenant overrides.
    /// </summary>
    public HealthThresholdOptions HealthThresholds { get; set; } = new();

    /// <summary>
    /// How the UI authenticates to the API. See <see cref="AuthOptions"/>. Sent to the browser in
    /// <c>config.json</c> only when something is set.
    /// </summary>
    public AuthOptions Auth { get; set; } = new();

    /// <summary>
    /// Validates the settings that can be checked without knowing the SPA's defaults. Called at
    /// startup so a bad deployment fails there, rather than being ignored silently in each
    /// browser.
    /// </summary>
    /// <exception cref="InvalidOperationException">A health threshold is out of range, or a
    /// <c>Warn</c> is not below its <c>Critical</c>.</exception>
    internal void Validate()
    {
        HealthThresholds.Validate();
        Auth.Validate();
    }

    /// <summary>
    /// Reduces <see cref="BasePath"/> to one of exactly two shapes — <c>""</c> for the origin root,
    /// or <c>"/telemetry"</c> (leading slash, no trailing slash) — so every consumer can use
    /// <see cref="Http.PathString.StartsWithSegments(Http.PathString, out Http.PathString)"/> and
    /// route-pattern concatenation without a special case. <c>null</c>, <c>""</c>, <c>"/"</c> and
    /// <c>"///"</c> all mean the root.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not a usable path prefix. Validation is not cosmetic: the normalized value is
    /// concatenated into a route-pattern literal, where an unescaped <c>{</c>, <c>}</c>, <c>?</c>,
    /// <c>#</c> or <c>%</c> would produce a corrupt template — or, worse, a template that parses
    /// into something other than what was configured.
    /// </exception>
    internal static string NormalizeBasePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var normalized = "/" + trimmed;
        if (!BasePathPattern().IsMatch(normalized))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BasePath '{value}' is not a usable URL path prefix. Use one or more " +
                "path segments made of unreserved URL characters (letters, digits, '.', '_', '~' " +
                "or '-') — for example '/telemetry' — or '/' to serve the UI at the origin root.");
        }

        return normalized;
    }

    /// <summary>
    /// The <c>&lt;base href&gt;</c> form of a normalized base path. The trailing slash is required:
    /// without it the browser resolves relative asset references against the prefix's
    /// <em>parent</em>, so <c>&lt;base href="/telemetry"&gt;</c> would send every request to the
    /// origin root.
    /// </summary>
    internal static string ToBaseHref(string normalizedBasePath) =>
        normalizedBasePath.Length == 0 ? "/" : normalizedBasePath + "/";

    [GeneratedRegex(@"^(/[A-Za-z0-9._~-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex BasePathPattern();
}

/// <summary>
/// Optional overrides for the Dashboard's health thresholds. Rates are 0-1 fractions (0.05 is 5%),
/// matching the SPA's own units. See <see cref="TelemetryUiOptions.HealthThresholds"/>.
/// </summary>
public sealed class HealthThresholdOptions
{
    /// <summary>Trace error rate: <c>Warn</c> colours the card as a warning, <c>Critical</c> as an error.</summary>
    public HealthBandOptions ErrorRate { get; set; } = new();

    internal void Validate()
    {
        CheckBound($"{nameof(ErrorRate)}:Warn", ErrorRate.Warn);
        CheckBound($"{nameof(ErrorRate)}:Critical", ErrorRate.Critical);

        if (ErrorRate.Warn is { } warn && ErrorRate.Critical is { } critical && warn >= critical)
        {
            throw new InvalidOperationException(
                $"{TelemetryUiOptions.SectionName}:HealthThresholds:{nameof(ErrorRate)}: Warn ({warn}) must be below Critical ({critical}).");
        }
    }

    /// <summary>
    /// The set values as the <c>healthThresholds</c> object <c>config.json</c> carries, or null when
    /// nothing is set so the key is omitted entirely.
    /// </summary>
    internal Dictionary<string, object>? ToConfigPayload()
    {
        var values = new Dictionary<string, object>();
        if (ErrorRate.Warn is { } warn) values["warn"] = warn;
        if (ErrorRate.Critical is { } critical) values["critical"] = critical;

        return values.Count == 0 ? null : new Dictionary<string, object> { ["errorRate"] = values };
    }

    private static void CheckBound(string name, double? value)
    {
        if (value is null) return;
        if (!double.IsFinite(value.Value) || value <= 0 || value > 1)
        {
            throw new InvalidOperationException(
                $"{TelemetryUiOptions.SectionName}:HealthThresholds:{name} '{value}' must be greater than 0 and at most 1.");
        }
    }
}

/// <summary>A warn/critical pair for one health threshold; either may be left unset.</summary>
public sealed class HealthBandOptions
{
    /// <summary>The value at or above which the card is coloured as a warning.</summary>
    public double? Warn { get; set; }

    /// <summary>The value at or above which the card is coloured as an error.</summary>
    public double? Critical { get; set; }
}

/// <summary>
/// The <c>TelemetryUi:Auth</c> section. <c>cookie</c> (default): the host owns sign-in and requests
/// carry its cookie; the SPA holds no tokens and only redirects to <see cref="LoginUrl"/> on a 401.
/// <c>oidc</c>: for bearer-only deployments the SPA signs in itself (authorization code + PKCE) against
/// <see cref="Oidc"/> and sends the access token to the API.
/// </summary>
public sealed class AuthOptions
{
    public const string CookieMode = "cookie";
    public const string OidcMode = "oidc";

    /// <summary><c>cookie</c> (default) or <c>oidc</c>.</summary>
    public string Mode { get; set; } = CookieMode;

    /// <summary>Where the SPA sends a signed-out user (<c>cookie</c> mode); gets a <c>returnUrl</c> query parameter.</summary>
    public string? LoginUrl { get; set; }

    /// <summary>Target of the header's "Sign out" item (<c>cookie</c> mode). The OIDC end-session endpoint is used in <c>oidc</c> mode.</summary>
    public string? LogoutUrl { get; set; }

    /// <summary>Send credentials (cookies) on cross-origin API calls. The API host's CORS policy then needs <c>AllowCredentials()</c> with explicit origins.</summary>
    public bool IncludeCredentials { get; set; }

    /// <summary>Settings for <c>oidc</c> mode.</summary>
    public OidcOptions Oidc { get; set; } = new();

    internal bool IsDefault =>
        Mode == CookieMode && string.IsNullOrWhiteSpace(LoginUrl) && string.IsNullOrWhiteSpace(LogoutUrl) && !IncludeCredentials;

    internal void Validate()
    {
        if (Mode is not (CookieMode or OidcMode))
            throw new InvalidOperationException($"{TelemetryUiOptions.SectionName}:Auth:Mode '{Mode}' must be '{CookieMode}' or '{OidcMode}'.");

        CheckUrl(nameof(LoginUrl), LoginUrl);
        CheckUrl(nameof(LogoutUrl), LogoutUrl);

        if (Mode == OidcMode)
        {
            if (!Uri.TryCreate(Oidc.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"{TelemetryUiOptions.SectionName}:Auth:Oidc:Authority must be an absolute https URL in '{OidcMode}' mode.");
            if (string.IsNullOrWhiteSpace(Oidc.ClientId))
                throw new InvalidOperationException($"{TelemetryUiOptions.SectionName}:Auth:Oidc:ClientId is required in '{OidcMode}' mode.");
        }
    }

    // Relative ("/account/login") or absolute http(s); anything else (javascript:, data:) is refused
    // because the SPA navigates to these values.
    private static void CheckUrl(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var ok = value.StartsWith('/') && !value.StartsWith("//")
            || Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        if (!ok)
            throw new InvalidOperationException($"{TelemetryUiOptions.SectionName}:Auth:{name} '{value}' must be a relative path starting with '/' or an absolute http(s) URL.");
    }

    internal Dictionary<string, object>? ToConfigPayload()
    {
        if (IsDefault && Mode == CookieMode) return null;
        var auth = new Dictionary<string, object> { ["mode"] = Mode, ["includeCredentials"] = IncludeCredentials };
        if (!string.IsNullOrWhiteSpace(LoginUrl)) auth["loginUrl"] = LoginUrl;
        if (!string.IsNullOrWhiteSpace(LogoutUrl)) auth["logoutUrl"] = LogoutUrl;
        if (Mode == OidcMode)
            auth["oidc"] = new Dictionary<string, object>
            {
                ["authority"] = Oidc.Authority!,
                ["clientId"] = Oidc.ClientId!,
                ["scope"] = Oidc.Scope,
            };
        return auth;
    }
}

/// <summary>Registration values for <c>oidc</c> mode: an SPA client (public, authorization code + PKCE).</summary>
public sealed class OidcOptions
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string Scope { get; set; } = "openid profile";
}
