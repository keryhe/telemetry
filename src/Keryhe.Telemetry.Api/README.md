# Keryhe.Telemetry.Api

REST read API for [Keryhe Telemetry](https://github.com/keryhe/telemetry): tenant-scoped routes for traces, metrics, logs,
alert rules and retention settings, with optional authorization. Mount it in your own ASP.NET Core host.

## Usage

```csharp
builder.Services.AddAuthentication(/* your scheme: JwtBearer, OIDC + cookie, Windows, ... */);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("TelemetryRead",  p => p.RequireAuthenticatedUser());
    o.AddPolicy("TelemetryAdmin", p => p.RequireRole("telemetry-admin"));
});
builder.Services.AddKeryheTelemetryApi(builder.Configuration);
builder.Services.AddPostgreSqlApiServices(builder.Configuration);              // your telemetry provider's package
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);  // your control-plane provider's package

var app = builder.Build();
app.UseRouting();
app.UseCors(/* ... */);
app.UseAuthentication();
app.UseAuthorization();
app.MapKeryheTelemetryApi();   // in place of MapControllers(); do not call RequireAuthorization() on it
app.Run();
```

Routes are `{BasePath}/...`, tenant data under `{BasePath}/tenants/{tenantId}/...` (for example `GET /api/tenants/1/traces/list`).

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Telemetry:Api:Provider:Database` / `ConnectionStrings:Api` | (required) | The telemetry database |
| `Telemetry:Api:Provider:ControlPlane` / `ConnectionStrings:ControlPlane` | (required) | The database holding tenants, alert rules and retention settings |
| `Telemetry:Api:BasePath` | `/api` | Prefix of every route; `/` and empty are rejected |
| `Telemetry:Api:Authorization:Enabled` | `false` | Off: every tenant is readable |
| `Telemetry:Api:Authorization:Policies` | (default policy) | Operation → your policy name: `Read`, `Admin`, `Export`, `ManageAlerts`, `ManageSettings` |
| `Telemetry:Api:Authorization:TenantMappings` | none | Claim → tenant names or ids (`"*"` for all), e.g. `{ "ClaimType": "groups", "ClaimValue": "payments-team", "Tenants": ["payments"] }` |
| `Telemetry:Api:FixedTenantId` | none | A single-tenant install (the collector's `FixedTenantId`): the bundled UI hides its Tenant selector and every page is this tenant's; on SQLite the host creates the tenant. Not access control: the API still serves every tenant |

Query limits, export, alert evaluation and retention settings are in [CONFIGURATION.md](../../docs/CONFIGURATION.md#api).

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
