# Keryhe.Telemetry.Api

REST read API for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — controllers,
tenant-scoped routing and authorization, and provider-agnostic wiring for querying ingested OpenTelemetry
traces, metrics, logs, and alert rules.

## What it provides

- `AddKeryheTelemetryApi(configuration, configure?)` — registers the telemetry controllers (as an MVC
  application part, since they live in this library), the scoped tenant context
  (`ApiTenantContext` / `ITenantContext`), the base-path routing convention and the authorization
  services. It does not register a database provider — the host does that separately (see Usage below).
- `MapKeryheTelemetryApi()` — maps controllers and answers any other path under the API base path
  with a JSON `404`, so an unknown API path never falls through to the UI's SPA shell.

The host application still owns CORS, Swagger/OpenAPI, HTTPS redirection, and authentication.

## Usage

```csharp
builder.Services.AddAuthentication(/* JwtBearer, OIDC + cookie, Windows, ... — the host's choice */);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("TelemetryRead",   p => p.RequireAuthenticatedUser());
    o.AddPolicy("TelemetryAdmin",  p => p.RequireRole("telemetry-admin"));
    o.AddPolicy("TelemetryExport", p => p.RequireClaim("scp", "telemetry.export"));
});
builder.Services.AddKeryheTelemetryApi(builder.Configuration);

// Install the NuGet package matching your chosen provider alongside this one, e.g.
// Keryhe.Telemetry.PostgreSQL, and call its own Add<Provider>ApiServices:
builder.Services.AddPostgreSqlApiServices(builder.Configuration);

var app = builder.Build();
app.UseRouting();
app.UseCors(/* ... */);
app.UseAuthentication();
app.UseAuthorization();
app.MapKeryheTelemetryApi();   // not RequireAuthorization(): see "Authorization"
app.Run();
```

Configuration:

- `ConnectionStrings:Api` — connection string for the telemetry data (read by the provider's own
  `Add<Provider>ApiServices` call, not by `AddKeryheTelemetryApi`).
- `ControlPlane:Provider` and `ConnectionStrings:ControlPlane` — the control-plane database for alert rules, the tenant
  catalog and each tenant's retention windows (read by `Add<Provider>ControlPlaneApiServices`, which throws naming the key when the
  connection string is missing).
- `Telemetry:Api` — the section below.

## Base path and routes

`Telemetry:Api:BasePath` (default `/api`) is the prefix of every route, e.g. `/telemetry/api`.
It must be one or more segments of unreserved URL characters; `/` and empty are rejected, because
the API's segments (`/traces`, `/logs`) are also UI routes. It applies to this library's controllers
only, never to the host's own. The bundled UI follows it unless `TelemetryUi:ApiBasePath` is set.

| Route | Notes |
|---|---|
| `GET {base}/capabilities` | global |
| `GET {base}/tenants` | the tenants the caller may access |
| `GET {base}/me` | global; anonymous-allowed (see below) |
| `GET`/`PUT {base}/tenants/{tenantId}/settings/retention` | the tenant's retention windows; Read / ManageSettings; 404 for a tenant with no row |
| `GET {base}/tenants/{tenantId}/traces/summary` | also `logs`, `metrics`, `resources`, `alerts` |
| `POST {base}/tenants/{tenantId}/alerts/rules` | |

A tenant id that is not a positive number, and any other unknown path under the base, is a JSON `404`.

**`GET {base}/me`** answers who the caller is, for the UI's user menu in cookie mode. It carries `[TelemetryAnonymous]` (instead of
`[TelemetryOperation]`; every action has exactly one of the two): `TelemetryAuthorizationFilter` skips it, so it never challenges. It
is always `200`, `Cache-Control: no-store`, with `{ signedIn, name?, givenName?, email?, picture? }` and `signedIn: false` when nobody
is signed in. When the Read operation's policy names `AuthenticationSchemes` the caller is authenticated through them, otherwise by
the host's default scheme. Claims are read with a fixed fallback list (no configuration): `name`, `ClaimTypes.Name`,
`Identity.Name`; `given_name`, `ClaimTypes.GivenName`; `email`, `ClaimTypes.Email`, `preferred_username` (only if it contains `@`);
`picture` (only an absolute `http`/`https` URL).

## Summary endpoints

The cards and charts of the dashboard, trace list and logs page read **per-minute rollups** the collector writes,
not the raw rows:

- `GET {base}/tenants/{id}/traces/summary?start&end&service&bucketCount` returns
  `{ bucketSeconds, writtenThrough, summary, buckets, services, latency, timedOut }`: **requests** (inbound spans,
  kind `SERVER`/`CONSUMER`), not traces, with approximate percentiles. A request through three services counts three
  times. `latency` is the time x duration-band grid (`band` 0-23, 23 open-ended).
- `GET {base}/tenants/{id}/logs/summary?start&end&service&minSeverity&bucketCount` returns
  `{ bucketSeconds, writtenThrough, total, buckets, timedOut }`.

The server picks `bucketSeconds` from a fixed ladder (1, 2, 5, 10, 15, 30 min, 1, 2, 3, 6, 12 h, 1 d), rounds the
window to whole minutes and leaves out the minutes after `writtenThrough`, which the rollup has not been written for
yet. The cards and charts describe the time range and service only (and, for logs, the minimum severity); the lists
below them have no exact total.

## Capped lists

The trace list, logs list, metrics catalog and a metric's exemplars are capped rather than paged. Each returns at most
`limit` rows (default and ceiling `Telemetry:Query:Limits`) as `{ items, truncated }`, where `truncated` is true when
more rows matched. To see others, narrow the time range or filters, or ask for the other end of the window with `order`.
Export streams everything.

| Route | Returns |
|---|---|
| `GET .../logs/list?start&end&service&minSeverity&q&order=newest\|oldest&limit` | `{ items, truncated }` |
| `GET .../traces/list?start&end&mode&service&operation&minDurationMs&maxDurationMs&q&order=newest\|oldest&limit` | `{ items, truncated }` |
| `GET .../metrics/catalog?...&limit` | `{ items, names, truncated }` |
| `GET .../metrics/exemplars?...&limit` | `{ name, type, exemplars, truncated }`, newest first |
| `GET {base}/capabilities` | the limits: `logListLimit`, `traceListLimit`, `metricCatalogLimit`, `exemplarLimit` |

`limit` is clamped to 1 through the configured limit, and `order` other than `newest` or `oldest` is a `400`. A list is
one request over the data as it is at that moment; it refreshes when the time range is re-applied.

## Authorization

Off by default (`Telemetry:Api:Authorization:Enabled`): every tenant is readable and a startup warning
says so outside Development. When enabled, every library action is checked through ASP.NET Core's
`IAuthorizationService`:

- **An operation requirement.** Each action declares one operation: `Read`, `Export` (the three
  `*/export` routes), `ManageAlerts` (alert rule create/update/delete) or `ManageSettings` (retention
  update). They map to policies *you* register, by name:

  ```json
  "Telemetry": { "Api": { "Authorization": {
    "Enabled": true,
    "Policies": { "Read": "TelemetryRead", "Admin": "TelemetryAdmin", "Export": "TelemetryExport" }
  } } }
  ```

  Unset entries fall back: `Export` → `Read`; `ManageAlerts` and `ManageSettings` → `Admin`;
  `Read` and `Admin` → the host's default policy (a startup warning says so when `Admin` is unset,
  because every signed-in user could then change settings). Set `ManageAlerts`/`ManageSettings` to
  split them.
- **A tenant requirement** on tenant routes. The built-in handler grants from claims:

  ```json
  "TenantMappings": [
    { "ClaimType": "groups", "ClaimValue": "payments-team", "Tenants": [ "payments", "checkout" ] },
    { "ClaimType": "role", "ClaimValue": "telemetry-operator", "Tenants": [ "*" ] }
  ]
  ```

  `Tenants` holds names or ids; `"*"` is every tenant (what such a user may *change* is still governed
  by the operation policies). An unknown name is logged and matches nothing. Without mappings the
  built-in handler grants nothing, so configure mappings or write a handler.

An unauthenticated failure is `401` (challenge), an authenticated one `403`. Startup fails, naming the
problem, when authorization is enabled but a configured policy is not registered, no authentication
scheme exists, a mapping is malformed, or nothing can grant tenant access.

**Custom rules.** Register an ordinary handler; anything ASP.NET authorization can express works
(roles, scopes, a database lookup):

```csharp
builder.Services.AddScoped<IAuthorizationHandler, MyTenantAccessHandler>();

class MyTenantAccessHandler : AuthorizationHandler<TenantAccessRequirement, TelemetryResource>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context,
        TenantAccessRequirement requirement, TelemetryResource resource)
    {
        if (/* may context.User see resource.TenantId? */) context.Succeed(requirement);
        // context.Fail() narrows access a mapping or another handler granted.
        return Task.CompletedTask;
    }
}
```

**Securing the host's own endpoints is separate.** `MapKeryheTelemetryApi()` returns the shared
controller endpoint builder, so calling `RequireAuthorization()` on it would also lock the host's
controllers; the library authorizes its own actions instead.

**Sign-in behind a proxy** (oauth2-proxy, Azure Easy Auth): register an authentication handler that
turns the proxy's headers into a principal. The library does not ship one.

**CSRF.** With authorization enabled, a `POST`/`PUT`/`DELETE` that carries no bearer token
must send `X-Telemetry-Client: 1`, or it is a `400`. A cross-site form cannot set it; a bearer token
cannot be attached by a browser on its own, so such requests are exempt. Basic and Negotiate/NTLM
credentials do not exempt a request, because the browser does attach those by itself. The bundled UI
sends the header.

**Telling refusals apart.** A `403` from the tenant check carries `X-Telemetry-Denied: tenant`; a `403`
for an operation the caller may not perform does not. The bundled UI uses it to show "no access to this
tenant" only for the former.

**Policies that name schemes.** A policy with `AddAuthenticationSchemes(...)` is evaluated against the
principal those schemes produce, and its challenge/forbid go through them, as with `[Authorize]`.

**Cross-origin cookies.** A UI on another origin than the API needs `TelemetryUi:Auth:IncludeCredentials`
plus `AllowCredentials()` with explicit origins in the host's CORS policy, and a `SameSite=None; Secure`
cookie. In any cross-origin setup, expose the refusal header: `WithExposedHeaders("X-Telemetry-Denied")`. The in-repo hosts do not set this up.

The in-repo `Api.Server` host is a development example: it registers no
authentication scheme and runs with authorization disabled.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture,
multi-tenancy model, and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
