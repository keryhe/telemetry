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

// Before authentication — asset requests, including config.json, are public.
app.UseKeryheTelemetryUi();

// Explicit, not left to WebApplication's implicit insertion, which would land ahead of the
// middleware above and let routing pre-select an endpoint before it runs.
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.MapKeryheTelemetryApi();

// After the API, so {BasePath}/api/* is never swallowed by the SPA fallback.
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
    "ApiBasePath": "/api",                           // where the browser should reach the API (default: Telemetry:Api:BasePath, else /api)
    "BrandName": "Sentinel",                         // header bar + browser tab title
    "BrandTagline": "OpenTelemetry Visualization"    // header bar subtitle
  }
}
```

### Health thresholds

The Dashboard colours its Error Rate card from a warn and a critical error-rate threshold. The
defaults are built into the bundle; set either under `TelemetryUi:HealthThresholds:ErrorRate` to
retune a deployment without rebuilding. Both keys are optional, and a partial override merges field
by field. Rates are 0-1 fractions. The setting applies to every tenant.

```jsonc
{
  "TelemetryUi": {
    "HealthThresholds": {
      "ErrorRate": { "Warn": 0.01, "Critical": 0.05 }  // warn / error colouring
    }
  }
}
```

The values shown are the defaults. A value out of range, or a `Warn` that is not below its
`Critical`, fails the host at startup naming the key. Only the keys you set are sent to the browser
in `config.json`, so the defaults live in one place.

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
- **It moves the UI only.** The API's location is `Telemetry:Api:BasePath` (default `/api`), and
  `ApiBasePath` follows it unless you set `TelemetryUi:ApiBasePath` explicitly (for example to an
  absolute URL for an API on another origin).
- **The origin root stops being served.** `GET /` returns 404, as does anything else outside the
  prefix — which is exactly what lets another app own `/`.

### URLs

Tenant pages live under `/t/{tenantId}/` (`/t/3/traces`, `/t/3/traces/{id}`, `/t/3/metrics/{name}`,
`/t/3/logs`, `/t/3/alerts`, `/t/3/dashboard`), so a copied link carries its tenant; `/settings` is
global. The tenant-less paths of earlier versions (`/traces?range=1h`) redirect to the same path and
query under the last-used tenant, else the first one the caller may access. A tenant the caller
cannot use shows "No access to this tenant".

### Authentication

How the UI authenticates to the API is chosen by `TelemetryUi:Auth:Mode`:

```jsonc
{
  "TelemetryUi": {
    "Auth": {
      "Mode": "cookie",            // "cookie" (default) or "oidc"
      "LoginUrl": "/account/login", // cookie: where a 401 sends the user (?returnUrl=...)
      "LogoutUrl": "/account/logout", // cookie: the header's "Sign out" target
      "IncludeCredentials": false,  // send cookies on cross-origin API calls
      "Oidc": { "Authority": "", "ClientId": "", "Scope": "openid profile api://telemetry/read" }
    }
  }
}
```

- **`cookie` (recommended).** The host owns sign-in (OIDC + cookie, Windows, ...); requests carry its
  cookie and the SPA holds no tokens. With no `LoginUrl`, a 401 shows "You are not signed in".
- **`oidc`.** For bearer-only deployments with no server-side session. The SPA signs in itself with
  authorization code + PKCE and refresh-token renewal (no iframe) against any OIDC provider (Entra,
  Auth0, Keycloak, Okta), and sends the access token only to requests under the API URL. `Authority`
  must be absolute https and `ClientId` is required, or startup fails. Register the app with the
  provider as a **single-page application** (public client, PKCE) with the redirect URI
  `{origin}{BasePath}/callback` (for `BasePath` `/` that is `https://host/callback`), and the post-logout
  redirect `{origin}{BasePath}/`. The sign-in library is loaded only in this mode.

In both modes the API's authorization settings are described in the `Keryhe.Telemetry.Api` README, and
the UI sends `X-Telemetry-Client: 1` on every API call.

## Versioning

Ships in lockstep with `Keryhe.Telemetry.Api` — pin both to the same version. The UI's TypeScript
models are hand-maintained mirrors of the API's DTOs with no generated contract between them, so a
mismatched pair fails silently (a field goes missing from a rendered page) rather than with an
error. `UseKeryheTelemetryUi` logs a warning if it detects the two assembly versions differ.
