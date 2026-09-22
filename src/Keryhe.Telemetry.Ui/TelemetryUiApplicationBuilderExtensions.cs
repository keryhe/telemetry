using System.Reflection;
using System.Text.Json;
using Keryhe.Telemetry.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Middleware and endpoint extensions that serve the prebuilt Angular UI packaged in this
/// assembly's <c>wwwroot</c> (see the csproj's <c>BuildAngularClient</c>/<c>IncludeAngularClient</c>
/// targets), mirroring <c>TelemetryApiApplicationBuilderExtensions</c> and
/// <c>TelemetryCollectorEndpointRouteBuilderExtensions</c> on the API/collector side.
/// </summary>
public static class TelemetryUiApplicationBuilderExtensions
{
    /// <summary>
    /// Serves the packaged SPA's static files (JS/CSS/images, fingerprinted and precompressed by
    /// the SDK) and its <c>index.html</c> at <see cref="TelemetryUiOptions.BasePath"/> — "/" by
    /// default — instead of the Razor class library default of
    /// <c>/_content/Keryhe.Telemetry.Ui/</c>, and answers <c>GET {BasePath}/config.json</c> with the
    /// configured <see cref="TelemetryUiOptions"/> — API location and branding alike — instead of
    /// the packaged dev-time defaults the SPA ships with.
    ///
    /// Requires <c>builder.Services.AddKeryheTelemetryUi(builder.Configuration)</c>, which is what
    /// binds those options from configuration; this method throws without it.
    ///
    /// Call before <c>UseKeryheTelemetryApi()</c> (or any other tenant-scoped middleware), the
    /// same ordering <c>UseDefaultFiles</c>/<c>UseStaticFiles</c> already required when this logic
    /// lived directly in each host's <c>Program.cs</c> — so UI asset requests, including
    /// <c>config.json</c>, never pay for tenant resolution. Call <c>MapControllers()</c> and
    /// <see cref="MapKeryheTelemetryUiFallback"/> afterwards to complete the pipeline.
    ///
    /// The host should also call <c>app.UseRouting()</c> explicitly, immediately after this method,
    /// rather than relying on <see cref="WebApplication"/>'s own implicit insertion of it. Left
    /// implicit, that insertion lands ahead of this method's middleware, so routing pre-selects an
    /// endpoint before any of it runs — and <c>UseStaticFiles</c>, finding an endpoint already
    /// selected, defers to it rather than serving. The shell itself is no longer exposed to that
    /// (it is written from memory below, by middleware that does not consult the selected
    /// endpoint), but every other asset this method serves still is, and the implicit insertion
    /// also silently relocates <c>UseCors</c> and the tenant middleware relative to routing.
    /// </summary>
    public static WebApplication UseKeryheTelemetryUi(
        this WebApplication app,
        Action<TelemetryUiOptions>? configure = null)
    {
        // Guarded on the shell, not on IOptions<TelemetryUiOptions>: the options open generic is
        // registered by the host builder regardless, so resolving it succeeds even with no
        // AddKeryheTelemetryUi call and silently hands back defaults. The singleton below exists
        // only if that call was made, which makes it the honest thing to test.
        var shell = app.Services.GetService<TelemetryUiShell>()
            ?? throw new InvalidOperationException(
                "UseKeryheTelemetryUi() requires the UI's services to be registered. Call " +
                "builder.Services.AddKeryheTelemetryUi(builder.Configuration) before builder.Build().");

        var options = app.Services.GetRequiredService<IOptions<TelemetryUiOptions>>().Value;
        configure?.Invoke(options);

        // Fixes the effective BasePath and builds the shell. Everything below — and
        // MapKeryheTelemetryUiFallback's route pattern — reads it back from the shell rather than
        // from the options, so the two cannot disagree about where the UI is mounted.
        shell.Initialize(options);

        //WarnIfAssemblyVersionMismatch(app.Logger);

        var basePath = new PathString(shell.BasePath);
        var uiFiles = TelemetryUiAssets.CreateFileProvider(app.Environment);

        // Answered as plain middleware, not a mapped endpoint: endpoint execution happens wherever
        // routing terminates the pipeline, which need not be where MapGet is called, while this
        // non-terminal middleware — like UseStaticFiles further below — runs in the registration
        // order this method's own call site puts it in. That is what lets "before the tenant
        // middleware" mean what it says.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.StartsWithSegments(basePath, out var rest) &&
                string.Equals(rest.Value, "/config.json", StringComparison.OrdinalIgnoreCase))
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

        // The SPA shell, written from memory with its <base href> rewritten to BasePath — see
        // TelemetryUiShell for why it cannot simply be served off the file provider.
        //
        // This replaces both UseDefaultFiles (which resolves a default file by listing the target
        // directory, something SubPathFileProvider did not do reliably through the composite
        // WebRootFileProvider) and the plain "/" -> "/index.html" rewrite that stood in for it.
        //
        // "{BasePath}/index.html" is matched here too, and that arm is not optional: left to
        // UseStaticFiles it would serve the packaged file with its original <base href="/">, and
        // every asset reference on the page would then resolve outside the prefix and 404. Handling
        // it here also means the negotiation middleware below never sees index.html, so it needs no
        // special case for the one file whose compressed variants must not be served.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(basePath, out var rest) &&
                IsShellPath(rest) &&
                await shell.TryServeAsync(context))
            {
                return;
            }

            await next(context);
        });

        UsePrecompressedAssetNegotiation(app, uiFiles, basePath);
        app.UseStaticFiles(new StaticFileOptions
        {
            // Strips BasePath before the file provider sees the path, which is what lets the same
            // re-rooted provider serve the bundle from any prefix.
            RequestPath = basePath,
            FileProvider = uiFiles,
            ContentTypeProvider = new PrecompressedContentTypeProvider(),
        });

        return app;
    }

    /// <summary>
    /// Serves the SPA shell for anything under <see cref="TelemetryUiOptions.BasePath"/> that was
    /// not matched by a concrete route, so client-side routes like <c>/traces/:id</c> survive a hard
    /// reload. Call after <c>MapControllers()</c> (and any gRPC service mappings), and after
    /// <see cref="UseKeryheTelemetryUi"/>, which is what settles the base path this maps under.
    ///
    /// Scoped to the base path rather than to the whole origin: a host that mounts the UI under a
    /// prefix has something else at "/", and a catch-all fallback would swallow it. At the default
    /// root base path the pattern is the same <c>/{*path:nonfile}</c> as before.
    ///
    /// Unlike the <c>MapFallbackToFile</c> this replaced, the shell is written from memory, so this
    /// route does negotiate a precompressed variant — the shell is built once at startup and held
    /// in all three representations, which is cheaper than the file-based negotiation that used to
    /// be skipped here as not worth it.
    /// </summary>
    public static IEndpointRouteBuilder MapKeryheTelemetryUiFallback(this IEndpointRouteBuilder endpoints)
    {
        var shell = endpoints.ServiceProvider.GetService<TelemetryUiShell>()
            ?? throw new InvalidOperationException(
                "MapKeryheTelemetryUiFallback() requires the UI's services to be registered. Call " +
                "builder.Services.AddKeryheTelemetryUi(builder.Configuration) before builder.Build().");

        // The route pattern below is built from the base path, so mapping the fallback before the
        // base path is settled would silently register the UI's deep links at the wrong prefix.
        if (!shell.IsInitialized)
        {
            throw new InvalidOperationException(
                "MapKeryheTelemetryUiFallback() must be called after UseKeryheTelemetryUi(), which " +
                "is what settles the UI's effective base path.");
        }

        var pattern = shell.BasePath.Length == 0
            ? "/{*path:nonfile}"
            : $"{shell.BasePath}/{{*path:nonfile}}";

        // MapFallback, not Map/MapGet: it applies Order = int.MaxValue, which is what keeps this
        // route losing to the api/* controllers and the gRPC services no matter how their patterns
        // compare. A plain Map would land at Order = 0 and compete on precedence alone.
        endpoints.MapFallback(pattern, async context =>
        {
            if (!await shell.TryServeAsync(context))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

        return endpoints;
    }

    /// <summary>"" , "/" or "/index.html" — the three ways to ask for the SPA shell itself.</summary>
    private static bool IsShellPath(PathString rest)
    {
        var value = rest.Value;
        return string.IsNullOrEmpty(value)
            || value == "/"
            || string.Equals(value, "/index.html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serves a <c>.br</c>/<c>.gz</c> sibling of the requested asset when the client accepts it,
    /// with the matching <c>Content-Encoding</c> — the compression itself is generated for free
    /// by the SDK's static web asset publish pipeline (see the csproj), but content-negotiated
    /// serving of the result is otherwise a feature of <c>MapStaticAssets()</c> alone, which maps
    /// a referenced Razor class library's assets at their <em>declared</em> path
    /// (<c>/_content/Keryhe.Telemetry.Ui/...</c>) with no supported way to re-root them the way
    /// <see cref="SubPathFileProvider"/> does for <c>UseStaticFiles</c>. This is the documented
    /// "serve pre-compressed files" pattern for that middleware instead, so re-rooting and
    /// compression are not a choice between one or the other.
    ///
    /// Note the asymmetry the base path forces: the file provider is probed with the
    /// <em>stripped</em> path, because it is rooted at this package's own assets, but
    /// <c>Request.Path</c> is rewritten to the <em>prefixed</em> one, because
    /// <c>StaticFileMiddleware</c> does its own <c>StartsWithSegments(RequestPath)</c> afterwards
    /// and would not find the asset otherwise.
    /// </summary>
    private static void UsePrecompressedAssetNegotiation(
        WebApplication app,
        IFileProvider uiFiles,
        PathString basePath)
    {
        app.Use(async (context, next) =>
        {
            var encoding = TelemetryUiAssets.PreferredCompressedEncoding(context.Request);
            if (encoding is not null &&
                HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.StartsWithSegments(basePath, out var rest))
            {
                var suffix = encoding == "br" ? ".br" : ".gz";
                var compressedPath = rest.Value + suffix;

                if (uiFiles.GetFileInfo(compressedPath).Exists)
                {
                    context.Request.Path = basePath.Add(new PathString(compressedPath));
                    context.Response.Headers.ContentEncoding = encoding;
                    context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
                }
            }

            await next(context);
        });
    }

    /// <summary>
    /// The UI's TypeScript models are hand-maintained mirrors of the API's C# DTOs with no shared
    /// contract generated between them — so a version-mismatched pair fails silently, as a field
    /// quietly missing from a rendered page, not as an exception. Looked up by assembly name via
    /// reflection, not a project reference: Keryhe.Telemetry.Ui must stay usable without ever
    /// depending on Keryhe.Telemetry.Api.
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
