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
    /// <item>It moves the UI only. The API's controllers are routed at <c>api/*</c> on the origin
    /// root regardless, so <see cref="ApiBasePath"/> is a separate setting and keeps its own
    /// default — set it too if your deployment moves the API as well.</item>
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
    /// Independent of <see cref="BasePath"/>, and deliberately not derived from it: the API's own
    /// controllers are routed at the origin root, so a deployment that moves only the UI leaves
    /// this default correct.
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
    /// Overrides for the thresholds the Global Dashboard uses to label a tenant healthy, warning,
    /// degraded, slow-tailed or silent. Every value is optional and has no default here: the
    /// defaults live in the SPA (<c>health-thresholds.ts</c>), and only what is set is sent to the
    /// browser to be merged over them, so the two sides cannot drift apart.
    /// Deployment-wide; there are no per-tenant overrides.
    /// </summary>
    public HealthThresholdOptions HealthThresholds { get; set; } = new();

    /// <summary>
    /// Validates the settings that can be checked without knowing the SPA's defaults. Called at
    /// startup so a bad deployment fails there, rather than being ignored silently in each
    /// browser.
    /// </summary>
    /// <exception cref="InvalidOperationException">A health threshold is out of range, or a
    /// <c>Warn</c> is not below its <c>Critical</c>.</exception>
    internal void Validate() => HealthThresholds.Validate();

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
/// Optional overrides for the Global Dashboard's health thresholds. Rates are 0-1 fractions (0.05
/// is 5%), durations are milliseconds, matching the SPA's own units. See
/// <see cref="TelemetryUiOptions.HealthThresholds"/>.
/// </summary>
public sealed class HealthThresholdOptions
{
    /// <summary>Trace error rate: <c>Warn</c> gives a warning, <c>Critical</c> degraded.</summary>
    public HealthBandOptions ErrorRate { get; set; } = new();

    /// <summary>Window p95 trace duration in milliseconds, with the same two bands.</summary>
    public HealthBandOptions P95Ms { get; set; } = new();

    /// <summary>
    /// Error and fatal logs as a fraction of all logs. Only <c>Critical</c> raises a tenant's
    /// status (to warning); <c>Warn</c> only colours the log count.
    /// </summary>
    public HealthBandOptions LogErrorRate { get; set; } = new();

    /// <summary>Traces needed in the window before latency rules apply.</summary>
    public int? MinSamples { get; set; }

    /// <summary>A p99 above this multiple of the p95 is a slow tail.</summary>
    public double? TailRatio { get; set; }

    /// <summary>
    /// A tenant with no traces in the window and nothing seen for longer than this (milliseconds)
    /// is silent rather than idle.
    /// </summary>
    public double? SilentAfterMs { get; set; }

    internal void Validate()
    {
        ValidateBand(nameof(ErrorRate), ErrorRate, 0, 1);
        ValidateBand(nameof(P95Ms), P95Ms, 0, double.MaxValue);
        ValidateBand(nameof(LogErrorRate), LogErrorRate, 0, 1);

        if (MinSamples is < 1)
            throw Invalid(nameof(MinSamples), MinSamples, "must be at least 1");
        if (TailRatio is not null && !(TailRatio > 1 && double.IsFinite(TailRatio.Value)))
            throw Invalid(nameof(TailRatio), TailRatio, "must be a number greater than 1");
        if (SilentAfterMs is not null && !(SilentAfterMs > 0 && double.IsFinite(SilentAfterMs.Value)))
            throw Invalid(nameof(SilentAfterMs), SilentAfterMs, "must be a number greater than 0");
    }

    /// <summary>
    /// The set values as the <c>healthThresholds</c> object <c>config.json</c> carries, or null when
    /// nothing is set so the key is omitted entirely.
    /// </summary>
    internal Dictionary<string, object>? ToConfigPayload()
    {
        var payload = new Dictionary<string, object>();

        AddBand(payload, "errorRate", ErrorRate);
        AddBand(payload, "p95Ms", P95Ms);
        AddBand(payload, "logErrorRate", LogErrorRate);
        if (MinSamples is { } minSamples) payload["minSamples"] = minSamples;
        if (TailRatio is { } tailRatio) payload["tailRatio"] = tailRatio;
        if (SilentAfterMs is { } silentAfterMs) payload["silentAfterMs"] = silentAfterMs;

        return payload.Count == 0 ? null : payload;
    }

    private static void AddBand(Dictionary<string, object> payload, string key, HealthBandOptions band)
    {
        var values = new Dictionary<string, object>();
        if (band.Warn is { } warn) values["warn"] = warn;
        if (band.Critical is { } critical) values["critical"] = critical;
        if (values.Count > 0) payload[key] = values;
    }

    private static void ValidateBand(string name, HealthBandOptions band, double min, double max)
    {
        CheckBound($"{name}:Warn", band.Warn, min, max);
        CheckBound($"{name}:Critical", band.Critical, min, max);

        if (band.Warn is { } warn && band.Critical is { } critical && warn >= critical)
        {
            throw new InvalidOperationException(
                $"{TelemetryUiOptions.SectionName}:HealthThresholds:{name}: Warn ({warn}) must be below Critical ({critical}).");
        }
    }

    private static void CheckBound(string name, double? value, double min, double max)
    {
        if (value is null) return;
        if (!double.IsFinite(value.Value) || value <= min || value > max)
        {
            throw Invalid(name, value,
                max == double.MaxValue ? $"must be greater than {min}" : $"must be greater than {min} and at most {max}");
        }
    }

    private static InvalidOperationException Invalid(string name, object? value, string rule) =>
        new($"{TelemetryUiOptions.SectionName}:HealthThresholds:{name} '{value}' {rule}.");
}

/// <summary>A warn/critical pair for one health threshold; either may be left unset.</summary>
public sealed class HealthBandOptions
{
    /// <summary>The value at or above which a tenant is a warning.</summary>
    public double? Warn { get; set; }

    /// <summary>The value at or above which a tenant is degraded.</summary>
    public double? Critical { get; set; }
}
