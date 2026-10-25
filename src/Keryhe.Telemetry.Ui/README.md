# Keryhe.Telemetry.Ui

The prebuilt Angular UI for [Keryhe Telemetry](https://github.com/keryhe/telemetry), packaged as static web assets. Reference it
from the API host to serve the UI; no Node or `npm` needed.

## Usage

```csharp
builder.Services.AddKeryheTelemetryUi(builder.Configuration);   // required

var app = builder.Build();
app.UseKeryheTelemetryUi();          // before authentication: assets and config.json are public
app.UseRouting();                    // call it explicitly, right after UseKeryheTelemetryUi
app.UseAuthentication();
app.UseAuthorization();
app.MapKeryheTelemetryApi();
app.MapKeryheTelemetryUiFallback();  // after the API, so deep links reload
app.Run();
```

Pin `Keryhe.Telemetry.Ui` and `Keryhe.Telemetry.Api` to the same version.

## Configuration (`TelemetryUi`)

| Key | Default | Meaning |
|---|---|---|
| `BasePath` | `/` | Where the UI is served, e.g. `/telemetry`. Behind a proxy, forward the prefix (don't strip it) |
| `ApiBasePath` | `Telemetry:Api:BasePath` | Where the browser reaches the API (an absolute URL for another origin) |
| `BrandName` / `BrandTagline` | `Sentinel` / `OpenTelemetry Visualization` | Header text and tab title |
| `HealthThresholds:ErrorRate:Warn` / `Critical` | `0.01` / `0.05` | Dashboard error-rate colouring (0–1) |
| `FixedTenantId` | none | A single-tenant install: hides the header's Tenant selector and makes every page this tenant's (other tenants' URLs redirect to it). Not access control: the API still serves every tenant |
| `Auth:Mode` | `cookie` | `cookie` (the host signs users in) or `oidc` (the SPA signs in with PKCE) |
| `Auth:LoginUrl` / `Auth:LogoutUrl` | none | Cookie mode: where a 401 sends the user, and the Sign out target |
| `Auth:IncludeCredentials` | `false` | Send cookies on cross-origin API calls |
| `Auth:Oidc:Authority` / `ClientId` / `Scope` | — / — / `openid profile email` | OIDC mode (redirect URI `{origin}{BasePath}/callback`) |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
