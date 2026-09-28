-- OpenTelemetry SQL Server Schema
-- Translated from PostgreSQL-Schema.sql (TimescaleDB features removed)
--
-- Differences from PostgreSQL schema:
--   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY  ->  BIGINT IDENTITY(1,1) PRIMARY KEY
--   VARCHAR(n)                                       ->  NVARCHAR(n)
--   TEXT                                             ->  NVARCHAR(MAX)
--   DOUBLE PRECISION                                 ->  FLOAT
--   BOOLEAN                                          ->  BIT
--   TIMESTAMPTZ                                      ->  DATETIME2
--   NOW() / CURRENT_TIMESTAMP                        ->  SYSDATETIME()
--   JSONB                                            ->  NVARCHAR(MAX)
--   CHAR(n)                                          ->  CHAR(n)  (unchanged)
--   INTEGER                                          ->  INT
--   ON CONFLICT DO NOTHING                           ->  MERGE ... WHEN NOT MATCHED
--   RETURNING                                        ->  OUTPUT INSERTED.*
--   col ->> 'key'                                    ->  JSON_VALUE(col, '$.key')
--   GIN index on JSONB                               ->  omitted (no equivalent)
--   TimescaleDB hypertables / compression / retention ->  omitted entirely
--
-- Hypertable tables (gauge_data_points, sum_data_points, histogram_data_points,
-- exponential_histogram_data_points, summary_data_points, log_records) had no
-- PRIMARY KEY in PostgreSQL because TimescaleDB disallows unique constraints
-- that exclude the partition column.  In SQL Server they get a normal
-- BIGINT IDENTITY(1,1) PRIMARY KEY.
--
-- Usage:
--   sqlcmd -S <server> -d telemetry -i schema/SqlServer-Schema.sql

-- sqlcmd connects with SET QUOTED_IDENTIFIER OFF by default, but the filtered index
-- (idx_alert_rules_tenant_enabled) requires it ON — otherwise CREATE INDEX fails with
-- Msg 1934 and, because schema_version is already committed above the failure, the
-- apply-schema.sh version gate would wrongly report the schema as applied. Set it (and
-- ANSI_NULLS) ON for the whole session so the script applies cleanly regardless of
-- client. (SSMS / ADO.NET already default these ON.)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- Schema 2.13.0 (decision 35, list-pages-server-side plan): required for the read
-- repositories' SET TRANSACTION ISOLATION LEVEL SNAPSHOT connections. Chosen over
-- READ_COMMITTED_SNAPSHOT so ingestion's own reads (e.g. the span INSERT ... WHERE NOT
-- EXISTS) are unaffected -- only the API's read repositories opt into SNAPSHOT explicitly.
-- Must run as its own batch, outside any transaction (a fresh-install run like this one has
-- none); it waits for in-flight transactions to finish but needs no single-user mode.
ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

CREATE TABLE tenants (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    name       NVARCHAR(255) NOT NULL,
    created_at DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT uk_tenant_name UNIQUE (name)
);

CREATE TABLE api_keys (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id    BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    key_hash     CHAR(64)      NOT NULL,
    name         NVARCHAR(255) NOT NULL,
    is_active    BIT           NOT NULL DEFAULT 1,
    created_at   DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_used_at DATETIME2,
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash)
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);
GO

-- Resource represents the entity producing telemetry.
CREATE TABLE resources (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id       BIGINT        NOT NULL DEFAULT 1 REFERENCES tenants(id),
    resource_hash   CHAR(64)      NOT NULL,
    schema_url      NVARCHAR(2048),
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash)
);
CREATE INDEX idx_resources_tenant_id ON resources (tenant_id);
CREATE INDEX idx_created_at ON resources (created_at);

-- PostgreSQL had a functional index on (attributes_json ->> 'service.name').
-- SQL Server equivalent requires a persisted computed column; omitted here.
-- Add one if filtering by service name at scale becomes a bottleneck:
--   ALTER TABLE resources ADD service_name AS JSON_VALUE(attributes_json, '$."service.name"') PERSISTED;
--   CREATE INDEX idx_resources_service_name ON resources (service_name);
GO

-- Instrumentation scope (library).
CREATE TABLE instrumentation_scopes (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    name            NVARCHAR(255) NOT NULL,
    version         NVARCHAR(255),
    schema_url      NVARCHAR(2048),
    scope_hash      CHAR(64)      NOT NULL,
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    CONSTRAINT uk_scope_hash UNIQUE (scope_hash)
);
CREATE INDEX idx_name_version ON instrumentation_scopes (name, version);
GO

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

CREATE TABLE spans (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    trace_id                 CHAR(32)      NOT NULL,
    span_id                  CHAR(16)      NOT NULL,
    parent_span_id           CHAR(16),
    resource_id              BIGINT        NOT NULL,
    scope_id                 BIGINT        NOT NULL,
    name                     NVARCHAR(255) NOT NULL,
    kind                     NVARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    start_time_unix_nano     BIGINT        NOT NULL,
    end_time_unix_nano       BIGINT        NOT NULL,
    dropped_attributes_count INT           DEFAULT 0,
    dropped_events_count     INT           DEFAULT 0,
    dropped_links_count      INT           DEFAULT 0,
    trace_state              NVARCHAR(MAX),
    flags                    INT           DEFAULT 0,
    status_code              NVARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK (status_code IN ('UNSET', 'OK', 'ERROR')),
    status_message           NVARCHAR(MAX),
    created_at               DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX),
    events_json              NVARCHAR(MAX),
    links_json               NVARCHAR(MAX),
    CONSTRAINT fk_spans_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    CONSTRAINT uk_trace_span      UNIQUE (trace_id, span_id)
);
-- idx_trace_id, idx_start_time, idx_kind and idx_status dropped in 2.8.0: idx_trace_id is a left
-- prefix of uk_trace_span (trace_id, span_id); idx_start_time is a left prefix of idx_duration
-- (start_time_unix_nano, end_time_unix_nano); idx_kind (6 distinct values) and idx_status (3
-- distinct values) are too low-cardinality for the planner to ever choose. All three carried real
-- write cost for zero read benefit.
CREATE INDEX idx_span_id             ON spans (span_id);
CREATE INDEX idx_parent_span         ON spans (parent_span_id);
CREATE INDEX idx_spans_trace_parent  ON spans (trace_id, parent_span_id);
CREATE INDEX idx_end_time            ON spans (end_time_unix_nano DESC);
CREATE INDEX idx_duration            ON spans (start_time_unix_nano, end_time_unix_nano);
CREATE INDEX idx_spans_name          ON spans (name);
CREATE INDEX idx_spans_resource_time ON spans (resource_id, start_time_unix_nano DESC);
-- Filtered, not the idx_status this replaces the intent of (dropped in 2.8.0 for being
-- low-cardinality over the *whole* table): ERROR is the minority status in practice (spans are
-- overwhelmingly UNSET/OK), so this indexes only the rare rows mode=errors actually needs and
-- stays small and cheap to maintain despite the 2.8.0 reasoning not applying to it (schema 2.12.0).
CREATE INDEX idx_spans_error ON spans (start_time_unix_nano DESC) WHERE status_code = 'ERROR';
-- GIN index on attributes_json omitted: no SQL Server equivalent.
GO

-- Trace page/summary anchor on roots (schema 2.13.1, list-pages-server-side plan Phase 3):
-- every root-anchored query used to read all spans through idx_duration and filter out
-- non-roots. INCLUDE(end_time_unix_nano) so mode=slow's duration check runs inside the index.
CREATE INDEX idx_spans_root_time ON spans (start_time_unix_nano DESC) INCLUDE (end_time_unix_nano)
    WHERE parent_span_id IS NULL;
GO

-- span_events and span_links were dropped in 2.11.0: neither was ever read or written
-- independently of its parent span, so both collapsed into spans.events_json/links_json.

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

CREATE TABLE metrics (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    resource_id BIGINT        NOT NULL,
    scope_id    BIGINT        NOT NULL,
    name        NVARCHAR(255) NOT NULL,
    description NVARCHAR(MAX),
    unit        NVARCHAR(63),
    -- [type] is bracketed because TYPE is a reserved word in T-SQL.
    [type]      NVARCHAR(30)  NOT NULL
        CHECK ([type] IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at  DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    -- Metric identity: one row per (resource, scope, name, type), not one row per OTLP
    -- export cycle. Resources and instrumentation_scopes dedup on an unbounded attribute map
    -- and therefore need a SHA-256 hash column; a metric is identified by bounded scalar
    -- columns that already exist here, so a plain composite UNIQUE is enough.
    --
    -- type is part of the key, not merely updated on conflict. The write path chooses which
    -- data-point table to insert into from the INCOMING type while the read path chooses which
    -- to read from the STORED type, so a metric that changes type mid-stream and matched an
    -- existing row would write points the reader would never look for. Keying on type makes
    -- such a change a new row instead: old points stay readable, new points are found.
    --
    -- Column order is deliberate. Leading with (resource_id, name, ...) makes the former
    -- idx_resource_name an exact redundant left prefix, so it is dropped below.
    -- Key bytes: 8 + 510 + 60 + 8 = 586, well inside the 1700-byte nonclustered limit.
    -- NOTE: under a case-insensitive database collation (the LocalDB default) this treats
    -- metric names differing only by case as the same metric, which PostgreSQL and
    -- ClickHouse do not. OTLP names are case-sensitive; no real instrumentation emits
    -- case-variant duplicates, so this is accepted rather than forced with a BIN2 collation.
    CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, [type], scope_id)
);
CREATE INDEX idx_metrics_name  ON metrics (name);
CREATE INDEX idx_type          ON metrics ([type]);
-- idx_resource_name (resource_id, name) dropped in 2.7.0: now a left prefix of uk_metric_identity.
GO

-- exemplars_json (2.9.0) replaces the former single exemplar_id column and the shared
-- `exemplars` table, which no writer ever populated. OTLP declares `repeated Exemplar
-- exemplars` on every data point except Summary, so one id per row could never hold more than
-- the first. The list is stored as JSON on the data point itself: a child table would need each
-- data point's generated id, which the bulk-load path (binary COPY / bulk copy) does not hand
-- back, and JSON is already how bucket_counts, explicit_bounds and quantile_values are stored.
-- Trade-off: an exemplar's trace_id is no longer indexable. No read path queries it.
-- Gauge data points.
-- PostgreSQL used a TimescaleDB hypertable (no PRIMARY KEY); SQL Server uses a normal table.
CREATE TABLE gauge_data_points (
    id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         FLOAT,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    attributes_json      NVARCHAR(MAX),
    exemplars_json       NVARCHAR(MAX),
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_gauge_metric_time ON gauge_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points (time_unix_nano DESC);
GO

-- Sum data points.
CREATE TABLE sum_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    value_double            FLOAT,
    value_int               BIGINT,
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            BIT          DEFAULT 0,
    flags                   INT          DEFAULT 0,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX),
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
-- Standalone time index added in 2.7.0: metric retention now deletes from the data-point
-- tables by time_unix_nano alone, which without this would full-scan.
CREATE INDEX idx_sum_metric_time ON sum_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_sum_time        ON sum_data_points (time_unix_nano DESC);
CREATE INDEX idx_temporality     ON sum_data_points (aggregation_temporality);
GO

-- Histogram data points.
CREATE TABLE histogram_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    bucket_counts           NVARCHAR(MAX),
    explicit_bounds         NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX),
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_histogram_metric_time ON histogram_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_histogram_time        ON histogram_data_points (time_unix_nano DESC);
GO

-- Exponential histogram data points.
CREATE TABLE exponential_histogram_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    scale                   INT          NOT NULL,
    zero_count              BIGINT       NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  NVARCHAR(MAX),
    negative_offset         INT,
    negative_bucket_counts  NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    attributes_json         NVARCHAR(MAX),
    exemplars_json          NVARCHAR(MAX),
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_exp_histogram_time        ON exponential_histogram_data_points (time_unix_nano DESC);
GO

-- Summary data points.
CREATE TABLE summary_data_points (
    id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            FLOAT  NOT NULL,
    quantile_values      NVARCHAR(MAX),
    flags                INT    DEFAULT 0,
    attributes_json      NVARCHAR(MAX),
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_summary_metric_time ON summary_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_summary_time        ON summary_data_points (time_unix_nano DESC);
GO

-- metric_last_seen (schema 2.13.2, list-pages-server-side plan Phase 5, decision 27): not a
-- column on metrics -- see PostgreSQL-Schema.sql's identical table for the full rationale (no
-- FK, so ingestion's metrics MERGE and this table's touch worker can never deadlock against
-- each other).
CREATE TABLE metric_last_seen (
    metric_id           BIGINT NOT NULL PRIMARY KEY,
    last_seen_unix_nano BIGINT NOT NULL
);
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);
GO

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records.
-- PostgreSQL used a TimescaleDB hypertable on time_unix_nano; SQL Server uses a normal table.
-- Default 0 for time_unix_nano handles OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    resource_id              BIGINT       NOT NULL,
    scope_id                 BIGINT       NOT NULL,
    time_unix_nano           BIGINT       NOT NULL DEFAULT 0,
    observed_time_unix_nano  BIGINT,
    severity_number          INT,
    severity_text            NVARCHAR(MAX),
    event_name               NVARCHAR(256),
    body_type                NVARCHAR(20) DEFAULT 'STRING'
        CHECK (body_type IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    body_value               NVARCHAR(MAX),
    dropped_attributes_count INT          DEFAULT 0,
    flags                    INT          DEFAULT 0,
    trace_id                 CHAR(32),
    span_id                  CHAR(16),
    created_at               DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX),
    CONSTRAINT fk_log_records_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id)
);
-- idx_log_time dropped in 2.13.0: it is a pure left prefix of idx_log_time_id below, the new
-- keyset-paging tiebreak index (same provably-redundant reasoning as the 2.8.0 spans index
-- cleanup documented in CLAUDE.md).
CREATE INDEX idx_log_time_id       ON log_records (time_unix_nano DESC, id DESC);
CREATE INDEX idx_observed_time     ON log_records (observed_time_unix_nano  DESC);
CREATE INDEX idx_severity          ON log_records (severity_number);
CREATE INDEX idx_log_severity_time ON log_records (severity_number, time_unix_nano DESC);
CREATE INDEX idx_log_trace_span    ON log_records (trace_id, span_id);
CREATE INDEX idx_log_resource_time ON log_records (resource_id, time_unix_nano DESC);
-- GIN index on attributes_json omitted: no SQL Server equivalent.
GO

-- =============================================================================
-- ROLLUP TABLES (schema 2.13.0, list-pages-server-side plan decisions 37-38)
-- =============================================================================

-- One row per signal + granularity, claimed by RollupWorker with an atomic
-- UPDATE ... WHERE lease_expires_at < now, the same pattern as alert_rules'
-- TryClaimFireAsync. No foreign keys: the worker's writes must never lock resources or
-- anything ingestion touches.
CREATE TABLE rollup_state (
    [signal_name]                  NVARCHAR(20)  NOT NULL,
    granularity               NVARCHAR(10)  NOT NULL,
    coverage_start_unix_nano  BIGINT,
    rolled_until_unix_nano    BIGINT        NOT NULL DEFAULT 0,
    repassed_until_unix_nano  BIGINT        NOT NULL DEFAULT 0,
    lease_owner               NVARCHAR(100),
    lease_expires_at          DATETIME2     NOT NULL DEFAULT '1970-01-01T00:00:00',
    CONSTRAINT pk_rollup_state PRIMARY KEY ([signal_name], granularity)
);
GO

-- Per-minute log summary, recomputed from raw log_records by RollupWorker -- never
-- incremented during ingestion (decision 38). Severity groups match
-- LogReadRepositoryBase.GetLogHistogramAsync's six-group CASE exactly. No foreign key on
-- resource_id: the worker's writes must never lock resources.
CREATE TABLE log_rollup_minute (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    CONSTRAINT pk_log_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id)
);
CREATE INDEX idx_log_rollup_minute_bucket ON log_rollup_minute (bucket_unix_nano);
GO

-- Same shape, one row per hour.
CREATE TABLE log_rollup_hour (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    CONSTRAINT pk_log_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id)
);
CREATE INDEX idx_log_rollup_hour_bucket ON log_rollup_hour (bucket_unix_nano);
GO

-- Traces whose root span never arrived (schema 2.13.1, decision 41): one row per trace,
-- holding the anchor span (its earliest span, whose own parent does not exist anywhere) that
-- the rollup worker detected per finished minute. No foreign keys: the worker's writes must
-- never lock resources. Indexed to merge with idx_spans_root_time in the same order.
CREATE TABLE orphan_roots (
    trace_id             CHAR(32)     NOT NULL,
    span_id              CHAR(16)     NOT NULL,
    resource_id          BIGINT       NOT NULL,
    start_time_unix_nano BIGINT       NOT NULL,
    end_time_unix_nano   BIGINT       NOT NULL,
    detected_at          DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT pk_orphan_roots PRIMARY KEY (trace_id)
);
CREATE INDEX idx_orphan_roots_start ON orphan_roots (start_time_unix_nano DESC, trace_id);
GO

-- Per-minute trace summary (schema 2.13.1, decisions 37-38, 41): one row per minute, per
-- anchor span's resource, operation name (folded to '__other__' past the 200-distinct-name
-- cardinality guard) and inbound flag (anchor kind SERVER/CONSUMER). Counts traces whose
-- ANCHOR (null-parent root, or its orphan_roots span) starts in that minute; error flag and
-- duration are aggregated over the trace's full span set. lb_00..lb_39 are the fixed
-- latency-bucket counts (LatencyBucketSql) as plain columns so SQL can sum them across rows.
CREATE TABLE trace_rollup_minute (
    bucket_unix_nano BIGINT        NOT NULL,
    resource_id      BIGINT        NOT NULL,
    root_name        NVARCHAR(255) NOT NULL,
    inbound          TINYINT       NOT NULL,
    trace_count      INT           NOT NULL DEFAULT 0,
    error_count      INT           NOT NULL DEFAULT 0,
    duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
    duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
    lb_00 INT NOT NULL DEFAULT 0,
    lb_01 INT NOT NULL DEFAULT 0,
    lb_02 INT NOT NULL DEFAULT 0,
    lb_03 INT NOT NULL DEFAULT 0,
    lb_04 INT NOT NULL DEFAULT 0,
    lb_05 INT NOT NULL DEFAULT 0,
    lb_06 INT NOT NULL DEFAULT 0,
    lb_07 INT NOT NULL DEFAULT 0,
    lb_08 INT NOT NULL DEFAULT 0,
    lb_09 INT NOT NULL DEFAULT 0,
    lb_10 INT NOT NULL DEFAULT 0,
    lb_11 INT NOT NULL DEFAULT 0,
    lb_12 INT NOT NULL DEFAULT 0,
    lb_13 INT NOT NULL DEFAULT 0,
    lb_14 INT NOT NULL DEFAULT 0,
    lb_15 INT NOT NULL DEFAULT 0,
    lb_16 INT NOT NULL DEFAULT 0,
    lb_17 INT NOT NULL DEFAULT 0,
    lb_18 INT NOT NULL DEFAULT 0,
    lb_19 INT NOT NULL DEFAULT 0,
    lb_20 INT NOT NULL DEFAULT 0,
    lb_21 INT NOT NULL DEFAULT 0,
    lb_22 INT NOT NULL DEFAULT 0,
    lb_23 INT NOT NULL DEFAULT 0,
    lb_24 INT NOT NULL DEFAULT 0,
    lb_25 INT NOT NULL DEFAULT 0,
    lb_26 INT NOT NULL DEFAULT 0,
    lb_27 INT NOT NULL DEFAULT 0,
    lb_28 INT NOT NULL DEFAULT 0,
    lb_29 INT NOT NULL DEFAULT 0,
    lb_30 INT NOT NULL DEFAULT 0,
    lb_31 INT NOT NULL DEFAULT 0,
    lb_32 INT NOT NULL DEFAULT 0,
    lb_33 INT NOT NULL DEFAULT 0,
    lb_34 INT NOT NULL DEFAULT 0,
    lb_35 INT NOT NULL DEFAULT 0,
    lb_36 INT NOT NULL DEFAULT 0,
    lb_37 INT NOT NULL DEFAULT 0,
    lb_38 INT NOT NULL DEFAULT 0,
    lb_39 INT NOT NULL DEFAULT 0,
    CONSTRAINT pk_trace_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
);
CREATE INDEX idx_trace_rollup_minute_bucket ON trace_rollup_minute (bucket_unix_nano);
GO

-- Same shape, one row per hour.
CREATE TABLE trace_rollup_hour (
    bucket_unix_nano BIGINT        NOT NULL,
    resource_id      BIGINT        NOT NULL,
    root_name        NVARCHAR(255) NOT NULL,
    inbound          TINYINT       NOT NULL,
    trace_count      INT           NOT NULL DEFAULT 0,
    error_count      INT           NOT NULL DEFAULT 0,
    duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
    duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
    lb_00 INT NOT NULL DEFAULT 0,
    lb_01 INT NOT NULL DEFAULT 0,
    lb_02 INT NOT NULL DEFAULT 0,
    lb_03 INT NOT NULL DEFAULT 0,
    lb_04 INT NOT NULL DEFAULT 0,
    lb_05 INT NOT NULL DEFAULT 0,
    lb_06 INT NOT NULL DEFAULT 0,
    lb_07 INT NOT NULL DEFAULT 0,
    lb_08 INT NOT NULL DEFAULT 0,
    lb_09 INT NOT NULL DEFAULT 0,
    lb_10 INT NOT NULL DEFAULT 0,
    lb_11 INT NOT NULL DEFAULT 0,
    lb_12 INT NOT NULL DEFAULT 0,
    lb_13 INT NOT NULL DEFAULT 0,
    lb_14 INT NOT NULL DEFAULT 0,
    lb_15 INT NOT NULL DEFAULT 0,
    lb_16 INT NOT NULL DEFAULT 0,
    lb_17 INT NOT NULL DEFAULT 0,
    lb_18 INT NOT NULL DEFAULT 0,
    lb_19 INT NOT NULL DEFAULT 0,
    lb_20 INT NOT NULL DEFAULT 0,
    lb_21 INT NOT NULL DEFAULT 0,
    lb_22 INT NOT NULL DEFAULT 0,
    lb_23 INT NOT NULL DEFAULT 0,
    lb_24 INT NOT NULL DEFAULT 0,
    lb_25 INT NOT NULL DEFAULT 0,
    lb_26 INT NOT NULL DEFAULT 0,
    lb_27 INT NOT NULL DEFAULT 0,
    lb_28 INT NOT NULL DEFAULT 0,
    lb_29 INT NOT NULL DEFAULT 0,
    lb_30 INT NOT NULL DEFAULT 0,
    lb_31 INT NOT NULL DEFAULT 0,
    lb_32 INT NOT NULL DEFAULT 0,
    lb_33 INT NOT NULL DEFAULT 0,
    lb_34 INT NOT NULL DEFAULT 0,
    lb_35 INT NOT NULL DEFAULT 0,
    lb_36 INT NOT NULL DEFAULT 0,
    lb_37 INT NOT NULL DEFAULT 0,
    lb_38 INT NOT NULL DEFAULT 0,
    lb_39 INT NOT NULL DEFAULT 0,
    CONSTRAINT pk_trace_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
);
CREATE INDEX idx_trace_rollup_hour_bucket ON trace_rollup_hour (bucket_unix_nano);
GO

-- Seed the four rows this phase needs (logs/traces x minute/hour).
INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'logs', N'minute'), (N'logs', N'hour'), (N'traces', N'minute'), (N'traces', N'hour');
GO

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    version    NVARCHAR(20) PRIMARY KEY,
    applied_at DATETIME2    NOT NULL DEFAULT SYSDATETIME()
);
GO
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables and views), so a partial/failed apply never records a version that the
-- apply-schema.sh version gate would wrongly treat as "already applied".

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    id               INT           IDENTITY(1,1) PRIMARY KEY,
    tenant_id        BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name             NVARCHAR(MAX) NOT NULL,
    -- [type] bracketed because TYPE is a reserved word in T-SQL.
    [type]           NVARCHAR(50)  NOT NULL,
    service_name     NVARCHAR(255),
    condition_json   NVARCHAR(MAX) NOT NULL,
    webhook_url      NVARCHAR(MAX) NOT NULL,
    cooldown_minutes INT           NOT NULL DEFAULT 60,
    enabled          BIT           NOT NULL DEFAULT 1,
    created_at       DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_fired_at    DATETIME2
);
CREATE INDEX idx_alert_rules_tenant_id      ON alert_rules (tenant_id);
-- SQL Server filtered index: WHERE enabled = 1  (BIT 1 = TRUE)
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules (tenant_id, enabled) WHERE enabled = 1;
GO

CREATE TABLE alert_events (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    rule_id      INT           NOT NULL,
    fired_at     DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    details_json NVARCHAR(MAX) NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events (rule_id);
CREATE INDEX idx_alert_events_fired_at ON alert_events (fired_at DESC);
GO

-- =============================================================================
-- RETENTION TABLES
-- =============================================================================

-- Single global row (id = 1, enforced by the CHECK below) — see
-- IRetentionSettingsRepository for why this is untenanted and why UPDATE, never INSERT,
-- is the only mutation the app issues against it after the seed row below.
CREATE TABLE retention_settings (
    id                    SMALLINT  NOT NULL PRIMARY KEY DEFAULT 1,
    trace_retention_days  INT       NOT NULL,
    log_retention_days    INT       NOT NULL,
    metric_retention_days INT       NOT NULL,
    updated_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT chk_retention_settings_singleton CHECK (id = 1)
);
GO
-- Seeded with today's implicit defaults (traces 90d, logs 90d, metrics 180d).
INSERT INTO retention_settings (id, trace_retention_days, log_retention_days, metric_retention_days)
VALUES (1, 90, 90, 180);
GO

-- =============================================================================
-- VIEWS
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;
GO

-- Trace summary: aggregated span counts and timing per trace.
CREATE VIEW trace_summary AS
SELECT
    s.trace_id                                                AS trace_id_hex,
    s.trace_id,
    COUNT(*)                                                  AS span_count,
    MIN(s.start_time_unix_nano)                               AS trace_start_time,
    MAX(s.end_time_unix_nano)                                 AS trace_end_time,
    MAX(s.end_time_unix_nano) - MIN(s.start_time_unix_nano)   AS trace_duration_ns,
    r.id                                                      AS resource_id
FROM spans s
JOIN resources r ON s.resource_id = r.id
GROUP BY s.trace_id, r.id;
GO

-- Service map: service-to-service call relationships extracted from span parent-child pairs.
-- JSON_VALUE replaces PostgreSQL's ->> operator.
CREATE VIEW service_map AS
SELECT
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') AS parent_service,
    JSON_VALUE(child_res.attributes_json,  '$."service.name"') AS child_service,
    child.kind                                               AS span_kind,
    COUNT(*)                                                 AS call_count
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.attributes_json,  '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.attributes_json, '$."service.name"') <>
        JSON_VALUE(child_res.attributes_json,  '$."service.name"')
GROUP BY
    JSON_VALUE(parent_res.attributes_json, '$."service.name"'),
    JSON_VALUE(child_res.attributes_json,  '$."service.name"'),
    child.kind;
GO

-- Service map with performance metrics.
CREATE VIEW service_map_detailed AS
SELECT
    JSON_VALUE(parent_res.attributes_json, '$."service.name"')                               AS parent_service,
    JSON_VALUE(child_res.attributes_json,  '$."service.name"')                               AS child_service,
    child.kind                                                                             AS span_kind,
    COUNT(*)                                                                               AS call_count,
    AVG(CAST(child.end_time_unix_nano - child.start_time_unix_nano AS FLOAT)) / 1000000   AS avg_duration_ms,
    MIN(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000                  AS min_duration_ms,
    MAX(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000                  AS max_duration_ms,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END)                          AS error_count,
    CAST(SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END) AS FLOAT)
        / COUNT(*) * 100                                                                   AS error_rate
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.attributes_json,  '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.attributes_json, '$."service.name"') <>
        JSON_VALUE(child_res.attributes_json,  '$."service.name"')
GROUP BY
    JSON_VALUE(parent_res.attributes_json, '$."service.name"'),
    JSON_VALUE(child_res.attributes_json,  '$."service.name"'),
    child.kind;
GO

-- Log severity distribution by day.
-- PostgreSQL had a TimescaleDB continuous aggregate (pre-computed, refreshed every 5 minutes).
-- SQL Server uses a regular view (computed on demand).
-- The day bucket is computed by integer-dividing nanoseconds down to whole days since epoch,
-- then converting back to a DATE via DATEADD(DAY, ..., '1970-01-01').
CREATE VIEW log_severity_stats AS
WITH bucketed AS (
    SELECT
        severity_text,
        severity_number,
        CAST(time_unix_nano / 1000000000 / 86400 AS INT) AS day_bucket
    FROM log_records
    WHERE time_unix_nano > 0
)
SELECT
    severity_text,
    severity_number,
    COUNT(*)                                                       AS [count],
    CAST(DATEADD(DAY, day_bucket, '1970-01-01') AS DATE)          AS log_date
FROM bucketed
GROUP BY severity_text, severity_number, day_bucket;
GO

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
MERGE schema_version AS target
USING (VALUES (N'2.13.2')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all user tables created
--    SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;
--
-- 2) List all indexes
--    SELECT i.name AS index_name, t.name AS table_name
--    FROM sys.indexes i
--    JOIN sys.tables t ON i.object_id = t.object_id
--    WHERE i.name IS NOT NULL
--    ORDER BY t.name, i.name;
--
-- 3) Verify schema version
--    SELECT * FROM schema_version;
--
-- 4) Verify default tenant
--    SELECT * FROM tenants;
