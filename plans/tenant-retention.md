# Per-tenant retention

Written 2026-10-11. Retention windows move from the single global `retention_settings` row to each tenant. There is no
global default: a new tenant gets 30/30/30 days (traces/logs/metrics) from the schema, and is edited on its own settings
page. Telemetry whose tenant has no control-plane row expires on a built-in 30/30/30.

## Decisions

| # | Decision |
|---|---|
| 1 | The windows are columns on `tenants`: `trace_retention_days`, `log_retention_days`, `metric_retention_days` (`NOT NULL DEFAULT 30`) and `retention_updated_at` (`NOT NULL DEFAULT` now). `retention_settings` is dropped. Every tenant always has values, so there is no missing-row case |
| 2 | Defaults change from 90/90/180 to 30/30/30, both the column defaults and the built-in value |
| 3 | Telemetry for a `tenant_id` with no `tenants` row (deleted tenant, data written before the row existed, a different control-plane database) expires on the built-in `RetentionDefaults` (30/30/30, a constant in Core). A test keeps the constant and the three schema defaults equal |
| 4 | Both schema components go to **4.0.1**: the control plane (decision 1) and the telemetry scripts (decision 9). Each bumps only its own third digit, in one commit with `apply-schema.sh`'s `CONTROLPLANE_TARGET_VERSION` and `TELEMETRY_TARGET_VERSION`. Scripts stay fresh-install (no migration from 4.0.0) |
| 5 | Retention is edited on a **tenant-scoped** settings page only. The Admin tool does not change (a created tenant takes the column defaults). No bulk "apply to all tenants" |
| 6 | API: `GET`/`PUT {base}/tenants/{tenantId}/settings/retention` (Read / ManageSettings, plus tenant access). The global `{base}/settings/retention` is removed (breaking; an old client gets the JSON 404). A tenant id with no `tenants` row is a 404 |
| 7 | ClickHouse: `DROP PARTITION` for days past the **longest** window in effect; tenants with a shorter window are pruned with a lightweight `DELETE`, one statement per distinct window (tenants grouped), restricted to the days between their cutoff and the drop boundary |
| 8 | ClickHouse and Parquet keep the ingest-time drop of records already past retention, now per tenant (unknown tenant = built-in) |
| 9 | Relational metric retention becomes per tenant through `metric_id` (the data-point tables have no `tenant_id`), served by the existing `(metric_id, time_unix_nano, id)` index. The time-only retention index on the five data-point tables (PostgreSQL BRIN, SQL Server/MySQL `(time_unix_nano)`) is **dropped** in telemetry 4.0.1 |
| 10 | No confirmation in the UI when a window is lowered. A shortened window deletes on the next sweep (within `Telemetry:Retention:IntervalSeconds`, 1 h) |
| 11 | The theme moves off the settings page into a new **global settings menu**: a gear button in the header, right of the time picker, that drops down a menu. The theme is a Light / Dark / System select inside it. The menu is where future global settings go |
| 12 | Global settings stay **per browser** (localStorage, as the theme is today): no API or schema change |
| 13 | A **user button** sits right of the gear whenever someone is signed in (OIDC or cookie mode): the user's picture, else the initial of their first name, else a generic person icon. It opens a user menu: avatar, name and email, then Sign out. The tooltip is the name (the email when there is no name) |
| 14 | Sign out lives **only** in the user menu. The header's Sign out button goes, and the gear menu holds global settings only. Nobody signed in = no user button and no Sign out (even in cookie mode with a `logoutUrl`) |
| 15 | User details: OIDC reads the client-side claims (`angular-auth-oidc-client`'s user data); cookie mode calls a new `GET {base}/me`, which reads `HttpContext.User` with a fixed claim fallback list (no configuration) |
| 16 | `/me` is anonymous-allowed: a new marker that `TelemetryAuthorizationFilter` skips. It always answers 200, `{signedIn: false}` when nobody is signed in, so the auth interceptor never redirects to sign in because of it. It returns only the caller's own claims |
| 17 | The default OIDC scope becomes `openid profile email` |
| 18 | Out of scope: the Microsoft Entra ID photo (it lives behind Microsoft Graph); Entra users get their initial |
| 19 | The left nav item keeps its label "Settings" and gear icon; it now opens the current tenant's settings page (retention only) |

What stays the same:
- `RetentionWorker`'s cadence and options (`Telemetry:Retention:*`), and its "Retention sweep complete" log line.
- The tenants a sweep visits come from the **telemetry** side (relational `resources`, Parquet folders, ClickHouse all
  rows), never from the control plane's `tenants`, so orphan data still expires.
- Granularity: row on the relational providers, day on ClickHouse and Parquet.
- Ingest, read paths and their SQL. Only retention's own deletes change.

## Phase 1: control plane and Core

**Schema** (`schema/PostgreSQL-ControlPlane.sql`, `SqlServer-ControlPlane.sql`, `MySQL-ControlPlane.sql`):
- Add the four columns to `tenants` with their defaults. Use the provider's normal form for the timestamp default
  (`NOW()`, `SYSUTCDATETIME()`, `CURRENT_TIMESTAMP(6)`), matching `created_at`.
- Remove `retention_settings` (table, `CHECK`, seed `INSERT`) and its header comment mentions.
- The three scripts record `control_plane_schema_version` `4.0.1`, and `apply-schema.sh` sets `CONTROLPLANE_TARGET_VERSION=4.0.1`.

**Core:**
- `RetentionDefaults` (new, static): `TraceDays = LogDays = MetricDays = 30`.
- `RetentionSettings` (model): unchanged fields; its doc comment now says it is one tenant's windows.
- `IRetentionSettingsRepository`:
  - `Task<RetentionSettings?> GetSettingsAsync(long tenantId, ct)`: null when no tenant row.
  - `Task<bool> UpdateSettingsAsync(long tenantId, RetentionSettings settings, ct)`: `UPDATE tenants ... WHERE id = @tenantId`, false
    when no row was updated; sets `retention_updated_at`.
  - `Task<IReadOnlyDictionary<long, RetentionSettings>> GetAllAsync(ct)`: every tenant's windows, for the worker.
- `RetentionSettingsRepositoryBase`: the three statements against `tenants`. Still one SQL form for all three providers.
- `RetentionPolicy` (new): built once per sweep from `GetAllAsync` and `RetentionDefaults`, with one fixed "now".
  `CutoffFor(signal, tenantId)` (unknown tenant → built-in), `KnownTenants`, `DistinctWindows(signal)` and
  `LongestWindow(signal)` (the built-in is always included, since orphans may exist). Keeps today's negative-period guard.
- `IRetentionSweeper`: the three `Delete*Async` take `RetentionPolicy` instead of a `TimeSpan`. Doc comment rewritten
  (windows are per tenant; the orphan rule).

**API** (`Keryhe.Telemetry.Api`):
- `RetentionWorker`: reads `GetAllAsync`, builds the policy, passes it to the three sweeps. Nothing else changes.
- `SettingsController`'s retention actions move to a new `[TenantScoped]` controller (`[Route("settings")]`, so the
  convention gives `tenants/{tenantId}/settings/retention`). The tenant comes from `ITenantContext`. Same validation
  (1..3650). 404 when the repository reports no tenant. If `SettingsController` is left with no actions, delete it.
- The authorization test that enumerates every action, and the `ApiHttp` route tests, follow the move.

## Phase 2: relational sweeps (PostgreSQL, SQL Server, MySQL)

`RetentionSweeperBase`:
- `SweepPerTenantAsync` takes the policy and binds each tenant's own cutoff (the tenant list query is unchanged).
- Metric data points become per tenant: for each tenant from `resources`, `BatchedDeleteSql(table,
  "metric_id IN (SELECT id FROM metrics WHERE tenant_id = @tenantId) AND time_unix_nano < @cutoff")` on each of
  `TimePrunedMetricTables`. Check the plan on each provider (EXPLAIN / showplan) that it seeks `(metric_id,
  time_unix_nano, id)` per metric and not a scan, especially PostgreSQL's `ctid = ANY (ARRAY(SELECT ctid ... LIMIT n))`
  form and MySQL's `DELETE ... WHERE ... IN (subquery) LIMIT n`. If MySQL refuses or scans, use a join-delete or select the
  tenant's metric ids first and delete by id list in chunks.
- `MySqlRetentionSweeper`'s hour-tier rollups (`request_rollup_hour`, `log_rollup_hour`) take the policy the same way.

**Telemetry schema (decision 9):** remove the time-only index from the five data-point tables in
`PostgreSQL-Telemetry.sql` (BRIN), `SqlServer-Telemetry.sql` and `MySQL-Telemetry.sql` (`(time_unix_nano)`), with their
"Time access for retention" comments; the four telemetry scripts (ClickHouse included, though its tables do not change) record `telemetry_schema_version` `4.0.1`, and `apply-schema.sh` sets `TELEMETRY_TARGET_VERSION=4.0.1`. Before removing, grep every read
repository and export for a data-point query not led by `metric_id` (expected: none) and record the result here.
Update the header comments that say retention uses those indexes.

## Phase 3: ClickHouse

`ClickHouseRetentionSweeper`, per signal (traces: `spans`, `trace_index`, `request_rollup_minute`; logs: `log_records`,
`log_rollup_minute`; metrics: the five points tables, then `metric_series`/`metric_catalog`):
1. **Drop.** `DROP PARTITION ID` for each day wholly before the cutoff of `LongestWindow(signal)`, as today (rows counted
   from `system.parts` for the raw tables).
2. **Shorter windows.** For each distinct window `w` shorter than the longest, one lightweight delete over the days
   `[cutoffDay(longest), cutoffDay(w))`:
   - known tenants with window `w`: `DELETE FROM <t> WHERE tenant_id IN (@ids) AND <time> >= @from AND <time> < @to`.
   - if `w` is the built-in window, a second statement for orphans: `tenant_id NOT IN (@knownTenantIds)`, same range.
   - `SETTINGS lightweight_deletes_sync = 1`, as the existing catalog delete. Raw-table rows removed are counted with a
     `count()` over the same predicate first. That count is approximate if rows arrive in between, which is acceptable
     for a log line.
   - The time bound prunes partitions and `tenant_id` leads every sort key, so each delete reads only the expired days'
     granules for those tenants. The logs table needs the `toStartOfFiveMinutes(timestamp)` bound too, and the points
     tables `toStartOfHour(time)` (see the provider notes), or the sort key does not prune.
3. **Skip what's done.** A delete for a (table, window) whose newest expired day was already deleted is skipped. That is
   remembered in memory (`(table, window) → last day deleted`), so a restart repeats each delete once, which is harmless.
   Without this, the hourly sweep would re-run every delete over up to `longest - w` days each hour.
4. **`metric_series` / `metric_catalog`** (not partitioned): `DELETE ... WHERE tenant_id IN (@ids) AND last_seen <
   @cutoff` per distinct window, plus the orphan `NOT IN` form at the built-in window.

Reads: queries skip masked rows (`_row_exists`) on the affected parts until merges remove them. Measure it (phase 8).

## Phase 4: Parquet

`ParquetRetentionSweeper`: enumerate partitions up to the longest window's cutoff day (`EnumeratePartitions(table,
null, MinValue, longestCutoffDay - 1)`), then delete each `(tenant, day)` whose day is before
`policy.CutoffFor(signal, tenant)`'s day. The same per table, derived tables with their signal, and the row count from
the manifests as today. Reads and writes are unaffected.

## Phase 5: ingest-time drop (ClickHouse, Parquet)

- `RetentionWindowCache` reads `SELECT id, trace_retention_days, log_retention_days, metric_retention_days FROM tenants`
  into a tenant → windows map on its refresh interval, and stays fail-open (never loaded = drop nothing).
- `IRetentionWindows.OldestAllowedUtc(string signal, long tenantId)`: the tenant's window, else the built-in.
- `PartitionedIngestionWorker.Drain` (and `ClickHouseIngestionWorker` if it has its own check) resolve the cutoff per
  record's tenant (`TenantOf.Of(item)`), memoized per tenant within one drain so the cost stays one dictionary lookup
  per record.
- Note in the README: after a window is **lengthened**, records between the old and new cutoff are still dropped at
  ingest for up to the cache refresh interval.

## Phase 6: UI

- The route moves under the tenant: `t/:tenantId/settings` (behind `tenantRouteGuard`), and `legacy('settings')` redirects
  the old `/settings` to the last-used tenant. The header/nav link follows the current tenant.
- `SettingsApiService` builds the URL with `tenantApiUrl(apiUrl, tenantId)`. 403 with `X-Telemetry-Denied: tenant` is
  already handled by `authInterceptor`.
- `SettingsComponent`: form defaults 30/30/30, the tenant's name in the page heading, reloads when the route tenant
  changes. Keeps the `updatedAt` display. No lowering confirmation.
- The "Settings" nav item loses `global: true` and links under the tenant like the others (label and icon unchanged).
  `ShellComponent.selectTenant` then treats settings as a tenant section: switching tenant on the settings page opens the
  new tenant's settings (its comment "Elsewhere (settings) there is nothing to navigate" goes).

## Phase 7: header menus (global settings and user)

**Header** (`layout/shell/shell.component.html`), in `.toolbar-controls-secondary` after `<app-time-range-picker />`:
the gear button, then the user button (when shown). The header's own Sign out button is removed. Both stay visible at every
width, including below 600 px beside the icon-only time picker; check the 960 px and 600 px wraps in
`shell.component.scss` still keep them on the controls row.

**Global settings menu**, a new standalone `GlobalSettingsMenuComponent` (`layout/global-settings-menu/`):
- A `mat-icon-button` with the `settings` (gear) icon, tooltip/`aria-label` "Settings", opening a `mat-menu`.
- A "Theme" `mat-form-field` + `mat-select` (Light / Dark / System) bound to `ThemeService.mode()`, calling `setMode`.
  Clicks and keys inside the field must not close the menu (stop propagation on the field), and the select's overlay
  must open above the menu panel. The menu closes on Escape or an outside click.
- Laid out as labelled rows so a later global setting is one more row (a comment says so). Settings stay per browser
  (localStorage). No Sign out here.
- **Settings page**: remove the Theme card, `themeModes`, `setTheme`, the `ThemeService` injection and the `.theme-tile`
  styles from `features/settings/`. `ThemeService` is still applied at startup (`app.ts` calls `setMode`); update the
  shell's injection comment, which says the control lives on the settings page.

**User**, a `UserService` (`core/auth/`) exposing a `user` signal: `{ name?, givenName?, email?, picture? }` when signed
in, `null` when not, and an `unknown` state while loading or when the lookup failed.
- **OIDC**: from the session's user data (ID token claims, plus userinfo if the host enables it):
  `name`, `given_name`, `email`, `picture`. Signed in = the session reports authenticated. `OidcOptions.Scope` defaults
  to `openid profile email` (`TelemetryUiOptions.cs`, the Angular config default if it has one, and the docs).
- **Cookie mode**: `GET {apiUrl}/me` once at startup through the normal `HttpClient` (so the interceptor adds the
  credentials and `X-Telemetry-Client`). `signedIn: false` → `null`. An error (network, 500) → `unknown`.
- `AuthService.canSignOut` becomes: a sign-out mechanism exists (OIDC session or `logoutUrl`) **and** the user is
  signed in or `unknown` (signed in but the details could not be read, so Sign out stays reachable).

**User button and menu**, a new standalone `UserMenuComponent` (`layout/user-menu/`), rendered when `user` is not
`null` (signed in or `unknown`):
- The button: a round avatar. `picture` as an `<img>` (`referrerpolicy="no-referrer"`, which some providers need);
  on its `(error)`, or without a picture, the upper-cased first letter of `givenName`, else `name`, else `email`, on a
  theme-coloured circle; with none of those (or `unknown`), the `person` icon. Tooltip and `aria-label`: the name,
  else the email, else "Account".
- The menu: a non-clickable header row with the larger avatar, the name and the email (each line only if present), a
  divider, then "Sign out" (`logout` icon) calling `AuthService.signOut()`.

**API: `GET {base}/me`** (`Keryhe.Telemetry.Api`), a global controller (not `[TenantScoped]`):
- Response `{ signedIn, name?, givenName?, email?, picture? }`, always 200, `Cache-Control: no-store`.
- Exempt from authorization through a new `[TelemetryAnonymous]` marker. `TelemetryAuthorizationFilter` skips an action
  carrying it, and the action-enumeration test accepts it in place of `[TelemetryOperation]` (exactly one of the two).
  The convention still applies the base path.
- Who the caller is: when the Read operation's policy names `AuthenticationSchemes`, authenticate through those schemes
  (`TelemetryPolicyResolver` + `IPolicyEvaluator.AuthenticateAsync`, no authorization step), as the filter does today.
  Otherwise use `HttpContext.User` from the default scheme. Not authenticated → `{signedIn: false}`.
- Claims, first non-empty wins (fixed list, no configuration):
  - name: `name`, `ClaimTypes.Name`, `Identity.Name`
  - givenName: `given_name`, `ClaimTypes.GivenName`
  - email: `email`, `ClaimTypes.Email`, `preferred_username` (only if it contains `@`)
  - picture: `picture`, absolute `http`/`https` URLs only
- `TelemetryApiStartupValidator` needs no change (`/me` adds no policy).

## Phase 8: tests

- **Defaults parity** (no Docker): parse the three control-plane scripts and assert each `*_retention_days` default equals
  `RetentionDefaults` (in the style of `ColumnLimitsTests`).
- **`ControlPlaneTestsBase`**: a new tenant reads 30/30/30; updating one tenant leaves another unchanged and sets
  `UpdatedAt`; get/update for an unknown id returns null/false; `GetAllAsync` lists every tenant.
- **`RetentionTestsBase`** (every provider): three tenants, A on 10 days, B on 60 (or the built-in for B; at least two
  distinct windows), and an orphan with data but no `tenants` row (built-in 30). Seed rows at the existing offsets around
  *each* tenant's cutoff, then assert each tenant's survivors for spans, logs, data points and the rollups. ClickHouse
  and Parquet keep their day-granularity exceptions. Negative control: give the sweep a policy with A's window swapped
  for B's, and the test must fail.
- **ClickHouse**: the shorter-window delete reads only the expired days (assert `system.query_log` `read_rows` against
  the table's total); a second sweep on the same day issues no delete (statement count from `system.query_log`);
  `metric_series`/`metric_catalog` per tenant.
- **Relational metric plans**: extend the existing plan tests (PostgreSQL index-not-seq-scan, SQL Server showplan) to the
  per-tenant data-point delete.
- **Ingest drop**: `ClickHouseIngestionWorkerTests`' `FakeRetention` and Parquet `IngestionWorkerTests` gain per-tenant
  windows: two tenants with different windows in one export, plus an unknown tenant on the built-in.
- **ApiHttp**: the new route (Read/ManageSettings, tenant refusal → 403 + header, unknown tenant → 404, validation 400);
  the old `settings/retention` → 404; the action-enumeration test.
- **Read cost on ClickHouse** (manual, recorded here): `TraceQueryBench` on ClickHouse with a lightweight delete applied
  to some of the seeded days versus none.
- **ApiHttp `/me`**: anonymous → 200 `signedIn:false` with authorization on and off (no 401, no challenge); signed in
  with the short claim names, with the `ClaimTypes.*` URIs, with only `Identity.Name`; a policy naming a scheme
  authenticates through it; a non-http `picture` is dropped; `Cache-Control: no-store`. The action-enumeration test
  covers `[TelemetryAnonymous]`.
- **UI** (manual, in the browser; the Angular project has no meaningful test suite), light and dark, at 1280, 800 and
  375 px wide:
  - The gear menu opens and closes, and the theme select changes the theme without closing the menu and survives a
    reload. The menu has no Sign out.
  - The settings page shows retention only and follows a tenant switch.
  - Dev hosts (no sign-in): gear only, no user button.
  - Cookie mode, with a test scheme on a local host: the user button shows the initial, the menu shows the name and
    email, and Sign out goes to `logoutUrl`. With `/me` failing, the generic icon still offers Sign out.
  - OIDC against a local IdP (e.g. Keycloak in Docker) with and without a `picture`, plus a broken picture URL (falls
    back to the initial).

## Phase 9: docs

`CLAUDE.md` (Retention section and table, Database/control-plane table lists, `IRetentionSettingsRepository`
description, the relational data-point index set, the ClickHouse and Parquet notes), `docs/SETUP.md`,
`docs/CONFIGURATION.md` (the UI notes where they mention the settings page, the theme or Sign out; the new OIDC scope default; `GET /me`), the `Keryhe.Telemetry.Api` README (`/me`, `[TelemetryAnonymous]`), the provider READMEs, `Keryhe.Telemetry.Core/README.md`, and the doc comments on
`IRetentionSweeper`, `IRetentionSettingsRepository`, `RetentionSettings`, `RetentionSweeperBase` and the sweepers.

## Order and verification

Phases 1–2 together (the interface change breaks every sweeper), then 3, 4, 5, 6, 8 alongside, 7 (independent of the rest; can go first or separately), 9 last.
`dotnet build Telemetry.sln`; `dotnet test tests/Keryhe.Telemetry.IntegrationTests` per provider
(`--filter Provider=...`) plus `Suite=ApiHttp`; `tests/Keryhe.Telemetry.Parquet.Tests`; the UI built and the settings
page checked live against the API host for two tenants. The dev database must be recreated at 4.0.1 (control plane and telemetry).

## Implementation notes (2026-10-11)

All phases implemented, uncommitted. What differed from the plan, and what was measured:

- **Data-point index audit (phase 2).** Every data-point query in `MetricReadRepositoryBase` (series, distribution, exemplars, labels,
  the "seen in range" `EXISTS`, the latest-value join) is led by `metric_id`; none relies on the time-only index, so it was dropped from the
  PostgreSQL, SQL Server and MySQL telemetry scripts (4.0.1).
- **MySQL** plans `DELETE ... WHERE metric_id IN (SELECT id FROM metrics WHERE tenant_id = @t) AND time < @c LIMIT n` as a scan of the
  data-point table (`type ALL` + `unique_subquery`), so `MySqlRetentionSweeper` sets `MetricIdsFirst`: it reads the tenant's metric ids and
  deletes by id list (200 per statement), a range on the primary key. PostgreSQL (nested loop, `metric_id = metrics.id` index condition) and
  SQL Server (clustered index seek) keep the subquery form; both have plan tests with a negative control.
- **Parquet** (phase 4) enumerates folders up to the cutoff day of the **shortest** window (the latest cutoff), not the longest as written
  above: the longest window's cutoff is the earliest, so it would never reach a short-window tenant's expired days. Each `(tenant, day)` is then
  checked against its own tenant's cutoff.
- **ClickHouse** `ClickHouseRetentionMemory` (singleton) is keyed by table, window and tenant set, so a tenant moved to another window is not
  skipped. The fixtures clear it on reset (a test truncates the tables under it).
- **User button** is shown for state `in`, or `unknown` when a sign-out mechanism exists (an `unknown` button with nothing in its menu is noise).
- Not run: stress smoke, `TraceQueryBench` on ClickHouse with a lightweight delete applied, OIDC against a local IdP, cookie mode against a real
  host (the UI was checked against a stub API for cookie mode: signed in, signed out, `/me` failing; light/dark; 375 px).
