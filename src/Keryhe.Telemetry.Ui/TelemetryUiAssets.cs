using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Keryhe.Telemetry.Ui;

/// <summary>
/// The two facts about this package's packaged bundle that both the middleware pipeline
/// (<c>TelemetryUiApplicationBuilderExtensions</c>) and the in-memory SPA shell
/// (<see cref="TelemetryUiShell"/>) need to agree on: where the assets live, and which compressed
/// variant a given request would accept. Kept here rather than duplicated in either, since a
/// disagreement between the two would surface as assets resolving in one code path and 404ing in
/// the other.
/// </summary>
internal static class TelemetryUiAssets
{
    /// <summary>
    /// The path this assembly's static web assets are served under by default — the same rule
    /// <c>StaticWebAssetsLoader</c> applies to every referenced Razor class library.
    /// </summary>
    internal static readonly string ContentPath =
        $"_content/{typeof(TelemetryUiAssets).Assembly.GetName().Name}";

    /// <summary>
    /// Re-roots the host's web root at <see cref="ContentPath"/>, so this package's bundle can be
    /// addressed as if it sat at the root — see <see cref="SubPathFileProvider"/> for why wrapping
    /// the host's provider beats pointing a fresh <c>PhysicalFileProvider</c> at the same directory.
    /// </summary>
    internal static IFileProvider CreateFileProvider(IWebHostEnvironment environment) =>
        new SubPathFileProvider(environment.WebRootFileProvider, ContentPath);

    /// <summary>
    /// "br" if the client's Accept-Encoding allows it, else "gzip" if that does, else null — the
    /// two encodings this package has a compressed variant for. Brotli is preferred as the smaller
    /// of the two whenever both are offered. Uses the same <see cref="StringWithQualityHeaderValue"/>
    /// parsing ASP.NET Core's own response-compression middleware relies on, so a "q=0" exclusion is
    /// honored rather than treated as a substring match on the raw header.
    /// </summary>
    internal static string? PreferredCompressedEncoding(HttpRequest request)
    {
        var header = request.Headers.AcceptEncoding;
        if (header.Count == 0)
        {
            return null;
        }

        if (!StringWithQualityHeaderValue.TryParseList(header, out var values) || values.Count == 0)
        {
            return null;
        }

        bool Accepts(string encoding) => values.Any(v =>
            string.Equals(v.Value.Value, encoding, StringComparison.OrdinalIgnoreCase) &&
            v.Quality is not 0);

        if (Accepts("br"))
        {
            return "br";
        }

        return Accepts("gzip") ? "gzip" : null;
    }
}
