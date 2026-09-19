using Microsoft.AspNetCore.StaticFiles;

namespace Keryhe.Telemetry.Ui;

/// <summary>
/// Resolves the content type of a <c>.br</c>/<c>.gz</c> file from the extension of the file it
/// compresses, not its own — <c>StaticFileMiddleware</c>'s default provider does not know either
/// suffix and would otherwise report <c>application/octet-stream</c> for
/// <c>chunk-abc123.js.br</c> instead of <c>text/javascript</c>. Paired with the middleware in
/// <see cref="TelemetryUiApplicationBuilderExtensions"/> that rewrites the request path to the
/// compressed sibling; this is what lets the *served* file's content type still match the
/// *original*, uncompressed one.
/// </summary>
internal sealed class PrecompressedContentTypeProvider : IContentTypeProvider
{
    private readonly IContentTypeProvider _inner = new FileExtensionContentTypeProvider();

    public bool TryGetContentType(string subpath, out string contentType)
    {
        if (subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ||
            subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            subpath = subpath[..subpath.LastIndexOf('.')];
        }

        return _inner.TryGetContentType(subpath, out contentType!);
    }
}
