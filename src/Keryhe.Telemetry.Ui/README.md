# Keryhe.Telemetry.Ui

The prebuilt Angular UI for [Keryhe Telemetry](https://github.com/keryhe/telemetry), packaged as
static web assets — the front-end equivalent of the `Keryhe.Telemetry.Api`/`Keryhe.Telemetry.Collector`
NuGet packages. A consumer building their own host on top of those packages gets the UI too,
without cloning `src/telemetry-client`, installing Node, or running `npm`.

## What it provides

- The compiled Angular bundle (`dist/telemetry-client/browser`), staged into this package's
  `wwwroot` at build time and shipped as static web assets — reachable via a plain
  `ProjectReference` or `PackageReference` with no further build step.
- `AddKeryheTelemetryUi(configuration, configure?)` — binds `TelemetryUiOptions` from the
  `TelemetryUi` configuration section, so where the UI is mounted, where its API is, and what it is
  called are all settable from `appsettings.json`, an environment variable or a command-line switch.
- `UseKeryheTelemetryUi(configure?)` — serves the bundle at `BasePath` (`/` by default) instead of
  the usual Razor class library location (`/_content/Keryhe.Telemetry.Ui/`), and answers
  `GET {BasePath}/config.json` with your host's configured API location and branding so the same
  bundle works regardless of where you mount either.
- `MapKeryheTelemetryUiFallback()` — serves `index.html` for any route the UI's client-side router
  owns (`/traces/:id`, `/metrics/:name`, ...), so a hard reload on a deep link still works.

## Usage

```csharp
// Required — UseKeryheTelemetryUi() throws without it.
builder.Services.AddKeryheTelemetryUi(builder.Configuration);

var app = builder.Build();

// Before UseKeryheTelemetryApi() (or any other tenant-scoped middleware) — asset requests,
// including config.json, should never pay for tenant resolution.
app.UseKeryheTelemetryUi();

// Explicit, not left to WebApplication's implicit insertion, which would land ahead of the
// middleware above and let routing pre-select an endpoint before it runs.
app.UseRouting();

app.UseKeryheTelemetryApi();
app.MapControllers();

// After MapControllers(), so /api/* is never swallowed by the SPA fallback.
app.MapKeryheTelemetryUiFallback();

app.Run();
```

## Configuration

Everything below binds from the `TelemetryUi` section, and can equally be set in code via the
`configure` delegate on either `AddKeryheTelemetryUi` or `UseKeryheTelemetryUi` (in that order of
precedence, configuration first).

```jsonc
{
  "TelemetryUi": {
    "BasePath": "/",                                 // where the UI is mounted
    "ApiBasePath": "/api",                           // where the browser should reach the API
    "BrandName": "Sentinel",                         // header bar + browser tab title
    "BrandTagline": "OpenTelemetry Visualization"    // header bar subtitle
  }
}
```

### Hosting the UI under a sub-path

Set `BasePath` to serve the UI beside another app rather than at the origin root:

```jsonc
{ "TelemetryUi": { "BasePath": "/telemetry" } }
```

The bundle's `<base href>` is rewritten to match at startup, which re-roots its asset references,
its own `config.json` fetch and its client-side router in one go — no rebuild, which is the point
of shipping it prebuilt. Three things to know:

- **Behind a reverse proxy, the prefix must be forwarded, not stripped.** `BasePath` is what the
  *browser* requests, and it is baked into the served HTML at startup, so it cannot be recovered
  per request from `PathBase`. In nginx terms that is `proxy_pass http://app;` (forwards
  `/telemetry/...`), not `proxy_pass http://app/;` (strips it).
- **It moves the UI only.** The API's controllers are routed at `api/*` on the origin root
  regardless, so `ApiBasePath` keeps its own default and is correct as-is if only the UI moved. Set
  it too if your deployment also relocates the API.
- **The origin root stops being served.** `GET /` returns 404, as does anything else outside the
  prefix — which is exactly what lets another app own `/`.

## Versioning

Ships in lockstep with `Keryhe.Telemetry.Api` — pin both to the same version. The UI's TypeScript
models are hand-maintained mirrors of the API's DTOs with no generated contract between them, so a
mismatched pair fails silently (a field goes missing from a rendered page) rather than with an
error. `UseKeryheTelemetryUi` logs a warning if it detects the two assembly versions differ.
