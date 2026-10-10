# Configuration reference

Every configuration key the hosts and libraries read, with its default. Anything a host's `appsettings.json` does not
set falls back to the default shown here. Any key can be set the usual .NET ways: `appsettings.json`,
`appsettings.{Environment}.json`, User Secrets, environment variables (`:` becomes `__`, e.g.
`Telemetry__Query__SummaryTimeoutSeconds=10`) or the command line (`--Telemetry:Query:SummaryTimeoutSeconds=10`).

**Which host reads what.** The collector host (`Keryhe.Telemetry.Collector.Server`) reads the [Collector](#collector)
sections, the API host (`Keryhe.Telemetry.Api.Server`) the [API](#api) sections. [Common](#common-settings) settings apply
to both.

## Common settings

### Database and connection strings

| Key | Default | Host | Description |
|---|---|---|---|
| `Database:Provider` | none (required) | collector, API | The telemetry data provider: `PostgreSQL`, `SqlServer`, `ClickHouse` or `MySql`. Unknown or missing fails startup. |
| `ControlPlane:Provider` | none (required) | collector, API, Admin | The control-plane provider (tenants, API keys, alert rules, retention settings): `PostgreSQL`, `SqlServer` or `MySql`. ClickHouse is not a control-plane provider. Unknown or missing fails startup naming the key. |
| `ConnectionStrings:Collector` | none (required) | collector | Write path: the telemetry bulk writer, metric-touch and rollup stores. |
| `ConnectionStrings:Api` | none (required) | API | Read path: every telemetry read repository and the retention sweeper. |
| `ConnectionStrings:ControlPlane` | none (required) | collector, API | The control-plane database: the collector's API-key lookup and touch, the API's alert rules, tenant catalog and retention settings. No fallback to `Collector`/`Api`; missing fails startup naming the key. On PostgreSQL, SQL Server and MySQL it normally points at the same database as the data strings. |
| `ConnectionStrings:Admin` | none (required) | Admin tool | `Keryhe.Telemetry.Admin`'s connection (the control-plane database). |

The committed `appsettings.json` files leave the connection strings empty; local values live in User Secrets.

### Host settings

| Key | Default | Host | Description |
|---|---|---|---|
| `Kestrel:Endpoints` | per host | both | Listening ports and protocols (CLAUDE.md, "Default ports"). Don't set `ASPNETCORE_URLS` on the collector: it replaces these endpoints and collapses their per-endpoint protocols, breaking h2c gRPC on 5117. |
| `Logging:LogLevel` | ASP.NET Core defaults | both | Standard .NET logging levels. |
| `AllowedHosts` | `*` | both | Standard ASP.NET Core host filtering. |

## Collector

Host: `Keryhe.Telemetry.Collector.Server` (gRPC OTLP ingestion, the write path). Uses `Database:Provider`,
`ControlPlane:Provider`, `ConnectionStrings:Collector` and `ConnectionStrings:ControlPlane` above.

```json
{
  "Telemetry": {
    "Collector": { "AllowInsecureTransport": false },
    "Ingestion": { "FlushConcurrency": 4 },
    "TenantResolution": { "PositiveCacheTtlSeconds": 30 },
    "MetricTouch": { "FlushIntervalSeconds": 60 },
    "Rollup": { "FlushIntervalSeconds": 15, "CloseGraceSeconds": 30 }
  }
}
```

### Transport (`Telemetry:Collector`)

`TelemetryCollectorOptions`.

| Key | Default | Description |
|---|---|---|
| `AllowInsecureTransport` | `false` | Outside Development the collector fails startup on a plaintext (`http://`) TCP address, because API keys would cross the network in cleartext. Set `true` only when TLS is terminated by a proxy in front of the collector. Unix-socket addresses are exempt. |
| `MaxConnectionAgeSeconds` | 0 | A connection older than this many seconds (plus up to 10% jitter) is sent an HTTP/2 `GOAWAY` after its current request, so clients reconnect and a layer-4 load balancer spreads them over every collector instance (an OTLP exporter otherwise keeps one connection open for as long as it runs). 0 never closes a healthy connection. Not needed behind a gRPC-aware balancer or with client-side round-robin. |
| `HttpBasePath` | empty | A prefix for the OTLP/HTTP routes (`{HttpBasePath}/v1/traces`, `/v1/logs`, `/v1/metrics`), for a host that mounts them elsewhere. Must start with `/`. |
| `MaxReceiveMessageSizeBytes` | 4194304 | The largest OTLP export accepted, in bytes **after decompression** (gRPC's default, now explicit). A larger one is refused with `RESOURCE_EXHAUSTED` before it is read into memory; a gzip message is bounded while it is decompressed, not only by its size on the wire. A client's exporter should batch below it. |
| `AuthFailureLimit:PerSecond` | 5 | Failed authentication attempts a client **address** earns back per second (a token bucket). Only failures (missing, malformed, invalid or expired key) take a token; a client with none left is refused with gRPC `RESOURCE_EXHAUSTED` (no `RetryInfo`, so exporters do not retry) before any control-plane lookup, unless its key is already cached as valid. 0 turns the limit off. |
| `AuthFailureLimit:Burst` | 20 | Failed attempts in a burst before an address is refused. |
| `AuthFailureLimit:MaxTrackedClients` | 100000 | Addresses tracked at once; past it, new ones share one overflow bucket. IPv6 addresses are grouped by /64. |
| `ManagementPort` | 0 | Port of the plaintext HTTP/1.1 management endpoint that serves `/healthz/live` and `/healthz/ready`. 0 takes the port of the Kestrel endpoint named `Management` (the shipped `appsettings.json` defines one on `http://127.0.0.1:5119`); with neither, the HTTP health endpoints are not mapped. The gRPC `grpc.health.v1.Health` service is always available on the OTLP endpoints. |
| `ManagementEndpoints` | `[]` | Plaintext management addresses allowed outside Development besides loopback and private-network ones, as the exact `Url` of the Kestrel endpoint (for example `http://0.0.0.0:8081` in a container whose port is not published). A plaintext `Management` endpoint on any other address still fails startup. |

### Listening endpoint and certificate (`Kestrel:Endpoints`)

The collector's endpoints are named Kestrel endpoints. The files merge by endpoint name, so an override only needs
the keys it changes.

| Key | Default | Description |
|---|---|---|
| `Kestrel:Endpoints:Https:Url` | `https://0.0.0.0:7057` | The TLS endpoint (`appsettings.json`): OTLP/gRPC and OTLP/HTTP on one port. |
| `Kestrel:Endpoints:Https:Protocols` | `Http1AndHttp2` | TLS ALPN gives a gRPC client HTTP/2 and an OTLP/HTTP client HTTP/1.1 on the same port. gRPC requires HTTP/2, so a plaintext endpoint that must serve gRPC has to be `Http2`-only (see below). |
| `Kestrel:Endpoints:Https:Certificate:Path` | none | A `.pfx` (or a `.pem`/`.crt` with `KeyPath`). With no certificate configured Kestrel uses the ASP.NET Core development certificate, which is normally absent (startup fails) or untrusted outside a development machine. |
| `Kestrel:Endpoints:Https:Certificate:Password` | none | The `.pfx` (or encrypted key) password. Set it as an environment variable or secret, never in a committed file. |
| `Kestrel:Endpoints:Https:Certificate:KeyPath` | none | The private key file, when `Path` is a PEM certificate. |
| `Kestrel:Endpoints:Https:Certificate:Store` / `Location` / `Subject` | none | Load from a certificate store instead of a file (e.g. `My` / `LocalMachine` / `collector.example.com`). `AllowInvalid` (default `false`) permits a self-signed or otherwise invalid certificate. |
| `Kestrel:Endpoints:Http:Url` | `http://localhost:5117` (Development only) | The plaintext h2c endpoint, `Protocols: Http2`, from `appsettings.Development.json`; used by the TestDataGenerator and the stress harness. |
| `Kestrel:Endpoints:OtlpHttp:Url` | `http://localhost:5118` (Development only) | The plaintext HTTP/1.1 endpoint for OTLP/HTTP (`Protocols: Http1`): h2c and HTTP/1.1 cannot share a plaintext port, so the Development OTLP/HTTP clients (`Generator:Protocol` `http/protobuf`, `curl`) get their own. |

Give the certificate in an override (`appsettings.Production.json` next to the executable, or environment variables)
rather than editing the shipped `appsettings.json`, which every publish replaces. A certificate file:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Certificate": { "Path": "/etc/telemetry/collector.pfx" }
      }
    }
  }
}
```

with the password in the environment:

```
Kestrel__Endpoints__Https__Certificate__Password=<password>
```

A PEM certificate and key:

```json
"Certificate": { "Path": "/etc/telemetry/collector.crt", "KeyPath": "/etc/telemetry/collector.key" }
```

The Windows certificate store (the service account needs read access to the private key):

```json
"Certificate": { "Store": "My", "Location": "LocalMachine", "Subject": "collector.example.com" }
```

**TLS terminated by a proxy.** Repoint the endpoint at plaintext and tell the transport guard it is intended; the proxy must forward
HTTP/2 (h2c) to the collector. Plaintext cannot negotiate a protocol, so the endpoint becomes `Http2`-only (gRPC), and OTLP/HTTP needs an
HTTP/1.1 endpoint of its own for the proxy to forward to:

```
Kestrel__Endpoints__Https__Url=http://0.0.0.0:7057
Kestrel__Endpoints__Https__Protocols=Http2
Kestrel__Endpoints__OtlpHttp__Url=http://0.0.0.0:7058
Kestrel__Endpoints__OtlpHttp__Protocols=Http1
Telemetry__Collector__AllowInsecureTransport=true
```

Don't use `ASPNETCORE_URLS` or `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` for the collector: Kestrel ignores them while
`Kestrel:Endpoints` is configured.

### Ingestion (`Telemetry:Ingestion`)

`TelemetryIngestionOptions`.

| Key | Default | Description |
|---|---|---|
| `MaxQueuedLogRecords` | 200000 | Log records resident in the ingestion queue before a further export waits (backpressure). |
| `MaxQueuedMetrics` | 200000 | The same, for metrics. |
| `MaxQueuedSpans` | 200000 | The same, for spans (counted in spans, not traces). |
| `MaxQueuedBytesPerSignal` | 268435456 | Bytes each signal's queue may hold, measured as the protobuf size of the exports that produced the queued records (the in-memory models are larger than their wire form, so this is a proxy). An export is admitted when both its records and its bytes fit, or the queue is empty. 0 turns the byte budget off. Exposed as the `resident_bytes` gauge. |
| `MaxLogFlushBatchSize` | 2000 | Log records merged into one flush. |
| `MaxMetricFlushBatchSize` | 2000 | Metrics merged into one flush. |
| `MaxTraceFlushSpanBatchSize` | 2000 | Spans merged into one trace flush. |
| `FlushConcurrency` | 4 | Concurrent flush loops per signal. |
| `MaxFlushRetries` | 5 | Retries after a failed flush before the batch is dropped (counted on `records_dropped`). |
| `RetryBaseDelayMilliseconds` | 200 | First retry delay; doubles each attempt, with jitter. |
| `RetryMaxDelayMilliseconds` | 5000 | Cap on the retry delay. |
| `MaxGateWaitMilliseconds` | unset | How long an export waits for room in a full queue before it is refused with gRPC `UNAVAILABLE` + `RetryInfo`. Unset = 2000, and on ClickHouse the day-buffer `LingerMilliseconds` + 2000 (its queue frees room a whole buffer at a time). 0 refuses at once; negative waits without limit (the export is held open until the client's deadline). Setting it below ClickHouse's linger logs a warning at startup. While a queue has been refusing callers for the whole wait, further exports are refused before they are converted. |
| `RejectRetryDelayMilliseconds` | 1000 | The delay a refused client is told to wait (`RetryInfo`), plus up to 50% random jitter. |
| `MaxSplitFlushes` | 64 | Extra flushes spent isolating records the database permanently refuses (a value too long, a bad type) after a flush fails with such an error. The batch is cut in half repeatedly so only the bad records are dropped; what is left at the cap is dropped. Transient errors (locks, connections, timeouts) are retried, never split. |
| `ReadinessControlPlaneSeconds` | 60 | `/healthz/ready` fails when API-key lookups have been failing for longer than this. |
| `ReadinessSaturatedSeconds` | 10 | `/healthz/ready` fails when a signal's queue has been refusing exports continuously for longer than this. |

On ClickHouse the `MaxQueued*` gates and the retry keys apply, but `MaxLogFlushBatchSize`, `MaxMetricFlushBatchSize`,
`MaxTraceFlushSpanBatchSize`, `FlushConcurrency` and `FlushLingerMilliseconds` do not: see the next section.

### Fair use between tenants (`Telemetry:Ingestion:TenantQuota`)

`TenantQuotaOptions`, reloadable without a restart. Every tenant shares each signal's queue, so without a quota one tenant that sends more
than the database can absorb fills it and every other tenant's exports are refused too.

| Key | Default | Description |
|---|---|---|
| `MaxShare` | 0.5 | The most of a signal's queue (records, and bytes where the byte budget is on) one tenant may hold. An export that would take its tenant over the share is refused like a full queue (`UNAVAILABLE` + `RetryInfo`, reason `tenant_quota`); a tenant holding nothing is always admitted, so a single large export is never refused for size alone. `1` turns the quota off. |
| `RecordsPerSecond` | 0 | The most records per second a tenant may send per signal (a token bucket with a one-second burst). Over it, an export is refused with `UNAVAILABLE` and the time until enough tokens return (reason `tenant_rate`). `0` turns the rate limit off. |
| `Overrides:<tenantId>:MaxShare`, `Overrides:<tenantId>:RecordsPerSecond` | none | Per-tenant exceptions. |

A **single-tenant installation** gets half the queue at the default share; set `MaxShare` to `1` there. On ClickHouse a tenant's share also
counts the records waiting out the day-buffer linger, so a tenant at 40,000 records/s with the 2.5 s linger already holds about 100,000 (half
the default 200,000 queue): raise the queue or the share for very high rates from few tenants. Who is holding the queue is visible on the
`tenant_resident_records` gauge (tags `signal`, `tenant`).

### Input limits (`Telemetry:Ingestion:Limits`)

`IngestionLimitsOptions`. Applied while an export is converted, on every provider. Nothing is rejected for exceeding one: the value is
cut, the record's own dropped-attributes/events/links count is raised by what was cut (as an SDK would), and each cut is counted on
`records_truncated` (tags `signal`, `limit`). 0 means unlimited. A resource is truncated before it is hashed, so the same oversized
resource always maps to the same row.

| Key | Default | Description |
|---|---|---|
| `MaxAttributes` | 128 | Attributes kept per span, log record, event, link, data point, resource or scope. |
| `MaxAttributeKeyLength` | 256 | Characters of an attribute key. |
| `MaxAttributeValueLength` | 16384 | Characters of a string attribute value (bytes of a bytes value). |
| `MaxEventsPerSpan` | 128 | Events kept per span. |
| `MaxLinksPerSpan` | 128 | Links kept per span. |
| `MaxLogBodyLength` | 65536 | Characters of a log body. A bytes, array or map body that is still longer once rendered is cut and stored as a string body. |
| `MaxNestingDepth` | 32 | Levels of nested array/map values; deeper values are dropped. |
| `MaxElementsPerLevel` | 1024 | Elements kept at each level of an array or map value. |

Separately from these, every value stored in a sized text column is clipped to the column's size on every provider (`ColumnLimits`: span,
service, scope and metric names 255, event name 256, metric unit 63, schema URL 2048, severity text 255), whatever the limits above are set to; a
value over its column is a permanent flush error on PostgreSQL, SQL Server and MySQL. Clipping is by characters and never splits a surrogate
pair. The `limit` tag then names the column (`span_name`, `service_name`, `schema_url`, ...).

### ClickHouse ingestion (`Telemetry:ClickHouse:Ingestion`)

`ClickHouseIngestionOptions`, read by the collector when `Database:Provider` is `ClickHouse`. Records are sorted into
per-day buffers (by the UTC day of their time) and flushed as one insert per day per table.

| Key | Default | Description |
|---|---|---|
| `LingerMilliseconds` | 2500 | How long the current day's buffer collects records after its first before flushing. |
| `LateLingerMilliseconds` | 30000 | The same for an earlier day's buffer (late data). |
| `MaxSpanBatchRecords` | 300000 | A buffer flushes at once when it holds this many spans; a larger one is cut into pieces. |
| `MaxLogBatchRecords` | 300000 | The same, for log records. |
| `MaxMetricBatchRecords` | 300000 | The same, for metrics (counted as metrics, not data points). |
| `MaxLateDayBuffers` | 8 | With more late-day buffers than this, the oldest flushes early. |
| `CatalogRefreshSeconds` | 300 | A metric or series that keeps reporting has its `metric_catalog`/`metric_series` row re-written at most this often (and once when first seen). |
| `MaxTrackedSeries` | 1000000 | Series and metrics remembered between flushes; past it the collector forgets and simply re-writes. |
| `ParallelFlushMinRows` | 50000 | A raw-table insert of at least this many rows is split into concurrent inserts, and spans, logs and metrics (by data points) are built into rows on several threads; a smaller batch is one insert, as before. Set it very high to turn splitting off. |
| `InsertPieceRows` | 25000 | The fewest rows in a piece of a split insert. |
| `MaxParallelInserts` | 4 | The most concurrent inserts per table for one split batch, and the most row-building threads. |
| `RetentionRefreshSeconds` | 300 | How often the retention windows are re-read from the control plane. Records older than their window are dropped at ingest (`records_dropped`, `reason=out_of_retention`); if the windows cannot be read, nothing is dropped. |

All values must be greater than 0. Extra instrument: `late_buffer_records` (records waiting in non-current day buffers).
`derived_rows_dropped` (tag `table`) counts `trace_index`, rollup and catalog rows lost because their insert failed after the raw rows were stored.
`records_dropped` carries a `reason` tag on ClickHouse (`retries_exhausted`, `out_of_retention`, `shutdown`).

### Tenant resolution (`Telemetry:TenantResolution`)

`TenantResolutionOptions`.

| Key | Default | Description |
|---|---|---|
| `PositiveCacheTtlSeconds` | 30 | How long a valid API key's tenant is cached. |
| `NegativeCacheTtlSeconds` | 5 | How long an invalid or inactive key is cached. |
| `LastUsedFlushIntervalSeconds` | 60 | Interval of the `api_keys.last_used_at` flush. |
| `MaxConcurrentLookups` | 16 | Control-plane key lookups in flight at once on one collector, for keys that are not cached. Lookups of the *same* key hash are coalesced into one query regardless. |
| `LookupQueueTimeoutMilliseconds` | 1000 | How long a lookup waits for one of those slots before the request is answered as "lookup unavailable" (gRPC `UNAVAILABLE`, retryable, never cached). |

### Metric touch (`Telemetry:MetricTouch`)

`MetricTouchOptions`. Writes `metric_last_seen` on the relational providers (ClickHouse has no such table: its metric
catalog is written by the collector, see the ClickHouse ingestion section).

| Key | Default | Description |
|---|---|---|
| `FlushIntervalSeconds` | 60 | Interval between flushes. |
| `MaxBatchSize` | 4000 | Rows per batch (kept under SQL Server's lock-escalation threshold). |

### Summary rollups (`Telemetry:Rollup`)

`RollupOptions`. The collector keeps a per-minute rollup of inbound spans and log records
(`request_rollup_minute`, `log_rollup_minute`) that the dashboard, trace list, logs page and the error-rate and
log-spike alerts read. On the relational providers an in-memory accumulator is appended to the database once a minute
has closed (ClickHouse writes the same tables from the ingestion worker, with no materialized views, and ignores these keys). **The API host reads
`FlushIntervalSeconds`, `CloseGraceSeconds` and `ArrivalMarginSeconds` from the same section, so a host that changes
the first two on the collector must change them on the API too.**

| Key | Default | Host | Description |
|---|---|---|---|
| `FlushIntervalSeconds` | 15 | both | Seconds between rollup appends (collector); part of the write margin (API). |
| `CloseGraceSeconds` | 30 | both | A minute is appended once its end is this many seconds in the past (collector); part of the write margin (API). |
| `MaxBufferedRows` | 200000 | collector | Rows held in memory while appends fail; beyond it the oldest are dropped (`rollup_rows_dropped`). |
| `MaxBatchSize` | 4000 | collector | Rows per append call (kept under SQL Server's lock-escalation threshold). |
| `ArrivalMarginSeconds` | 60 | API | Extra margin for request duration, SDK export delay and queue lag before a minute counts as written. The charts leave out the minutes after `now - (CloseGraceSeconds + FlushIntervalSeconds + ArrivalMarginSeconds)`, and the rollup alerts read up to that point, so they fire about 1.75 minutes later than on raw data. |
| `CompactionIntervalSeconds` | 300 | API | MySQL only (the hour tier): seconds between compaction runs. |
| `RecompactHours` | 6 | API | MySQL only: how many hours back each run re-folds, so late minute rows reach the hour tier. |

## API

Host: `Keryhe.Telemetry.Api.Server` (REST API, background workers and the UI). Uses `Database:Provider`,
`ControlPlane:Provider`, `ConnectionStrings:Api` and `ConnectionStrings:ControlPlane` above.

```json
{
  "Telemetry": {
    "Api": { "BasePath": "/api" },
    "Query": { "SummaryTimeoutSeconds": 5, "Limits": { "Logs": 1000, "Traces": 500 } },
    "Export": { "MaxConcurrent": 2 },
    "AlertEvaluation": { "Enabled": true, "IntervalSeconds": 60 },
    "Retention": { "Enabled": true, "IntervalSeconds": 3600 }
  },
  "TelemetryUi": { "BasePath": "/" },
  "Cors": { "AllowedOrigins": ["http://localhost:4201"] }
}
```

### Routes and authorization (`Telemetry:Api`)

API host. `TelemetryApiOptions`; full detail in [src/Keryhe.Telemetry.Api/README.md](../src/Keryhe.Telemetry.Api/README.md).

| Key | Default | Description |
|---|---|---|
| `BasePath` | `/api` | Prefix of every API route. `/` and empty are rejected. The in-repo hosts set `/telemetry/api`. |
| `Authorization:Enabled` | false | Turns on operation and tenant authorization. |
| `Authorization:Policies:{Read\|Admin\|Export\|ManageAlerts\|ManageSettings}` | unset | Name of a host-registered policy per operation. Export falls back to Read, ManageAlerts/ManageSettings to Admin, Read/Admin to the default policy. |
| `Authorization:TenantMappings` | `[]` | List of `{ ClaimType, ClaimValue, Tenants }`: callers with that claim may access those tenants (names, ids or `"*"`). |

### Read queries (`Telemetry:Query`)

API host. `QueryOptions`.

| Key | Default | Description |
|---|---|---|
| `SummaryTimeoutSeconds` | 5 | Budget for a summary query (the rollup reads behind the cards and charts, the slow-request count, the dashboard's trace samples, the logs facets, metric series and exemplars). Past it the cards and charts report "timed out". The database driver's own command timeout is set 2 seconds longer, so the budget is what ends the query. `0` expires every such query before it starts (useful only for testing the fallbacks). |
| `RawSearchWindowHoursOverride` | unset (24) | Longest window a free-text/attribute search or `mode=slow` may cover; wider is a `400`. |
| `AnchorLookbackMinutes` | 5 | How far before a trace-list window the anchor derivation looks, so a trace that began just before the window is not listed on a later span. |
| `PageSliceSeconds` | 2 | First slice of trace start times a trace-list page scans (relational providers). |
| `PageSliceGrowth` | 4 | Factor each further slice widens by (minimum 2). |
| `TraceHintMarginMinutes` | 1 | Margin either side of a trace-detail `?start=&end=` hint (ClickHouse). |
| `TraceHintEnabled` | true | `false` ignores every trace-detail time hint. |

### List caps (`Telemetry:Query:Limits`)

API host. `QueryLimitsOptions`. The most rows each list returns; a request's `limit` may ask for fewer, never more. A list that
matched more says `truncated` and the UI suggests narrowing the time range or adding filters. Each must be a positive integer, or
the host fails at startup naming the key. `GET {base}/capabilities` reports them as `logListLimit`, `traceListLimit`,
`metricCatalogLimit` and `exemplarLimit`. Export is not capped.

| Key | Default | Description |
|---|---|---|
| `Logs` | 1000 | `GET .../logs/list` |
| `Traces` | 500 | `GET .../traces/list` |
| `MetricCatalog` | 500 | `GET .../metrics/catalog`, in either grouping |
| `Exemplars` | 500 | `GET .../metrics/exemplars` |

### Export (`Telemetry:Export`)

API host. `ExportOptions`.

| Key | Default | Description |
|---|---|---|
| `MaxWindowDaysOverride` | unset (7 on PostgreSQL/ClickHouse, 1 on SQL Server/MySQL) | Widest window an export may cover; wider is a `400`. |
| `MaxConcurrent` | 2 | Exports streaming at once per API instance; beyond it a request gets `429`. |

### Alert evaluation (`Telemetry:AlertEvaluation`)

API host (`AddAlerting`). `AlertingOptions`.

| Key | Default | Description |
|---|---|---|
| `Enabled` | true | When false the evaluation worker registers but never runs. |
| `IntervalSeconds` | 60 | Interval between evaluation cycles. |

### Retention (`Telemetry:Retention`)

API host (`AddRetention`). `RetentionOptions`. How many days to keep is not configuration: it is the
`retention_settings` row, edited on the Settings page.

| Key | Default | Description |
|---|---|---|
| `Enabled` | true | When false the retention worker registers but never sweeps. |
| `IntervalSeconds` | 3600 | Interval between sweeps. |

### UI (`TelemetryUi`)

API host (`AddKeryheTelemetryUi`). `TelemetryUiOptions`; full detail in
[src/Keryhe.Telemetry.Ui/README.md](../src/Keryhe.Telemetry.Ui/README.md). Read once at startup.

| Key | Default | Description |
|---|---|---|
| `BasePath` | `/` | Where the UI is mounted. A non-root value makes `GET /` a 404; a reverse proxy must forward the prefix. The in-repo hosts set `/telemetry`. |
| `ApiBasePath` | `Telemetry:Api:BasePath`, else `/api` | Where the browser finds the API; may be an absolute URL on another origin. |
| `BrandName` | `Sentinel` | Product name in the header and browser tab. |
| `BrandTagline` | `OpenTelemetry Visualization` | Tagline under the brand name. |
| `HealthThresholds:ErrorRate:Warn` | unset (0.01 in the SPA) | Dashboard Error Rate warning threshold, a 0-1 fraction. |
| `HealthThresholds:ErrorRate:Critical` | unset (0.05 in the SPA) | Error threshold; must be above `Warn`. |
| `Auth:Mode` | `cookie` | `cookie` (the host owns sign-in) or `oidc` (the SPA signs in itself and sends a bearer token). |
| `Auth:LoginUrl` | unset | Where the SPA sends the browser on a `401` (cookie mode). |
| `Auth:LogoutUrl` | unset | Sign-out link (cookie mode). |
| `Auth:IncludeCredentials` | false | Send cookies on cross-origin API calls. |
| `Auth:Oidc:Authority` | unset | OIDC issuer (oidc mode). |
| `Auth:Oidc:ClientId` | unset | OIDC client id (oidc mode). |
| `Auth:Oidc:Scope` | `openid profile` | Requested scopes (oidc mode). |

### CORS (`Cors`)

| Key | Default | Description |
|---|---|---|
| `Cors:AllowedOrigins` | `["http://localhost:4201"]` | Origins allowed to call the API from a browser. Only matters for a UI on a different origin from its API. |

## Other tools

### Test data generator (`Generator`)

`Keryhe.Telemetry.TestDataGenerator` only (`GeneratorOptions`); its [appsettings.json](../src/Keryhe.Telemetry.TestDataGenerator/appsettings.json)
sets every value and is the working example. API keys go in User Secrets, never appsettings.

| Key | Default | Description |
|---|---|---|
| `OtlpEndpoint` | `http://localhost:5117` | Collector to send to: its gRPC address, or with `Protocol` `http/protobuf` its base address (`http://localhost:5118` in Development; the SDK is given `/v1/traces`, `/v1/logs` and `/v1/metrics` under it). |
| `Protocol` | `grpc` | The transport live data uses: `grpc` or `http/protobuf`. Backfill is always gRPC and shares `OtlpEndpoint`, so the two cannot be combined in one run (backfill first, then run live over HTTP). |
| `Seed` | 42 | Seed for every random decision; the same seed and time range give the same telemetry. |
| `PeakRequestsPerSecond` | 2 | Traffic at the daily peak, before each tenant's `Scale`. |
| `Environment` | `production` | `deployment.environment` resource attribute. |
| `PodsPerService` | 2 | Simulated pods (resources) per service. |
| `LiveMetricIntervalSeconds` | 15 | Metric export interval when live. |
| `LiveLagSeconds` | 10 | How far behind the clock live emission runs, so requests have finished. |
| `Live` | true | Keep emitting live after any backfill. |
| `Backfill:Enabled` | false | Send history first (hand-built OTLP). Re-running over the same window stores it twice. |
| `Backfill:Window` | `1.00:00:00` (24 h) | How much history. |
| `Backfill:ChunkSeconds` | 60 | Simulated time per chunk (one metric interval). |
| `Backfill:MaxSpansPerExport` | 2000 | Spans per OTLP export during backfill. |
| `Tenants` | `[]` | List of `{ Name, ApiKey, Scale }`; `ApiKey` must already exist in the database. |
| `Incidents` | `[]` | Recurring daily windows: `{ Name, Tenant, Service, Route, At, Duration, LatencyMultiplier, ExtraErrorRate, Version }`. |
