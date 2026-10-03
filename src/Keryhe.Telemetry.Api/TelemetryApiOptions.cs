using System.Text.RegularExpressions;
using Keryhe.Telemetry.Api.Authorization;

namespace Keryhe.Telemetry.Api;

/// <summary>
/// Settings for the REST API, bound from the <c>Telemetry:Api</c> configuration section by
/// <c>AddKeryheTelemetryApi</c> and then optionally overridden in code. Read once at startup.
/// </summary>
public sealed partial class TelemetryApiOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Telemetry:Api";

    /// <summary>
    /// The URL path prefix every API route lives under (default <c>/api</c>), for example
    /// <c>/telemetry/api</c> to mount the API inside a consumer's own host. <c>/</c> and empty are
    /// rejected: the API's segments (<c>/traces</c>, <c>/logs</c>, ...) are also UI routes, so an API
    /// at the root would shadow the UI's deep links.
    /// </summary>
    public string BasePath { get; set; } = "/api";

    /// <summary>Authorization settings. Disabled by default.</summary>
    public TelemetryApiAuthorizationOptions Authorization { get; set; } = new();

    /// <summary>The base path as a leading-slash, no-trailing-slash value (<c>/api</c>).</summary>
    public string NormalizedBasePath => NormalizeBasePath(BasePath);

    // Twin of TelemetryUiOptions.NormalizeBasePath (Keryhe.Telemetry.Ui). The Api assembly does not
    // reference the Ui assembly, and moving the regex to Core would make the Ui package depend on
    // Core for one pattern. The value becomes a route-template literal, so '{', '}', '?', '#' and '%'
    // must be refused. Unlike the UI's, the root is not accepted here.
    internal static string NormalizeBasePath(string? value)
    {
        var trimmed = value?.Trim().Trim('/') ?? string.Empty;
        var normalized = "/" + trimmed;
        if (trimmed.Length == 0 || !BasePathPattern().IsMatch(normalized))
        {
            throw new InvalidOperationException(
                $"{SectionName}:BasePath '{value}' is not a usable API path prefix. Use one or more path " +
                "segments made of unreserved URL characters (letters, digits, '.', '_', '~' or '-'), for " +
                "example '/api' or '/telemetry/api'. '/' is not allowed because the API's segments would " +
                "shadow the UI's routes.");
        }

        return normalized;
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9._~-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex BasePathPattern();
}
