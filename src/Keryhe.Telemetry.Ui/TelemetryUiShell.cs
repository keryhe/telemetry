using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Keryhe.Telemetry.Ui;

/// <summary>
/// The SPA shell in every representation it is served in, built once at startup.
/// </summary>
/// <param name="Identity">The patched <c>index.html</c>, UTF-8, no BOM.</param>
/// <param name="Brotli">The same bytes, Brotli-compressed.</param>
/// <param name="Gzip">The same bytes, Gzip-compressed.</param>
/// <param name="ETag">Strong ETag for <paramref name="Identity"/>.</param>
/// <param name="BrotliETag">Strong ETag for <paramref name="Brotli"/>.</param>
/// <param name="GzipETag">Strong ETag for <paramref name="Gzip"/>.</param>
internal sealed record ShellContent(
    byte[] Identity,
    byte[] Brotli,
    byte[] Gzip,
    string ETag,
    string BrotliETag,
    string GzipETag);

/// <summary>
/// Holds the SPA shell <em>in memory</em>, with its <c>&lt;base href&gt;</c> rewritten to the
/// host's configured <see cref="TelemetryUiOptions.BasePath"/>.
///
/// The packaged <c>index.html</c> ships with <c>&lt;base href="/"&gt;</c> baked in by the Angular
/// build, and every asset reference in it — plus the client's own <c>config.json</c> fetch and its
/// router's <c>PathLocationStrategy</c> — resolves relative to that one attribute. Serving the file
/// straight off the file provider therefore pins the whole UI to the origin root. Patching it once
/// here, and serving the result from memory rather than through <c>UseStaticFiles</c> or
/// <c>MapFallbackToFile</c>, is what lets a single prebuilt bundle be mounted anywhere — the
/// property that justifies shipping this package prebuilt in the first place.
///
/// Serving from memory means this type also owns what those two middlewares used to provide for
/// free: content type, content length, ETag and conditional-request handling. See
/// <see cref="TryServeAsync"/>.
/// </summary>
internal sealed partial class TelemetryUiShell(
    IWebHostEnvironment environment,
    ILogger<TelemetryUiShell> logger)
{
    private readonly object _gate = new();

    /// <summary>
    /// The normalized, browser-visible path prefix the UI is mounted at: <c>""</c> for the origin
    /// root, or <c>"/telemetry"</c>. Read by both the middleware pipeline and the fallback endpoint
    /// so the two cannot disagree about where the UI lives.
    /// </summary>
    public string BasePath { get; private set; } = string.Empty;

    /// <summary>Whether <see cref="Initialize"/> has run and <see cref="BasePath"/> is settled.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// The built shell, or <see langword="null"/> when this package was built without a Node
    /// toolchain and so packages no <c>index.html</c> — a case the csproj deliberately degrades to
    /// a warning rather than a build failure, and which must therefore degrade to a 404 here rather
    /// than an exception.
    /// </summary>
    public ShellContent? Content { get; private set; }

    /// <summary>
    /// Fixes the effective options and builds every representation of the shell. Idempotent; the
    /// first call wins.
    ///
    /// Called from <c>UseKeryheTelemetryUi</c> <em>after</em> its <c>configure</c> override has been
    /// applied, so there is exactly one moment at which <see cref="BasePath"/> becomes real. That
    /// matters because <c>MapKeryheTelemetryUiFallback</c> builds its route pattern from it: were
    /// the two allowed to read the options independently, a host that mapped the fallback before
    /// calling <c>UseKeryheTelemetryUi</c> would silently register it at the wrong prefix.
    /// </summary>
    public void Initialize(TelemetryUiOptions options)
    {
        lock (_gate)
        {
            if (IsInitialized)
            {
                return;
            }

            BasePath = TelemetryUiOptions.NormalizeBasePath(options.BasePath);
            Content = BuildContent(TelemetryUiOptions.ToBaseHref(BasePath));
            IsInitialized = true;
        }
    }

    /// <summary>
    /// Writes the shell to <paramref name="context"/>, negotiating Brotli/Gzip and honoring
    /// <c>If-None-Match</c>. Returns <see langword="false"/> without touching the response when
    /// there is nothing to serve — no packaged <c>index.html</c>, or a method other than GET/HEAD —
    /// so callers can fall through to the next middleware or answer 404.
    ///
    /// Header order is load-bearing. The three headers a 304 is required to carry are written
    /// first, and the 304 branch returns before <c>Content-Type</c>, <c>Content-Encoding</c> or
    /// <c>Content-Length</c> is set. <c>Content-Length</c> is the length of the <em>encoded</em>
    /// bytes actually written: setting it from the identity length while writing a compressed body
    /// is the same <c>net::ERR_CONTENT_DECODING_FAILED</c> failure this package's static-file
    /// negotiation already has a long comment about, just reached by a different route.
    /// </summary>
    public async Task<bool> TryServeAsync(HttpContext context)
    {
        var content = Content;
        if (content is null)
        {
            return false;
        }

        var request = context.Request;

        // MapFallback maps every HTTP method, unlike the MapFallbackToFile this replaced — that one
        // ran a StaticFileMiddleware internally, which rejected anything but GET/HEAD. Without this
        // gate a POST to a client-side route would be answered with 200 and an HTML body.
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var encoding = TelemetryUiAssets.PreferredCompressedEncoding(request);
        var (bytes, etag) = encoding switch
        {
            "br" => (content.Brotli, content.BrotliETag),
            "gzip" => (content.Gzip, content.GzipETag),
            _ => (content.Identity, content.ETag),
        };

        var response = context.Response;
        response.Headers.ETag = etag;

        // Revalidate on every load. The shell is the one file in the bundle whose name is not
        // fingerprinted, so a cached copy pointing at superseded fingerprinted assets is exactly
        // what a deployment must not leave behind. "no-cache" still permits the 304 below;
        // "no-store" would not.
        response.Headers.CacheControl = "no-cache";

        // Sent even on an identity response, so an intermediary caches per encoding rather than
        // serving whichever representation it happened to see first.
        response.Headers.Vary = HeaderNames.AcceptEncoding;

        if (MatchesIfNoneMatch(request, etag))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return true;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/html; charset=utf-8";
        if (encoding is not null)
        {
            response.Headers.ContentEncoding = encoding;
        }

        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes);
        return true;
    }

    /// <summary>
    /// Reads the packaged <c>index.html</c>, rewrites its <c>&lt;base href&gt;</c> to
    /// <paramref name="baseHref"/>, and precomputes every representation plus its ETag.
    ///
    /// Compression happens once here rather than per request: the shell is ~27 KB, and Brotli at
    /// this size is worth tens of milliseconds exactly once.
    /// </summary>
    private ShellContent? BuildContent(string baseHref)
    {
        var file = TelemetryUiAssets.CreateFileProvider(environment).GetFileInfo("index.html");
        if (!file.Exists)
        {
            logger.LogError(
                "Keryhe.Telemetry.Ui packages no index.html, so the UI cannot be served and every " +
                "UI route will return 404. This is what a build of this package without a Node " +
                "toolchain produces — rebuild it with Node available, or publish with " +
                "-p:BuildSpa=false against an already-built src/telemetry-client/dist.");
            return null;
        }

        string html;
        using (var stream = file.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            html = reader.ReadToEnd();
        }

        var match = BaseTag().Match(html);
        string patched;
        if (match.Success)
        {
            patched = string.Concat(
                html.AsSpan(0, match.Index),
                $"""<base href="{baseHref}">""",
                html.AsSpan(match.Index + match.Length));
        }
        else
        {
            patched = html;
            logger.LogWarning(
                "The packaged index.html contains no <base href> tag, so its asset references will " +
                "resolve against the document URL instead of a fixed prefix. The UI will not work " +
                "correctly when mounted at '{BasePath}', and deep links may fail even at the root.",
                BasePath.Length == 0 ? "/" : BasePath);
        }

        // Written without a BOM: the byte array is served verbatim, and a leading U+FEFF would land
        // in the response body.
        var identity = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(patched);
        var hash = Convert.ToHexStringLower(SHA256.HashData(identity));

        // A distinct ETag per representation: an ETag shared across encodings makes two different
        // byte streams claim the same identity, which a shared cache is entitled to act on.
        return new ShellContent(
            identity,
            Compress(identity, brotli: true),
            Compress(identity, brotli: false),
            $"\"{hash}\"",
            $"\"{hash}-br\"",
            $"\"{hash}-gz\"");
    }

    private static byte[] Compress(byte[] source, bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream compressor = brotli
            ? new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)
            : new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(source);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Weak comparison per RFC 9110 §13.1.2, which is what <c>If-None-Match</c> specifies even for
    /// strong tags, plus the <c>*</c> wildcard.
    /// </summary>
    private static bool MatchesIfNoneMatch(HttpRequest request, string etag)
    {
        var header = request.Headers.IfNoneMatch;
        if (header.Count == 0)
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(header, out var tags) || tags.Count == 0)
        {
            return false;
        }

        var candidate = new EntityTagHeaderValue(etag);
        foreach (var tag in tags)
        {
            if (tag.Tag.Equals("*", StringComparison.Ordinal) ||
                tag.Compare(candidate, useStrongComparison: false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches the whole <c>&lt;base&gt;</c> tag, anchored at the element name and unable to cross
    /// its own <c>&gt;</c>, so the inlined critical CSS the Angular build writes into the same
    /// document cannot be matched by accident. Attribute order, quote style, whitespace and casing
    /// are all tolerated, since this tag is emitted by a toolchain this package does not control.
    /// </summary>
    [GeneratedRegex(
        """<base\b[^>]*?\bhref\s*=\s*(?:"[^"]*"|'[^']*')[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BaseTag();
}
