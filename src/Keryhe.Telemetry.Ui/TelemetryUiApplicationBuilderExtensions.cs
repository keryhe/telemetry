using System.Reflection;
using System.Text.Json;
using Keryhe.Telemetry.Ui;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Middleware and endpoint extensions that serve the prebuilt Angular UI packaged in this
/// assembly's <c>wwwroot</c> (see the csproj's <c>BuildAngularClient</c>/<c>IncludeAngularClient</c>
/// targets) at the origin root, mirroring <c>TelemetryApiApplicationBuilderExtensions</c> and
/// <c>TelemetryCollectorEndpointRouteBuilderExtensions</c> on the API/collector side.
/// </summary>
public static class TelemetryUiApplicationBuilderExtensions
{
    /// <summary>
    /// The path this assembly's static web assets are served under by default — the same rule
    /// <c>StaticWebAssetsLoader</c> applies to every referenced Razor class library.
    /// </summary>
    private static readonly string ContentPath =
        $"_content/{typeof(TelemetryUiApplicationBuilderExtensions).Assembly.GetName().Name}";

    /// <summary>
    /// Serves the packaged SPA's static files (JS/CSS/images, fingerprinted and precompressed by
    /// the SDK) and its <c>index.html</c> at "/" instead of the RCL default of
    /// <c>/_content/Keryhe.Telemetry.Ui/</c>, and answers <c>GET /config.json</c> with the
    /// <paramref name="configure"/>-supplied <see cref="TelemetryUiOptions"/> — API location and
    /// branding alike — instead of the packaged dev-time defaults the SPA ships with.
    ///
    /// Call before <c>UseKeryheTelemetryApi()</c> (or any other tenant-scoped middleware), the
    /// same ordering <c>UseDefaultFiles</c>/<c>UseStaticFiles</c> already required when this logic
    /// lived directly in each host's <c>Program.cs</c> — so UI asset requests, including
    /// <c>/config.json</c>, never pay for tenant resolution. Call <c>MapControllers()</c> and
    /// <see cref="MapKeryheTelemetryUiFallback"/> afterwards to complete the pipeline.
    ///
    /// The host must also call <c>app.UseRouting()</c> explicitly, immediately after this method —
    /// not rely on <see cref="WebApplication"/>'s own implicit insertion of it. Left implicit,
    /// that insertion lands ahead of this method's middleware (since some <c>Map*</c> call exists
    /// later in every host that uses this package), so routing pre-selects an endpoint for "/"
    /// before this method's static-file middleware ever runs — <c>MapKeryheTelemetryUiFallback</c>'s
    /// route pattern matches any extensionless path, "/" included — and static file middleware,
    /// finding an endpoint already selected, defers to it rather than serving. The practical
    /// symptom is exactly the one route that most needs this method's compression negotiation
    /// silently not getting it, while every other route works, since the fallback's own
    /// (uncompressed) handler still serves the same content.
    /// </summary>
    public static WebApplication UseKeryheTelemetryUi(
        this WebApplication app,
        Action<TelemetryUiOptions>? configure = null)
    {
        var options = new TelemetryUiOptions();
        configure?.Invoke(options);

        WarnIfAssemblyVersionMismatch(app.Logger);

        var uiFiles = new SubPathFileProvider(app.Environment.WebRootFileProvider, ContentPath);

        // Answered as plain middleware, not a mapped endpoint: endpoint execution happens wherever
        // routing terminates the pipeline, which need not be where MapGet is called, while this
        // non-terminal middleware — like UseStaticFiles right below it — runs in the registration
        // order this method's own call site puts it in. That is what lets "before the tenant
        // middleware" mean what it says.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Equals("/config.json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new
                    {
                        apiUrl = options.ApiBasePath,
                        brandName = options.BrandName,
                        brandTagline = options.BrandTagline,
                    }));
                return;
            }

            await next(context);
        });

        // A plain rewrite of "/" to "/index.html", in place of UseDefaultFiles: that middleware
        // resolves a default file by listing the target directory's contents, and
        // SubPathFileProvider's GetDirectoryContents did not reliably resolve through the
        // composite WebRootFileProvider (GetFileInfo for an exact path, used everywhere else in
        // this method, did) — "/" fell through all the way to MapKeryheTelemetryUiFallback's
        // *uncompressed* endpoint instead of the negotiated path below. The SPA has exactly one
        // default file, so this needs no directory listing at all.
        app.Use((context, next) =>
        {
            if (context.Request.Path == "/")
            {
                context.Request.Path = "/index.html";
            }

            return next(context);
        });
        UsePrecompressedAssetNegotiation(app, uiFiles);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = uiFiles,
            ContentTypeProvider = new PrecompressedContentTypeProvider(),
        });

        return app;
    }

    /// <summary>
    /// Serves the SPA shell for anything not matched by a concrete route, so client-side routes
    /// like <c>/traces/:id</c> survive a hard reload. Call after <c>MapControllers()</c> (and any
    /// gRPC service mappings) so <c>/api/*</c> is never swallowed by this fallback.
    ///
    /// Unlike <see cref="UseKeryheTelemetryUi"/>'s static files, this does not negotiate a
    /// precompressed variant — <c>MapFallbackToFile</c> serves through its own endpoint, outside
    /// this class's middleware chain, and <c>index.html</c> (tens of KB) is a much smaller win
    /// than the megabyte-scale JS bundle that route already handles.
    /// </summary>
    public static IEndpointRouteBuilder MapKeryheTelemetryUiFallback(this IEndpointRouteBuilder endpoints)
    {
        var environment = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
        var uiFiles = new SubPathFileProvider(environment.WebRootFileProvider, ContentPath);

        endpoints.MapFallbackToFile("index.html", new StaticFileOptions
        {
            FileProvider = uiFiles,
            ContentTypeProvider = new PrecompressedContentTypeProvider(),
        });

        return endpoints;
    }

    /// <summary>
    /// Serves a <c>.br</c>/<c>.gz</c> sibling of the requested asset when the client accepts it,
    /// with the matching <c>Content-Encoding</c> — the compression itself is generated for free
    /// by the SDK's static web asset publish pipeline (see the csproj), but content-negotiated
    /// serving of the result is otherwise a feature of <c>MapStaticAssets()</c> alone, which maps
    /// a referenced Razor class library's assets at their <em>declared</em> path
    /// (<c>/_content/Keryhe.Telemetry.Ui/...</c>) with no supported way to re-root them at "/" the
    /// way <see cref="SubPathFileProvider"/> does for <c>UseStaticFiles</c>. This is the
    /// documented "serve pre-compressed files" pattern for that middleware instead, so re-rooting
    /// and compression are not a choice between one or the other.
    /// </summary>
    private static void UsePrecompressedAssetNegotiation(WebApplication app, IFileProvider uiFiles)
    {
        app.Use(async (context, next) =>
        {
            var encoding = PreferredCompressedEncoding(context.Request);
            if (encoding is not null && HttpMethods.IsGet(context.Request.Method))
            {
                var suffix = encoding == "br" ? ".br" : ".gz";
                var compressedPath = context.Request.Path.Value + suffix;

                if (uiFiles.GetFileInfo(compressedPath).Exists)
                {
                    context.Request.Path = new PathString(compressedPath);
                    context.Response.Headers.ContentEncoding = encoding;
                    context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// "br" if the client's Accept-Encoding allows it, else "gzip" if that does, else null — the
    /// two encodings <see cref="IncludeAngularClient"/> generates a sibling for. Brotli is
    /// preferred as the smaller of the two whenever both are offered. Uses the same
    /// <see cref="StringWithQualityHeaderValue"/> parsing ASP.NET Core's own response-compression
    /// middleware relies on, so a "q=0" exclusion is honored rather than treated as a substring
    /// match on the raw header.
    /// </summary>
    private static string? PreferredCompressedEncoding(HttpRequest request)
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

    /// <summary>
    /// The UI's TypeScript models are hand-maintained mirrors of the API's C# DTOs with no shared
    /// contract generated between them (see plans/ui-packaging-runtime-config.md §2.7) — so a
    /// version-mismatched pair fails silently, as a field quietly missing from a rendered page,
    /// not as an exception. Looked up by assembly name via reflection, not a project reference:
    /// Keryhe.Telemetry.Ui must stay usable without ever depending on Keryhe.Telemetry.Api.
    /// </summary>
    private static void WarnIfAssemblyVersionMismatch(ILogger logger)
    {
        var apiAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Keryhe.Telemetry.Api");
        if (apiAssembly is null)
        {
            return;
        }

        var uiVersion = InformationalVersionOf(typeof(TelemetryUiApplicationBuilderExtensions).Assembly);
        var apiVersion = InformationalVersionOf(apiAssembly);

        if (uiVersion is not null && apiVersion is not null && uiVersion != apiVersion)
        {
            logger.LogWarning(
                "Keryhe.Telemetry.Ui {UiVersion} is running against Keryhe.Telemetry.Api {ApiVersion}. " +
                "These ship in lockstep; a mismatch can surface as a field silently missing from the UI " +
                "rather than as an error. Pin both packages to the same version.",
                uiVersion, apiVersion);
        }
    }

    private static string? InformationalVersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
}
