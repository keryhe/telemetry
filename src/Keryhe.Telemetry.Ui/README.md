# Keryhe.Telemetry.Ui

The prebuilt Angular UI for [Keryhe Telemetry](https://github.com/keryhe/telemetry), packaged as
static web assets — the front-end equivalent of the `Keryhe.Telemetry.Api`/`Keryhe.Telemetry.Collector`
NuGet packages. A consumer building their own host on top of those packages gets the UI too,
without cloning `src/telemetry-client`, installing Node, or running `npm`.

## What it provides

- The compiled Angular bundle (`dist/telemetry-client/browser`), staged into this package's
  `wwwroot` at build time and shipped as static web assets — reachable via a plain
  `ProjectReference` or `PackageReference` with no further build step.
- `UseKeryheTelemetryUi(configure?)` — serves the bundle at the origin root (`/`) instead of the
  usual Razor class library location (`/_content/Keryhe.Telemetry.Ui/`), and answers
  `GET /config.json` with your host's configured API location so the same bundle works regardless
  of where you mount the API.
- `MapKeryheTelemetryUiFallback()` — serves `index.html` for any route the UI's client-side router
  owns (`/traces/:id`, `/metrics/:name`, ...), so a hard reload on a deep link still works.

## Usage

```csharp
var app = builder.Build();

// Before UseKeryheTelemetryApi() (or any other tenant-scoped middleware) — asset requests,
// including /config.json, should never pay for tenant resolution.
app.UseKeryheTelemetryUi(options =>
{
    // Only needed if the API is not served at the conventional same-origin "/api".
    options.ApiBasePath = "/api";
});

app.UseKeryheTelemetryApi();
app.MapControllers();

// After MapControllers(), so /api/* is never swallowed by the SPA fallback.
app.MapKeryheTelemetryUiFallback();

app.Run();
```

## Versioning

Ships in lockstep with `Keryhe.Telemetry.Api` — pin both to the same version. The UI's TypeScript
models are hand-maintained mirrors of the API's DTOs with no generated contract between them, so a
mismatched pair fails silently (a field goes missing from a rendered page) rather than with an
error. `UseKeryheTelemetryUi` logs a warning if it detects the two assembly versions differ.
