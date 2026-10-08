# ClickHouse row model (redesign step 3)

Designed 2026-10-08 from the OTLP data shape and ClickHouse practice, without reference to the other providers or the
current ClickHouse schema. The targets it must meet are in `plans/clickhouse-diagnosis-results.md` ("Redesign targets"):
100,000 records/s on 4 CPU / 8 GB with the database under about 60% CPU, at most 5 s to queryable, tiered read p95s, one
node, day-partition retention, late data accepted, collector-side batching.

This is the row model only. How the collector, the read repositories and the plan steps change comes in step 4.

It assumes `plans/list-caps.md` has landed first: the trace list, logs list, metrics catalog and exemplars return at
most N rows (`truncated` when there were more), newest or oldest first, with no cursors and no `asOf` pin. Nothing
here carries a column only for paging.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | ClickHouse version | 25.8 LTS |
| 2 | Resources and scopes | Copied onto every row (attributes, schema URL, scope name/version). No reference tables, no surrogate ids |
| 3 | Attributes | `Map(LowCardinality(String), String)` holds the full set. A fixed list of common semantic-convention keys is also promoted to real typed columns (`MATERIALIZED` from the map) |
| 4 | Trace and span ids | Raw bytes: `FixedString(16)` / `FixedString(8)`; `hex()` / `unhex()` at the edges |
| 5 | Timestamps | `DateTime64(9, 'UTC')`; span duration as `UInt64` nanoseconds |
| 6 | Duplicates | Plain `MergeTree`. Each insert carries an `insert_deduplication_token` that is reused on retry; tables set `non_replicated_deduplication_window`. A client re-send in a new export is still stored twice; reads tolerate it |
| 7 | Span sort key | `(tenant_id, service_name, start_time)`; operation filters use a skip index on `span_name` |
| 8 | Events and links | Arrays on the span row (`Nested`) |
| 9 | Trace lookup | `trace_index` table, written by the collector, one row per (trace, service, day), sorted by `(tenant_id, trace_id, service_name)` |
| 10 | Log sort key | `(tenant_id, toStartOfFiveMinutes(timestamp), service_name, timestamp)`, the OpenTelemetry exporter's shape with tenant first. No row id: lists are capped, so nothing needs a paging tiebreak |
| 11 | Log search | `tokenbf_v1` skip index on `lower(body)` |
| 12 | Metrics | One table per type; sort key `(tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)`; `series_id` is a 64-bit hash the collector computes |
| 13 | Derived data | Written by the collector after the raw insert succeeds. No materialized views on any insert path |
| 14 | Request rollup | Per `(tenant, service, operation, minute)`, inbound spans only, 24 fixed duration bands |
| 15 | Rollup tiers | Minute only; add an hour tier only if 7-day reads miss 3 s |
| 16 | Metric catalog | Two tables: metrics, and series with their attributes |
| 17 | Partitioning | `toDate(<time>)` on every time-series table, raw and derived. Retention is `DROP PARTITION` per expired day |
| 18 | Trace list anchors | Keep the anchor meaning (earliest span in scope). Candidates come from one time slice of `spans`; `trace_index`'s per-service minimum start confirms them. Lists are capped (no paging), so no pin is needed |

Choices I made without asking (small, easy to change; say if any is wrong):
- `tenant_id` is `UInt64`. The control plane's ids are signed `BIGINT` but always positive, so every id fits. It costs nothing after compression.
- Rollups sort by minute before operation or severity: `(tenant_id, service_name, minute, operation)`. The main read
  (a service, or all services, over a window) is then one contiguous range, and these tables are small enough that a
  per-operation read filtering inside that range is cheap.
- Absent optional numbers (histogram sum/min/max) are `Nullable(Float64)`. These are the only `Nullable` columns.
- Enums for span kind and status code; `LowCardinality(String)` for names, severity text, units and schema URLs.

## Conventions on every table

- `tenant_id UInt64` leads every sort key. 100 tenants don't belong in the partition key: that would multiply the part
  count by 100.
- Every time-series table is `PARTITION BY toDate(<its time column>)`. The collector splits each batch by day, so the
  current day goes in as one large insert and older days are buffered and flushed less often (target decision "Late
  data").
- Codecs: timestamps `CODEC(Delta(8), ZSTD(1))`; durations and counters `CODEC(T64, ZSTD(1))`; strings and maps
  `ZSTD(1)`; ids keep the default LZ4 (random bytes don't compress).
- `SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000`.
- Attribute columns are `Map(LowCardinality(String), String)`. Keys are a small, repeating set, so `LowCardinality`
  keys cost almost nothing.
- Map values are strings. OTLP `AnyValue` is written as: string as-is; int, double and bool as their text (`42`,
  `0.5`, `true`); array and kvlist as JSON text; bytes as base64. The value's original type isn't kept, so a numeric
  comparison casts (`toFloat64OrNull(attributes['k'])`).
- Attribute maps on spans and logs carry two bloom filter skip indexes, one on `mapKeys(attributes)` and one on
  `mapValues(attributes)`. They let an exact `key:value` search skip granules that can't match.

### Promoted columns

Common semantic-convention keys are also stored as real columns, computed by ClickHouse from the map at insert time
(`MATERIALIZED`). The map stays complete, so display and generic search don't care which keys are promoted. The
collector doesn't send these columns, so they can't drift from the map.

| Column | Type | Source key (first non-empty) | Tables |
|---|---|---|---|
| `service_namespace` | `LowCardinality(String)` | resource `service.namespace` | spans, logs |
| `service_version` | `LowCardinality(String)` | resource `service.version` | spans, logs |
| `deployment_environment` | `LowCardinality(String)` | resource `deployment.environment.name`, `deployment.environment` | spans, logs |
| `host_name` | `LowCardinality(String)` | resource `host.name` | spans, logs |
| `http_method` | `LowCardinality(String)` | `http.request.method`, `http.method` | spans |
| `http_route` | `LowCardinality(String)` | `http.route` | spans |
| `http_status_code` | `UInt16` | `http.response.status_code`, `http.status_code` (0 when absent) | spans |
| `server_address` | `LowCardinality(String)` | `server.address` | spans |
| `db_system` | `LowCardinality(String)` | `db.system.name`, `db.system` | spans |
| `db_operation` | `LowCardinality(String)` | `db.operation.name`, `db.operation` | spans |
| `rpc_service` | `LowCardinality(String)` | `rpc.service` | spans |
| `rpc_method` | `LowCardinality(String)` | `rpc.method` | spans |
| `messaging_system` | `LowCardinality(String)` | `messaging.system` | spans |
| `messaging_destination` | `LowCardinality(String)` | `messaging.destination.name` | spans |
| `error_type` | `LowCardinality(String)` | `error.type` | spans |
| `exception_type` | `LowCardinality(String)` | `exception.type` | logs |

The fallback keys cover the older semantic-convention names that some SDKs still send. The metric tables promote
nothing: a chart reads by `series_id`, and the metric catalog holds the attributes for pickers.

To promote another key later, run `ALTER TABLE ... ADD COLUMN ... MATERIALIZED ...`, then `MATERIALIZE COLUMN` to fill
existing parts. No collector change is needed.

## Spans

```sql
CREATE TABLE spans
(
    tenant_id                 UInt64,
    service_name              LowCardinality(String),
    span_name                 LowCardinality(String),
    start_time                DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    duration_ns               UInt64 CODEC(T64, ZSTD(1)),
    trace_id                  FixedString(16),
    span_id                   FixedString(8),
    parent_span_id            FixedString(8),          -- all zero bytes for a root
    trace_state               String CODEC(ZSTD(1)),
    flags                     UInt32,
    kind                      Enum8('UNSPECIFIED' = 0, 'INTERNAL' = 1, 'SERVER' = 2, 'CLIENT' = 3, 'PRODUCER' = 4, 'CONSUMER' = 5),
    status_code               Enum8('UNSET' = 0, 'OK' = 1, 'ERROR' = 2),
    status_message            String CODEC(ZSTD(1)),
    attributes                Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    dropped_attributes_count  UInt32,
    events Nested
    (
        time                     DateTime64(9, 'UTC'),
        name                     LowCardinality(String),
        attributes               Map(LowCardinality(String), String),
        dropped_attributes_count UInt32
    ),
    dropped_events_count      UInt32,
    links Nested
    (
        trace_id                 FixedString(16),
        span_id                  FixedString(8),
        trace_state              String,
        flags                    UInt32,
        attributes               Map(LowCardinality(String), String),
        dropped_attributes_count UInt32
    ),
    dropped_links_count       UInt32,
    resource_attributes       Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    resource_schema_url       LowCardinality(String),
    scope_name                LowCardinality(String),
    scope_version             LowCardinality(String),
    scope_attributes          Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    scope_schema_url          LowCardinality(String),

    -- promoted (see "Promoted columns"); two shown, the rest follow the same pattern
    service_version           LowCardinality(String) MATERIALIZED resource_attributes['service.version'],
    http_status_code          UInt16 MATERIALIZED toUInt16OrZero(
                                  if(attributes['http.response.status_code'] != '',
                                     attributes['http.response.status_code'], attributes['http.status_code'])),
    -- ...

    INDEX idx_trace_id    trace_id                 TYPE bloom_filter(0.001) GRANULARITY 1,
    INDEX idx_span_name   span_name                TYPE bloom_filter(0.01) GRANULARITY 1,
    INDEX idx_duration    duration_ns              TYPE minmax             GRANULARITY 1,
    INDEX idx_status      status_code              TYPE set(3)             GRANULARITY 1,
    INDEX idx_kind        kind                     TYPE set(6)             GRANULARITY 1,
    INDEX idx_attr_keys   mapKeys(attributes)      TYPE bloom_filter(0.01) GRANULARITY 1,
    INDEX idx_attr_values mapValues(attributes)    TYPE bloom_filter(0.01) GRANULARITY 1
)
ENGINE = MergeTree
PARTITION BY toDate(start_time)
ORDER BY (tenant_id, service_name, start_time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;
```

- The end time is `start_time + duration_ns`. A stored duration means "slow" filters and the duration skip index don't
  need an expression.
- A service and time window is one contiguous range; a window over all services is one range per service.
- An operation filter (`span_name = ...`) reads inside that range and skips granules through the `span_name` bloom
  filter. Operation stats come from the request rollup, not from spans.
- Lists order by `(start_time, span_id)` so ties are deterministic.
- The `trace_id` bloom filters (here and on `log_records`) use a 0.001 false-positive rate, as the OpenTelemetry
  exporter does: a little larger than 0.01, but trace detail and logs-by-trace skip more granules. The other bloom
  filters stay at 0.01.

## Trace index

```sql
CREATE TABLE trace_index
(
    tenant_id     UInt64,
    trace_id      FixedString(16),
    service_name  LowCardinality(String),
    day           Date,
    start_min     SimpleAggregateFunction(min, DateTime64(9, 'UTC')),
    end_max       SimpleAggregateFunction(max, DateTime64(9, 'UTC')),
    span_count    SimpleAggregateFunction(sum, UInt64),
    has_error     SimpleAggregateFunction(max, UInt8)
)
ENGINE = AggregatingMergeTree
PARTITION BY day
ORDER BY (tenant_id, trace_id, service_name)
SETTINGS non_replicated_deduplication_window = 1000;
```

- One row per (trace, service, day), so `start_min` is that **service's** earliest span start in the trace. The whole
  trace's values are the `min()` / `max()` / `sum()` over its service rows. That's about 3 rows per trace for a trace
  touching three services, still far smaller than `spans`.
- The collector writes the rows from each batch after the span insert succeeds. Merges fold the partial rows. Reads use
  `min()` / `max()` / `sum()` with `GROUP BY`, never `FINAL`.
- **Trace detail** reads by the `(tenant_id, trace_id)` prefix, takes the overall min and max, then reads `spans` within
  that range with the bloom filter on `trace_id`. Adding `service_name` after `trace_id` doesn't change that read.
- A trace spanning midnight has rows in each day. A lookup with no time hint probes each day partition's primary
  index: about 90 cheap binary searches at a 90-day window.
- `has_error` per service gives the trace list's error flag for either scope without reading spans.
- `span_count` counts re-delivered spans twice; it's a size hint, not the displayed count.

## Trace list anchors

The trace list shows one row per trace, represented by its **anchor**: the earliest span in scope, ranked by
`(start_time, span_id)`. With all services, scope is the whole trace. With a service selected, it's that service's spans
(any kind), so a trace appears under each service it touches. The row's service, operation, kind and duration are the
anchor span's own. This handles traces with no root or a late root.

Finding anchors (decision 18):

1. Read one **slice** of spans by start time, from the window's end for newest first or its start for oldest first
   (the first slice is a few seconds; widen it until there are `limit + 1` anchors, which is how `truncated` is
   known). With a service selected that's one range of `spans`; with all services, one per service.
2. Within the slice, take each trace's earliest in-scope span: `argMin` over `(start_time, span_id)` with
   `GROUP BY trace_id`.
3. Look those trace ids up in `trace_index` (the slice's day and the day before). A candidate is the anchor if its start
   equals the trace's `start_min`: the minimum over all the trace's service rows with all services, or that service's
   row with a service selected.
4. Keep the anchors; a candidate whose trace started earlier is not its trace's anchor (newest first: that trace's
   anchor lies in an older slice, or before the window).

Reads follow the list limit, not the window: one slice of spans plus point lookups into a table sorted by trace id. There's
no 5-minute look-back margin; the anchor is the trace's true earliest span (within the two days the lookup covers).

**No pin.** A list is one request, so `trace_index` needs no `asOf`. A span arriving between two slices of the same
request could, in principle, move a trace's anchor; that window is a few milliseconds and is ignored.

## Log records

```sql
CREATE TABLE log_records
(
    tenant_id                 UInt64,
    service_name              LowCardinality(String),
    timestamp                 DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    observed_timestamp        DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    trace_id                  FixedString(16),         -- all zero bytes when absent
    span_id                   FixedString(8),
    flags                     UInt32,
    severity_number           UInt8,                   -- OTLP 0 = unspecified
    severity_text             LowCardinality(String),
    event_name                LowCardinality(String),
    body                      String CODEC(ZSTD(1)),   -- a non-string AnyValue body is stored as its JSON text
    attributes                Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    dropped_attributes_count  UInt32,
    resource_attributes       Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    resource_schema_url       LowCardinality(String),
    scope_name                LowCardinality(String),
    scope_version             LowCardinality(String),
    scope_attributes          Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    scope_schema_url          LowCardinality(String),

    -- promoted (see "Promoted columns"): service_namespace, service_version, deployment_environment, host_name,
    -- exception_type, each MATERIALIZED from its map

    INDEX idx_body        lower(body)            TYPE tokenbf_v1(32768, 3, 0) GRANULARITY 1,
    INDEX idx_trace_id    trace_id               TYPE bloom_filter(0.001)     GRANULARITY 1,
    INDEX idx_severity    severity_number        TYPE minmax                  GRANULARITY 1,
    INDEX idx_attr_keys   mapKeys(attributes)    TYPE bloom_filter(0.01)      GRANULARITY 1,
    INDEX idx_attr_values mapValues(attributes)  TYPE bloom_filter(0.01)      GRANULARITY 1
)
ENGINE = MergeTree
PARTITION BY toDate(timestamp)
ORDER BY (tenant_id, toStartOfFiveMinutes(timestamp), service_name, timestamp)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;
```

- **Why time comes before service.** The default logs page is the newest records across all services. With the
  five-minute bucket right after the tenant, a query ordered by `toStartOfFiveMinutes(timestamp) DESC, timestamp DESC`
  matches the sort key, so ClickHouse reads in order (`optimize_read_in_order`) from the newest bucket and stops once
  it has `limit + 1` rows. With service ahead of time, it would have to read and sort the whole window: about 86 million rows
  for one tenant's 24 hours at the target load.
- **The cost falls on small services.** A selected service is one short range per bucket. A service with a small
  share of the traffic may fill less than a granule per bucket and read some of its neighbours' rows. At about 1,000
  records/s per tenant, a service with 1% of the traffic reads roughly 3× what it needs. That's bounded, and it's
  acceptable for a much cheaper default view.
- Spans keep service ahead of time: the trace list groups spans by trace over a time slice, so it never reads raw spans
  in time order, and a selected service stays one exact range.
- There's no row id. Lists are capped and ordered by `timestamp` (ties in no particular order), and nothing in the UI
  identifies a log row by id (`plans/list-caps.md`, Phase 4). That saves 8 random, incompressible bytes per row.
- Oldest first reads the buckets in ascending order; reading in order works in both directions.
- Whole-word search terms use the token index. A substring search (part of a word) scans the 24 h search window. The
  index size (32 KB per granule) is a starting point to tune in the spike.
- A record with no timestamp uses `observed_timestamp`, as OTLP specifies.

## Metric data points

Every table has these columns. Each table also adds its type's own value columns, listed below.

```sql
    tenant_id            UInt64,
    service_name         LowCardinality(String),
    metric_name          LowCardinality(String),
    series_id            UInt64,
    time                 DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    start_time           DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    metric_unit          LowCardinality(String),
    metric_description   LowCardinality(String),
    flags                UInt32,
    attributes           Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    resource_attributes  Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    resource_schema_url  LowCardinality(String),
    scope_name           LowCardinality(String),
    scope_version        LowCardinality(String),
    scope_attributes     Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    scope_schema_url     LowCardinality(String),
    ...type columns...
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;
```

| Table | Type columns |
|---|---|
| `gauge_points` | `value Float64`, `exemplars` |
| `sum_points` | `value Float64`, `temporality Enum8('UNSPECIFIED'=0,'DELTA'=1,'CUMULATIVE'=2)`, `is_monotonic Bool`, `exemplars` |
| `histogram_points` | `count UInt64`, `sum Nullable(Float64)`, `min Nullable(Float64)`, `max Nullable(Float64)`, `bucket_counts Array(UInt64)`, `explicit_bounds Array(Float64)`, `temporality`, `exemplars` |
| `exp_histogram_points` | `count UInt64`, `sum`, `min`, `max` (Nullable), `scale Int32`, `zero_count UInt64`, `zero_threshold Float64`, `positive_offset Int32`, `positive_bucket_counts Array(UInt64)`, `negative_offset Int32`, `negative_bucket_counts Array(UInt64)`, `temporality`, `exemplars` |
| `summary_points` | `count UInt64`, `sum Float64`, `quantiles Nested(quantile Float64, value Float64)` |

`exemplars` is `Nested(time DateTime64(9,'UTC'), value Float64, trace_id FixedString(16), span_id FixedString(8),
filtered_attributes Map(LowCardinality(String), String))`.

- `series_id` = 64-bit hash over (tenant, resource attributes, scope name/version/attributes, metric name, type, point
  attributes), computed by the collector with keys sorted so key order doesn't matter. Charts `GROUP BY series_id` instead of
  grouping on a map. (A `Map` can't be part of a sort key anyway.)
- The hour bucket comes before `series_id` (as in the OpenTelemetry exporter's layout). The common chart is every series
  of one metric over a window, which then reads one contiguous range per hour, however many series the metric has.
  With `series_id` first it would read one range per series. A single-series read still narrows to that series inside
  each hour.
- OTLP integer values (`as_int`) are stored as `Float64`. That's exact up to 2^53, which covers practical counters.
  Flag it if you have counters beyond that.
- Unit and description repeat on every row (`LowCardinality`, nearly free) so a chart needs no catalog lookup.

## Metric catalog

```sql
CREATE TABLE metric_catalog
(
    tenant_id     UInt64,
    service_name  LowCardinality(String),
    metric_name   LowCardinality(String),
    metric_type   Enum8('GAUGE'=1,'SUM'=2,'HISTOGRAM'=3,'EXPONENTIAL_HISTOGRAM'=4,'SUMMARY'=5),
    unit          SimpleAggregateFunction(anyLast, LowCardinality(String)),
    description   SimpleAggregateFunction(anyLast, String),
    first_seen    SimpleAggregateFunction(min, DateTime64(9, 'UTC')),
    last_seen     SimpleAggregateFunction(max, DateTime64(9, 'UTC'))
)
ENGINE = AggregatingMergeTree
ORDER BY (tenant_id, service_name, metric_name, metric_type);

CREATE TABLE metric_series
(
    tenant_id            UInt64,
    service_name         LowCardinality(String),
    metric_name          LowCardinality(String),
    series_id            UInt64,
    attributes           Map(LowCardinality(String), String),
    resource_attributes  Map(LowCardinality(String), String),
    scope_name           LowCardinality(String),
    scope_version        LowCardinality(String),
    first_seen           DateTime64(9, 'UTC'),
    last_seen            DateTime64(9, 'UTC')
)
ENGINE = ReplacingMergeTree(last_seen)
ORDER BY (tenant_id, service_name, metric_name, series_id);
```

- The collector writes a series row the first time its process sees the series, then at most once every 5 minutes
  while it keeps reporting. So `last_seen` is accurate to about 5 minutes, which suits "has data in this range" and
  attribute pickers. The catalog is written at the same pace.
- `metric_series` reads use `FINAL` or `argMax(..., last_seen)`; the table is small (one row per series).
- Neither table is partitioned, since they hold one row per metric or series, not per point. Retention removes rows
  whose `last_seen` is older than the metrics window with one lightweight `DELETE` per sweep.

## Summary rollups

```sql
CREATE TABLE request_rollup_minute
(
    tenant_id       UInt64,
    service_name    LowCardinality(String),
    minute          DateTime('UTC'),
    operation       LowCardinality(String),
    request_count   SimpleAggregateFunction(sum, UInt64),
    error_count     SimpleAggregateFunction(sum, UInt64),
    duration_sum_ns SimpleAggregateFunction(sum, UInt64),
    duration_max_ns SimpleAggregateFunction(max, UInt64),
    b00 SimpleAggregateFunction(sum, UInt64),
    -- ... b01 through b22 ...
    b23 SimpleAggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY toDate(minute)
ORDER BY (tenant_id, service_name, minute, operation)
SETTINGS non_replicated_deduplication_window = 1000;

CREATE TABLE log_rollup_minute
(
    tenant_id        UInt64,
    service_name     LowCardinality(String),
    minute           DateTime('UTC'),
    severity_number  UInt8,
    record_count     SimpleAggregateFunction(sum, UInt64)
)
ENGINE = AggregatingMergeTree
PARTITION BY toDate(minute)
ORDER BY (tenant_id, service_name, minute, severity_number)
SETTINGS non_replicated_deduplication_window = 1000;
```

- Requests are inbound spans (kind SERVER or CONSUMER), and `operation` is the span name.
- Duration bands double: band 0 is under 0.25 ms, band N is [0.25 ms × 2^(N−1), 0.25 ms × 2^N), and band 23 is everything
  from about 1,049 s. Percentiles interpolate within a band, so they're approximate, up to 2× in the worst case.
- The collector folds each successful batch into partial rows and inserts them right after the raw insert, with a
  dedup token derived from the raw batch's token. That way a retried batch can't double-count. A collector crash
  between the two inserts under-counts that batch in the charts; the raw rows are intact.
- Reads `sum()` / `max()` with `GROUP BY`; merges compact the partial rows over time.
- A late span for an earlier minute is simply one more partial row for that minute.

## What reads use

| Page or read | Reads |
|---|---|
| Dashboard, trace-list and logs cards and charts, error-rate and log-spike alerts | The two rollups |
| Trace list (service + window) | One slice of `spans` (one range), then `trace_index` lookups for the candidates (see "Trace list anchors"). Error flag from `trace_index.has_error` |
| Trace list (all services) | The same, with one range per service in the slice |
| Operation stats | `request_rollup_minute` filtered by operation |
| Slow requests | `spans` with the `duration_ns` index, inbound kinds |
| Trace detail | `trace_index` for the bounds (or the link's hint), then `spans` within them, using the `trace_id` bloom filter |
| Logs list and search | `log_records`: newest five-minute buckets first, stopping after `limit + 1` rows (oldest first reads them ascending); a selected service is one short range per bucket. Token index for words, `trace_id` index for logs-by-trace |
| Metrics page, attribute pickers | `metric_catalog`, `metric_series` |
| Metric charts | One points table: one range per hour of the window, narrowed by `series_id` when one series is selected |

## Spike before the plan (step 4)

These could change a decision above, so each gets a short test on 25.8 first:

1. **The .NET client.** Confirm the driver can bulk-write `Map(LowCardinality(String), String)`, `FixedString`,
   `Nested` (including maps inside it) and `DateTime64(9)`, with an `insert_deduplication_token` per insert.
2. **Insert cost at the target.** Measure database CPU per row at 100,000 records/s with this layout: maps,
   `MATERIALIZED` promoted columns and the attribute bloom filters, against the 60% budget. If the map bloom
   filters cost too much, they're the first thing to drop.
3. **Dedup tokens across day splits.** Confirm a retried multi-partition insert is fully dropped.
4. **Token index sizing** on realistic log bodies: false-positive rate against index size.
5. **Logs read in order.** Check with `EXPLAIN PIPELINE` and `system.query_log` (rows read) that the newest-logs list
   (and the oldest-first list) reads in sort order and stops after `limit + 1` rows, including with a service filter
   and a severity filter, rather than reading the whole window.

## Risks

- **Attribute value types are lost.** Map values are strings, so numeric attribute filters cast at read time, and a
  value of `"42"` and `42` look the same. Promoted columns carry the important typed ones (`http_status_code`).
- **The promoted list needs upkeep.** Semantic-convention names change (`db.system` became `db.system.name`); the
  fallback keys cover known renames, and adding a column later is a metadata change plus `MATERIALIZE COLUMN`.
- **Rollup under-count on a crash** between the raw and rollup inserts. This is accepted: the charts are approximate
  in that slice, and raw data and alerts on raw spans are unaffected.
- **Catalog freshness** is about 5 minutes, which suits pickers but not "seen in the last minute".
