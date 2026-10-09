-- OpenTelemetry ClickHouse telemetry schema -- telemetry schema 4.0.0 (ClickHouse 25.8 LTS or later)
-- Fresh install only: there is no upgrade path from any earlier layout (recreate the database).
-- ClickHouse holds telemetry data only: tenants, API keys, alert rules and retention settings are the
-- control plane, which a ClickHouse deployment runs on PostgreSQL, SQL Server or MySQL
-- (PostgreSQL-ControlPlane.sql and its siblings, ControlPlane:Provider).
--
-- Row model: plans/clickhouse-row-model.md. In short:
--   * Every row carries its own resource and scope (attributes, schema URL, name, version): no reference tables,
--     no surrogate ids. Attribute maps are Map(LowCardinality(String), String); common semantic-convention keys
--     are also promoted to MATERIALIZED columns, which the collector never sends.
--   * Trace ids are UUID and span ids UInt64 (the client cannot write raw bytes into FixedString); the app converts
--     to and from the OTLP hex form. Timestamps are DateTime64(9) but the client stores 100 ns resolution;
--     spans.duration_ns is exact.
--   * Raw tables are plain MergeTree appends. Each INSERT carries an insert_deduplication_token that the writer
--     reuses on retry (non_replicated_deduplication_window), so a retried batch is stored once; a client re-send
--     in a new export is stored twice and reads tolerate it.
--   * Every time-series table is PARTITION BY toDate(<time>): retention is DROP PARTITION per expired day.
--   * Derived tables (trace_index, the rollups, metric_catalog, metric_series) are written by the collector after
--     the raw insert succeeds. There are no materialized views on any insert path.
--   * tenant_id leads every sort key. A time-range read on log_records must also carry a
--     toStartOfFiveMinutes(timestamp) predicate or the sort key does not prune.

-- spans: trace detail (trace_id bloom filter inside the bounds from trace_index), the trace list's anchor candidates
-- (service + time range of the sort key), slow filters (duration minmax), operation filters (span_name bloom filter)
-- and exports. Sorted by tenant, service, start so a service and time window is one contiguous range.
CREATE TABLE IF NOT EXISTS spans
(
    tenant_id                 UInt64,
    service_name              LowCardinality(String),
    span_name                 LowCardinality(String),
    start_time                DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    duration_ns               UInt64 CODEC(T64, ZSTD(1)),
    trace_id                  UUID,
    span_id                   UInt64,
    parent_span_id            UInt64,
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
        trace_id                 UUID,
        span_id                  UInt64,
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

    service_namespace        LowCardinality(String) MATERIALIZED resource_attributes['service.namespace'],
    service_version          LowCardinality(String) MATERIALIZED resource_attributes['service.version'],
    deployment_environment   LowCardinality(String) MATERIALIZED if(resource_attributes['deployment.environment.name'] != '', resource_attributes['deployment.environment.name'], resource_attributes['deployment.environment']),
    host_name                LowCardinality(String) MATERIALIZED resource_attributes['host.name'],
    http_method              LowCardinality(String) MATERIALIZED if(attributes['http.request.method'] != '', attributes['http.request.method'], attributes['http.method']),
    http_route               LowCardinality(String) MATERIALIZED attributes['http.route'],
    http_status_code         UInt16 MATERIALIZED toUInt16OrZero(if(attributes['http.response.status_code'] != '', attributes['http.response.status_code'], attributes['http.status_code'])),
    server_address           LowCardinality(String) MATERIALIZED attributes['server.address'],
    db_system                LowCardinality(String) MATERIALIZED if(attributes['db.system.name'] != '', attributes['db.system.name'], attributes['db.system']),
    db_operation             LowCardinality(String) MATERIALIZED if(attributes['db.operation.name'] != '', attributes['db.operation.name'], attributes['db.operation']),
    rpc_service              LowCardinality(String) MATERIALIZED attributes['rpc.service'],
    rpc_method               LowCardinality(String) MATERIALIZED attributes['rpc.method'],
    messaging_system         LowCardinality(String) MATERIALIZED attributes['messaging.system'],
    messaging_destination    LowCardinality(String) MATERIALIZED attributes['messaging.destination.name'],
    error_type               LowCardinality(String) MATERIALIZED attributes['error.type'],

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

-- trace_index: one row per (trace, service, day) written from each span batch; start_min is that service's earliest
-- span start. Gives trace detail its time bounds and the trace list its anchor confirmation, error flag and span
-- count. Reads aggregate with min()/max()/sum() ... GROUP BY, never FINAL.
CREATE TABLE IF NOT EXISTS trace_index
(
    tenant_id     UInt64,
    trace_id      UUID,
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

-- log_records: the logs list (widening time slices), search (tokenbf_v1 on lower(body), used by hasToken only),
-- facets, logs by trace and exports. The sort key is the OpenTelemetry exporter's shape with tenant first; no row id.
CREATE TABLE IF NOT EXISTS log_records
(
    tenant_id                 UInt64,
    service_name              LowCardinality(String),
    timestamp                 DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    observed_timestamp        DateTime64(9, 'UTC') CODEC(Delta(8), ZSTD(1)),
    trace_id                  UUID,
    span_id                   UInt64,
    flags                     UInt32,
    severity_number           UInt8,
    severity_text             LowCardinality(String),
    event_name                LowCardinality(String),
    body                      String CODEC(ZSTD(1)),
    attributes                Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    dropped_attributes_count  UInt32,
    resource_attributes       Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    resource_schema_url       LowCardinality(String),
    scope_name                LowCardinality(String),
    scope_version             LowCardinality(String),
    scope_attributes          Map(LowCardinality(String), String) CODEC(ZSTD(1)),
    scope_schema_url          LowCardinality(String),

    service_namespace        LowCardinality(String) MATERIALIZED resource_attributes['service.namespace'],
    service_version          LowCardinality(String) MATERIALIZED resource_attributes['service.version'],
    deployment_environment   LowCardinality(String) MATERIALIZED if(resource_attributes['deployment.environment.name'] != '', resource_attributes['deployment.environment.name'], resource_attributes['deployment.environment']),
    host_name                LowCardinality(String) MATERIALIZED resource_attributes['host.name'],
    exception_type           LowCardinality(String) MATERIALIZED attributes['exception.type'],

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

-- Metric point tables (one per type): a chart reads one metric over a time range, grouped by series_id (a 64-bit hash
-- of the attribute set computed by the collector). The promoted columns are deliberately absent: metric_series holds
-- the attributes for pickers.
CREATE TABLE IF NOT EXISTS gauge_points
(
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
    value Float64,
    exemplars Nested
    (
        time                DateTime64(9, 'UTC'),
        value               Float64,
        trace_id            UUID,
        span_id             UInt64,
        filtered_attributes Map(LowCardinality(String), String)
    )
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;

CREATE TABLE IF NOT EXISTS sum_points
(
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
    value Float64,
    temporality Enum8('UNSPECIFIED'=0,'DELTA'=1,'CUMULATIVE'=2),
    is_monotonic Bool,
    exemplars Nested
    (
        time                DateTime64(9, 'UTC'),
        value               Float64,
        trace_id            UUID,
        span_id             UInt64,
        filtered_attributes Map(LowCardinality(String), String)
    )
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;

CREATE TABLE IF NOT EXISTS histogram_points
(
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
    count UInt64,
    sum Nullable(Float64),
    min Nullable(Float64),
    max Nullable(Float64),
    bucket_counts Array(UInt64),
    explicit_bounds Array(Float64),
    temporality Enum8('UNSPECIFIED'=0,'DELTA'=1,'CUMULATIVE'=2),
    exemplars Nested
    (
        time                DateTime64(9, 'UTC'),
        value               Float64,
        trace_id            UUID,
        span_id             UInt64,
        filtered_attributes Map(LowCardinality(String), String)
    )
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;

CREATE TABLE IF NOT EXISTS exp_histogram_points
(
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
    count UInt64,
    sum Nullable(Float64),
    min Nullable(Float64),
    max Nullable(Float64),
    scale Int32,
    zero_count UInt64,
    zero_threshold Float64,
    positive_offset Int32,
    positive_bucket_counts Array(UInt64),
    negative_offset Int32,
    negative_bucket_counts Array(UInt64),
    temporality Enum8('UNSPECIFIED'=0,'DELTA'=1,'CUMULATIVE'=2),
    exemplars Nested
    (
        time                DateTime64(9, 'UTC'),
        value               Float64,
        trace_id            UUID,
        span_id             UInt64,
        filtered_attributes Map(LowCardinality(String), String)
    )
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;

CREATE TABLE IF NOT EXISTS summary_points
(
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
    count UInt64,
    sum Float64,
    quantiles Nested(quantile Float64, value Float64)
)
ENGINE = MergeTree
PARTITION BY toDate(time)
ORDER BY (tenant_id, service_name, metric_name, toStartOfHour(time), series_id, time)
SETTINGS index_granularity = 8192, non_replicated_deduplication_window = 1000;

-- metric_catalog: one row per (tenant, service, metric, type); the metrics list reads it instead of scanning points.
CREATE TABLE IF NOT EXISTS metric_catalog
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

-- metric_series: one row per series with its attributes, for label pickers and series names.
CREATE TABLE IF NOT EXISTS metric_series
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

-- request_rollup_minute: inbound spans (SERVER/CONSUMER) per (tenant, service, minute, operation) with 24 doubling
-- duration bands (see DurationBands). Column names are shared with the relational providers' rollup tables.
-- Rows are partial: reads sum them.
CREATE TABLE IF NOT EXISTS request_rollup_minute
(
    tenant_id              UInt64,
    service_name           LowCardinality(String),
    bucket_start_unix_nano Int64,
    operation              LowCardinality(String),
    request_count          SimpleAggregateFunction(sum, Int64),
    error_count            SimpleAggregateFunction(sum, Int64),
    sum_duration_nanos     SimpleAggregateFunction(sum, Int64),
    max_duration_nanos     SimpleAggregateFunction(max, Int64),
    h00 SimpleAggregateFunction(sum, Int64),
    h01 SimpleAggregateFunction(sum, Int64),
    h02 SimpleAggregateFunction(sum, Int64),
    h03 SimpleAggregateFunction(sum, Int64),
    h04 SimpleAggregateFunction(sum, Int64),
    h05 SimpleAggregateFunction(sum, Int64),
    h06 SimpleAggregateFunction(sum, Int64),
    h07 SimpleAggregateFunction(sum, Int64),
    h08 SimpleAggregateFunction(sum, Int64),
    h09 SimpleAggregateFunction(sum, Int64),
    h10 SimpleAggregateFunction(sum, Int64),
    h11 SimpleAggregateFunction(sum, Int64),
    h12 SimpleAggregateFunction(sum, Int64),
    h13 SimpleAggregateFunction(sum, Int64),
    h14 SimpleAggregateFunction(sum, Int64),
    h15 SimpleAggregateFunction(sum, Int64),
    h16 SimpleAggregateFunction(sum, Int64),
    h17 SimpleAggregateFunction(sum, Int64),
    h18 SimpleAggregateFunction(sum, Int64),
    h19 SimpleAggregateFunction(sum, Int64),
    h20 SimpleAggregateFunction(sum, Int64),
    h21 SimpleAggregateFunction(sum, Int64),
    h22 SimpleAggregateFunction(sum, Int64),
    h23 SimpleAggregateFunction(sum, Int64)
)
ENGINE = AggregatingMergeTree
PARTITION BY toDate(toDateTime(intDiv(bucket_start_unix_nano, 1000000000), 'UTC'))
ORDER BY (tenant_id, service_name, bucket_start_unix_nano, operation)
SETTINGS non_replicated_deduplication_window = 1000;

-- log_rollup_minute: log records per (tenant, service, minute, severity); severity -1 is "none".
CREATE TABLE IF NOT EXISTS log_rollup_minute
(
    tenant_id              UInt64,
    service_name           LowCardinality(String),
    bucket_start_unix_nano Int64,
    severity_number        Int16,
    record_count           SimpleAggregateFunction(sum, Int64)
)
ENGINE = AggregatingMergeTree
PARTITION BY toDate(toDateTime(intDiv(bucket_start_unix_nano, 1000000000), 'UTC'))
ORDER BY (tenant_id, service_name, bucket_start_unix_nano, severity_number)
SETTINGS non_replicated_deduplication_window = 1000;

-- =============================================================================
-- Schema version
-- =============================================================================

CREATE TABLE IF NOT EXISTS telemetry_schema_version
(
    version    String,
    applied_at DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree
ORDER BY version;

-- The telemetry_schema_version row is seeded LAST, so a partial/failed apply never records a version that
-- the apply-schema.sh version gate would wrongly treat as "already applied".
INSERT INTO telemetry_schema_version (version) VALUES ('4.0.0');
