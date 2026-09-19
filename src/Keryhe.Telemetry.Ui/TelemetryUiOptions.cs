namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Options for <see cref="TelemetryUiApplicationBuilderExtensions.UseKeryheTelemetryUi"/>.
/// </summary>
public sealed class TelemetryUiOptions
{
    /// <summary>
    /// Where the telemetry REST API lives, from the browser's point of view — same-origin
    /// (<c>/api</c>, the default) or an absolute URL when the UI and API are hosted on different
    /// origins. No trailing slash; every one of the client's API services appends its own path
    /// segment (<c>/traces</c>, <c>/logs</c>, ...) directly onto this value.
    ///
    /// Served to the browser at <c>GET /config.json</c> and read there by the SPA before it
    /// bootstraps — see <c>src/telemetry-client/src/app/core/config/load-config.ts</c> — rather
    /// than baked into the compiled bundle, which is what makes one published bundle usable by
    /// any host regardless of where it mounts the API.
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
}
