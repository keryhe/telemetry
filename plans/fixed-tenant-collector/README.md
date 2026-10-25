# Fixed-tenant collector (ingestion without API keys)

Status: **implemented 2026-10-25** (uncommitted); decisions confirmed as written. Where the build differs from, or adds to, what is below:

- `ApiKey` mode with a `FixedTenantId` set does not log a warning (registration has no logger); the docs say the key is ignored there.
- The startup check is covered against SQLite only (`FixedTenantCollectorTests`); the planned PostgreSQL case was not added. The relational
  path is the same type-name factory `RetentionWindowCache` already uses on ClickHouse/Parquet.
- `traces-api.service.spec.ts` now selects its tenant itself: it passed only when localStorage happened to hold `selectedTenantId`, which the
  new `TenantService` spec exposed. The two scaffold `App` specs in `app.spec.ts` still fail, as before this work (no `HttpClient` provider).
- Bootstrap order (follow-up, 2026-10-25): both hosts now create a missing SQLite control plane (and its directory) and migrate it, and the
  API host creates a missing Parquet root and `_schema.json` as the collector does, so either host starts first. On SQLite the fixed tenant is
  created as `Default` by both hosts (the collector from `FixedTenantId`, the API host from `TelemetryUi:FixedTenantId`); the relational
  control planes still need the Admin tool.
- Verified end to end on a scratch Parquet root + SQLite file: the fixed-tenant collector started, accepted a keyless OTLP/HTTP log and the
  UI showed it under the fixed tenant with no selector, redirected `/`, `/traces` and `/t/<other>/logs`, and showed "Tenant 9 is not set up"
  for a missing id; phone width had no horizontal scroll.

Goal: a single-customer, on-site install where the collector accepts OTLP exports **without an API key** and stores every
record under one configured tenant. The `tenants` row still exists (the API, the UI's tenant routes, retention windows and
alert rules all key on it); `api_keys` stays empty. Multi-tenant API-key authentication stays the default and is unchanged.

## Today

Every collector endpoint (gRPC and OTLP/HTTP) carries `RequireAuthorization(CollectorPolicy)`, and that policy requires a principal
authenticated by the `KeryheTelemetryApiKey` scheme that carries a `TenantId` claim (`TelemetryCollectorExtensions.cs`). Only
`ApiKeyAuthenticationHandler` issues that claim, after resolving a bearer key through `ITenantResolver` → `IApiKeyLookup` → `api_keys`.
The services read the tenant through `TenantClaims.GetRequiredTenantId` and nothing else. Nothing reads the `ApiKeyId` claim outside
the handler. So **the whole change can sit in authentication**: from the policy onward, the write path does not care where the claim
came from.

## Decisions (recommended)

| # | Recommendation | Reason |
|---|---|---|
| D1 | **Configuration.** `Telemetry:Collector:Authentication:Mode` = `ApiKey` (default) or `FixedTenant`; `Telemetry:Collector:Authentication:FixedTenantId` (a tenant id, required in `FixedTenant` mode, > 0) | One explicit switch, and an unset value keeps today's behavior. An id rather than a name: it is what the claim and every table carry, and the Admin tool and the UI both show it |
| D2 | **Same scheme name, different handler.** In `FixedTenant` mode `AddKeryheTelemetryCollector` registers `FixedTenantAuthenticationHandler` under `TelemetryAuthenticationSchemes.ApiKey` in place of `ApiKeyAuthenticationHandler`. The policy, `MapKeryheTelemetryCollector()`, the HTTP endpoints, `CollectorEndpointMetadata` and the services stay as they are | Keeping the scheme name keeps `CollectorPolicy` and `TelemetryApiStartupValidator`'s "ignore this scheme" rule correct with no edits. A second policy or an `AllowAnonymous` branch would split every mapping in two |
| D3 | **What the handler does.** On an endpoint with `CollectorEndpointMetadata`: `Success` with a principal carrying `TenantId = FixedTenantId` and no `ApiKeyId` claim. On any other endpoint: `NoResult`, as today. Build the principal once and reuse it | That's exactly what the services need. There's no lookup, so there's no failure path, no challenge and no `auth_failures` |
| D4 | **A presented `Authorization` header is ignored** (not validated, not logged per request) | One rule that's easy to explain: in this mode the network is the boundary. A "validate if present, else the fixed tenant" mode would let a misconfigured exporter's stale key fail exports that would otherwise succeed. If a hybrid is ever wanted, it becomes a third `Mode` value |
| D5 | **The tenant must exist at startup.** `FixedTenantStartupCheck` (an `IHostedLifecycleService`, `StartingAsync`, so before Kestrel binds, the same position as `PlaintextTransportGuard` and `SqliteControlPlaneStartupCheck`) runs `SELECT id FROM tenants WHERE id = @id` against the control plane and fails startup naming the id and the control-plane provider when there is no row. A control-plane error is retried for up to 30 s (on-site services can start before the database) and then fails startup | Writing telemetry for a tenant with no row would succeed silently, and the data would never appear in the UI's tenant list. Fail fast, as the SQLite check does |
| D6 | **How the check opens the control plane.** Extract the type-name connection factory from `RetentionWindowCache` (`ConnectionTypes`, `ConnectionStringFor`, including SQLite's `Mode=ReadOnly`) into an internal Core helper (`ControlPlaneDbConnection.Open(configuration)`), used by both | The collector side registers no tenant repository, and adding one per control-plane provider (four implementations) for a single `SELECT` isn't worth it. The type-name pattern is already there and tested, and the SQLite read-only rule comes along for free |
| D7 | **A safety switch outside Development.** `Telemetry:Collector:Authentication:AllowUnauthenticatedIngestion` must be `true` for `FixedTenant` mode outside Development, or startup fails (checked in the same startup check). In every environment, `FixedTenant` mode logs one `Warning` at startup: mode, tenant id, and that anyone who can reach the OTLP endpoints can write | Anyone who can reach the port can write data, so this must never happen by accident (for example a copied `appsettings.json`). It follows the `AllowInsecureTransport` precedent |
| D8 | **`PlaintextTransportGuard` is unchanged.** Plaintext outside Development still needs `AllowInsecureTransport=true` | There's no key to leak, but the telemetry itself still crosses the network in clear. One flag per risk keeps each choice explicit |
| D9 | **Tenant quota defaults off in this mode.** When `Mode=FixedTenant` and `Telemetry:Ingestion:TenantQuota:MaxShare` isn't set in configuration, `PostConfigure` sets it to 1. An explicit value is respected, and `RecordsPerSecond` is untouched | With a single tenant, the default 0.5 share would refuse exports while half the queue is free, which protects no other tenant |
| D10 | **Key machinery stays registered but idle.** `CachingTenantResolver`, `ApiKeyLookupCoordinator`, `AuthFailureLimiter`, `ApiKeyTouchTracker`/`ApiKeyTouchWorker` and the provider's `IApiKeyLookup`/`IApiKeyTouchStore` are still registered. Nothing calls them, and the touch worker finds nothing to write | Fewer conditional registrations, and the host's `Add<Provider>ControlPlaneCollectorServices` call stays the same in both modes. Note: these are cheap singletons, and the touch worker's loop does nothing |
| D11 | **Readiness.** `ControlPlaneHealth` is fed only by key lookups, so in this mode it never reports failing, and readiness reflects shutdown and queue saturation only. This is documented; no new probe | After startup the collector doesn't need the control plane for ingestion in this mode, except `RetentionWindowCache` on ClickHouse/Parquet, which already fails open. So a control-plane outage shouldn't take the collector out of rotation |
| D12 | **A tenant deleted after startup** isn't re-checked. Ingestion continues under that id, and retention expires the data on `RetentionDefaults` (orphan data already expires) | A periodic re-check would add a failure mode, and readiness questions with it, for an operator action that, on a single-customer site, means they're decommissioning anyway |
| D13 | **The control plane is still required** (`ControlPlane:Provider`, `ConnectionStrings:ControlPlane`) on both hosts | The API host still needs it for tenants, alert rules and retention, and the collector needs it for D5 and `RetentionWindowCache`. Parquet plus `ControlPlane:Provider=Sqlite` still gives an install with no database server |
| D14 | **The UI has a fixed-tenant mode too: `TelemetryUi:FixedTenantId`** on the API host, set to the same id as the collector's `FixedTenantId`. When it is set, `config.json` carries `fixedTenantId`, the header's Tenant dropdown is hidden, and every page is that tenant's | The collector and the API are separate processes with separate configuration, so the UI can't learn the collector's mode. A `TelemetryUi` key matches how the UI is already configured (branding, thresholds, auth), and a consumer can also use it with an API-key collector |
| D15 | **"Selected" means routing goes through the fixed tenant, not the dropdown.** In fixed mode, `TenantService.effectiveTenantId` and `defaultTenantId()` return `fixedTenantId` (no last-used lookup, no waiting for the tenant list), and `tenantRouteGuard` redirects `/t/<any other id>/<rest>` to `/t/<fixedTenantId>/<rest>` with the query and fragment kept. So `/`, the old tenant-less paths and stale bookmarks all land on the fixed tenant, and `/no-tenants` is never reached | A hidden dropdown alone would leave whatever tenant was last used in localStorage, or the first in the list. Putting the rule in the service and the guard covers every entry point |
| D16 | **A missing tenant is reported, not hidden.** When the tenant list has loaded and `fixedTenantId` isn't in it (no row, or no access), the shell shows its empty state with "Tenant N is not set up" and a hint to create it with the Admin tool, in place of "Choose another tenant from the Tenant selector above" (there's no selector to choose from). The settings page title keeps showing the tenant's name | Without this, a mistyped id would give empty pages with no explanation |
| D17 | **UI convenience, not access control.** The API still serves `/tenants/{id}/...` for any tenant that exists, and the API host doesn't check `TelemetryUi:FixedTenantId` against the control plane at startup (with SQLite, the API host creates the file before the tenant can exist). Restricting the API to one tenant is what `Telemetry:Api:Authorization` + `TenantMappings` is for | It keeps the API's behavior and the startup order unchanged. D16 covers the misconfiguration case visibly |

Out of scope: the API's routes and authorization (unchanged, D17), the Admin tool (it already creates a tenant without a key), multiple
fixed tenants (for example by port or by header), and the test data generator and stress harness (see "Follow-ups").

## Changes

### `Keryhe.Telemetry.Collector`

1. `Authentication/CollectorAuthenticationOptions.cs` (new): `Mode` (enum `CollectorAuthenticationMode { ApiKey, FixedTenant }`),
   `FixedTenantId`, `AllowUnauthenticatedIngestion`, plus `Validate()` (in `FixedTenant` mode the id must be > 0; in `ApiKey` mode a
   `FixedTenantId` that is set logs a warning that it is ignored). Hung off `TelemetryCollectorOptions` as `Authentication`, so it binds from
   `Telemetry:Collector:Authentication`, and validated in the existing `.Validate(...)` call.
2. `Authentication/FixedTenantAuthenticationHandler.cs` (new): D3. Same options type pattern as `ApiKeyAuthenticationHandler` (an empty
   `AuthenticationSchemeOptions` subclass), with the tenant id from `IOptions<TelemetryCollectorOptions>`.
3. `Authentication/FixedTenantStartupCheck.cs` (new): D5 + D7 + the startup warning. It registers only in `FixedTenant` mode.
4. `TelemetryCollectorExtensions.cs`: read the mode from configuration at registration time, the way the provider switch does, then:
   - choose the handler for the scheme (D2);
   - register the startup check;
   - add the `PostConfigure<TenantQuotaOptions>` (D9).

   Update the authentication comment block.

### `Keryhe.Telemetry.Core`

5. `Data/ControlPlaneDbConnection.cs` (new, internal, `InternalsVisibleTo` the collector if needed): the factory extracted from
   `RetentionWindowCache` (D6). `RetentionWindowCache` switches to it with no change in behavior.

### Hosts

6. `Keryhe.Telemetry.Collector.Server`: no code change. `Program.cs` already calls `AddKeryheTelemetryCollector` and the two provider
   switches. Correct the indentation and comment of the `UseAuthentication()`/`UseAuthorization()` lines ("API key or fixed-tenant
   authentication").

### `Keryhe.Telemetry.Ui` (D14)

7. `TelemetryUiOptions`: `long? FixedTenantId`; `Validate()` fails startup when it is set and not > 0.
8. `TelemetryUiApplicationBuilderExtensions`: `config["fixedTenantId"]` only when it is set, like `healthThresholds` and `auth`.

### `src/telemetry-client` (D15, D16)

9. `core/config/app-config.ts` / `load-config.ts`: `fixedTenantId?: number` on `AppConfig` (not in `DEFAULT_APP_CONFIG`); `normalize`
   keeps it only when it is a positive integer, and otherwise warns and drops it.
10. `core/services/tenant.service.ts`:
    - inject `APP_CONFIG` and expose `fixedTenantId` (readonly, `number | null`) and `isFixed`;
    - `effectiveTenantId` and `defaultTenantId()` return it first;
    - `noAccess` is unchanged (true when the route tenant is missing from the loaded list), so the missing-tenant case of D16 comes from it.
11. `core/guards/tenant-route.guards.ts`: in `tenantRouteGuard`, a valid id other than the fixed one returns a `UrlTree` to the same path
    under the fixed id (query and fragment kept). `legacyTenantRedirect` needs no change, since it goes through `defaultTenantId()`.
12. `layout/shell/shell.component.html`: the Tenant `mat-form-field` renders only when `!tenantService.isFixed()`. In fixed mode the
    `noAccess` branch shows the D16 message and hint. Check that `.toolbar-controls` still wraps cleanly at phone width with one control fewer.

## Tests

All without Docker except where noted.

- `CollectorAuth` suite (`tests/Keryhe.Telemetry.IntegrationTests/CollectorAuth`, `CollectorHost` gains a mode option):
  - fixed mode, gRPC and OTLP/HTTP, no `Authorization` header → accepted, and the records in the ingestion channel carry `FixedTenantId`;
  - fixed mode with a bearer header of any value (valid, invalid, malformed) → accepted under `FixedTenantId` (D4), and `IApiKeyLookup` is
    never called (the fake counts calls);
  - fixed mode, a non-collector endpoint → the handler returns `NoResult` (as in the existing test for the key handler);
  - `ApiKey` mode is unchanged (the existing tests are the negative control: run the "no header → UNAUTHENTICATED" test in both modes).
- `TransportAndValidatorTests`:
  - `FixedTenant` outside Development without `AllowUnauthenticatedIngestion` → startup fails, naming the key; with it → starts; in
    Development → starts without it;
  - `FixedTenantId` 0 or missing → options validation fails.
- `FixedTenantStartupCheckTests` (`Provider=Sqlite`, the temp SQLite control-plane file):
  - tenant present → passes;
  - tenant missing → fails naming the id;
  - file missing → fails, and does not create the file.

  One PostgreSQL case on the existing container (Testcontainers) covers the relational type-name path.
- `TenantQuotaOptions`: fixed mode with no `MaxShare` configured → 1; with `MaxShare=0.3` configured → 0.3.
- `RetentionWindowCache` tests stay green after the extraction (the refactor's guard).
- `ApiHttpTests` (`Suite=ApiHttp`):
  - `config.json` has `fixedTenantId` when `TelemetryUi:FixedTenantId` is set and leaves the key out when it isn't;
  - `FixedTenantId=0` fails startup.
- Angular specs (`npm test`; the first meaningful ones in the project, kept to these two units):
  - `TenantService`: with `fixedTenantId`, `effectiveTenantId`/`defaultTenantId()` return it regardless of localStorage and the list
    order; `noAccess` is true when the loaded list lacks it;
  - `tenantRouteGuard`: `/t/7/traces?range=1h` with fixed tenant 3 → `/t/3/traces?range=1h`, `/t/3/...` → `true`, and with no fixed
    tenant the behavior is unchanged.
- Browser check (Development, API host with `TelemetryUi:FixedTenantId` set, two tenants in the control plane, localStorage holding the
  other one):
  - the header has no Tenant dropdown;
  - `/telemetry`, `/telemetry/traces` and `/telemetry/t/<other>/logs` all land on the fixed tenant;
  - a missing id shows the D16 message;
  - phone width still lays out.

## Docs

- `docs/CONFIGURATION.md`: the three `Telemetry:Collector:Authentication:*` keys and the effect on the `TenantQuota:MaxShare` default.
- `src/Keryhe.Telemetry.Collector/README.md`: a "Single-tenant install (no API keys)" section:
  - create the tenant (Admin tool → create tenant; or SQL; with SQLite, start the API host once first so the file exists);
  - configure the collector;
  - the network is now the boundary (bind to an internal interface, firewall the OTLP ports);
  - readiness behavior (D11) and the deleted-tenant behavior (D12);
  - how to switch back to keys later (create keys, roll them out to exporters, set `Mode=ApiKey`).
- `docs/CONFIGURATION.md` also gets `TelemetryUi:FixedTenantId`, and `src/Keryhe.Telemetry.Ui/README.md` its option, noting that it hides
  the selector but does not restrict the API (D17). The Collector README's single-tenant section tells the operator to set it to the
  same id on the API host.
- `docs/SETUP.md`: point to that section from the install flow.
- `CLAUDE.md` "Collector authentication": a bullet for the mode (same scheme name, startup check, safety switch, quota default); and in
  "API base path, routes and authorization" → "The UI", one line on `fixedTenantId` (selector hidden, guard redirect, not access control).

## Follow-ups (not part of this plan)

- Test data generator: allow an empty `Generator:Tenants:N:ApiKey` (no `Authorization` header sent) so it can drive a fixed-tenant collector.
  Today `Validate()` requires a key; a dummy value works in the meantime, because the header is ignored (D4).
- Stress harness: a `--collector-auth fixed` option, if the auth path's cost is ever worth measuring (in this mode it's close to zero).

## Done when

- A collector with `Mode=FixedTenant`, `FixedTenantId=<id>` and an empty `api_keys` accepts gRPC and OTLP/HTTP exports with no header, and
  the API/UI shows the data under that tenant.
- With `TelemetryUi:FixedTenantId` set to the same id, the UI has no Tenant dropdown and every URL resolves to that tenant.
- It refuses to start when the tenant row is missing, or when it runs outside Development without `AllowUnauthenticatedIngestion`.
- Every new test and the existing `CollectorAuth`/`ApiHttp`/`Provider=Sqlite` suites pass, and the docs above are updated.
