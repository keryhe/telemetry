-- OpenTelemetry PostgreSQL + TimescaleDB Schema
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Requires TimescaleDB extension (https://docs.timescale.com/install/latest/)
--
-- Column names use snake_case (double-quoted) while C# models remain PascalCase.
-- Table names use snake_case as configured via ToTable() in OpenTelemetryDbContext.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f Timescale-Schema.sql
--
-- For a vanilla PostgreSQL instance WITHOUT the timescaledb extension, use
-- PostgreSQL-Schema.sql instead (same logical table/column set, plain tables).
--
-- Optional clean reset in an existing local DB before re-running this script:
--   psql -U postgres -d telemetry -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"

-- =============================================================================
-- EXTENSIONS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

CREATE TABLE tenants (
    "id"        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"      VARCHAR(255) NOT NULL,
    "created_at" TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT uk_tenant_name UNIQUE ("name")
);

CREATE TABLE api_keys (
    "id"         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"   BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "key_hash"    CHAR(64)     NOT NULL,
    "name"       VARCHAR(255) NOT NULL,
    "is_active"   BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_used_at" TIMESTAMPTZ,
    CONSTRAINT uk_api_key_hash UNIQUE ("key_hash")
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys ("tenant_id");

-- Resource represents the entity producing telemetry
CREATE TABLE resources (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"       BIGINT       NOT NULL DEFAULT 1 REFERENCES tenants("id"),
    "resource_hash"   CHAR(64)     NOT NULL,
    "schema_url"      VARCHAR(2048),
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX idx_resources_tenant_id ON resources ("tenant_id");
CREATE INDEX idx_created_at ON resources ("created_at");
CREATE INDEX idx_resources_service_name ON resources (("attributes_json" ->> 'service.name'));

-- Instrumentation scope (library)
CREATE TABLE instrumentation_scopes (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"           VARCHAR(255) NOT NULL,
    "version"        VARCHAR(255),
    "schema_url"      VARCHAR(2048),
    "scope_hash"      CHAR(64)     NOT NULL,
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_scope_hash UNIQUE ("scope_hash")
);
CREATE INDEX idx_name_version ON instrumentation_scopes ("name", "version");

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans: TimescaleDB hypertable partitioned on "start_time_unix_nano" (schema 2.11.0).
-- Events and links live in the "events_json"/"links_json" columns on this row rather than
-- child tables; removing those child tables' FK references to spans("id") is what allowed
-- the primary key to be widened to include the partition column and the table to become a
-- hypertable (and therefore to be compressed).
CREATE TABLE spans (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "trace_id"                CHAR(32)     NOT NULL,
    "span_id"                 CHAR(16)     NOT NULL,
    "parent_span_id"           CHAR(16),
    "resource_id"             BIGINT       NOT NULL,
    "scope_id"                BIGINT       NOT NULL,
    "name"                   VARCHAR(255) NOT NULL,
    "kind"                   VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("kind" IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    "start_time_unix_nano"      BIGINT       NOT NULL,
    "end_time_unix_nano"        BIGINT       NOT NULL,
    "dropped_attributes_count" INTEGER      DEFAULT 0,
    "dropped_events_count"     INTEGER      DEFAULT 0,
    "dropped_links_count"      INTEGER      DEFAULT 0,
    "trace_state"             TEXT,
    "flags"                  INTEGER      DEFAULT 0,
    "status_code"             VARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK ("status_code" IN ('UNSET', 'OK', 'ERROR')),
    "status_message"          TEXT,
    "created_at"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json"         JSONB,
    "events_json"             JSONB,
    "links_json"              JSONB,
    CONSTRAINT fk_spans_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id"),
    -- Both unique constraints include the partition column, as TimescaleDB requires on a
    -- hypertable. "id" remains globally unique in practice (identity sequence); nothing
    -- FK-references it any more, so the composite PK costs no read path anything.
    CONSTRAINT pk_spans           PRIMARY KEY ("id", "start_time_unix_nano"),
    CONSTRAINT uk_trace_span      UNIQUE ("trace_id", "span_id", "start_time_unix_nano")
);
SELECT create_hypertable('spans', 'start_time_unix_nano',
    chunk_time_interval => 21600000000000,
    if_not_exists => TRUE
);
-- idx_trace_id, idx_start_time, idx_kind, idx_status and idx_spans_attributes_gin dropped in
-- 2.8.0: idx_trace_id is a left prefix of uk_trace_span (trace_id, span_id); idx_start_time is a
-- left prefix of idx_duration (start_time_unix_nano, end_time_unix_nano); idx_kind (6 distinct
-- values) and idx_status (3 distinct values) are too low-cardinality for the planner to ever
-- choose; idx_spans_attributes_gin has no query in the read path that does JSONB containment on
-- attributes_json -- every read of that column is a plain SELECT, verified by grep across
-- TraceReadRepositoryBase and LogReadRepositoryBase. All five carried real write cost (index
-- maintenance on every insert, GIN's most of all) for zero read benefit.
CREATE INDEX idx_span_id            ON spans ("span_id");
CREATE INDEX idx_parent_span        ON spans ("parent_span_id");
CREATE INDEX idx_spans_trace_parent ON spans ("trace_id", "parent_span_id");
CREATE INDEX idx_end_time           ON spans ("end_time_unix_nano"   DESC);
CREATE INDEX idx_duration           ON spans ("start_time_unix_nano", "end_time_unix_nano");
CREATE INDEX idx_spans_name         ON spans ("name");
CREATE INDEX idx_spans_resource_time ON spans ("resource_id", "start_time_unix_nano" DESC);
-- Partial, not the idx_status this replaces the intent of (dropped in 2.8.0 for being
-- low-cardinality over the *whole* table): ERROR is the minority status in practice (spans are
-- overwhelmingly UNSET/OK), so this indexes only the rare rows mode=errors actually needs and
-- stays small and cheap to maintain despite the 2.8.0 reasoning not applying to it (schema
-- 2.12.0). Created after create_hypertable above, so TimescaleDB propagates it to every chunk.
CREATE INDEX idx_spans_error ON spans ("start_time_unix_nano" DESC) WHERE "status_code" = 'ERROR';

-- span_events and span_links were dropped in 2.11.0: neither was ever read or written
-- independently of its parent span, so both collapsed into spans."events_json"/"links_json",
-- which in turn removed the FK that kept spans from being a hypertable.

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table (regular table  referenced by FK from data point tables)
CREATE TABLE metrics (
    "id"          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "resource_id"  BIGINT       NOT NULL,
    "scope_id"     BIGINT       NOT NULL,
    "name"        VARCHAR(255) NOT NULL,
    "description" TEXT,
    "unit"        VARCHAR(63),
    "type"        VARCHAR(30)  NOT NULL
        CHECK ("type" IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    "created_at"   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id"),
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
    CONSTRAINT uk_metric_identity UNIQUE ("resource_id", "name", "type", "scope_id")
);
CREATE INDEX idx_metrics_name  ON metrics ("name");
CREATE INDEX idx_type          ON metrics ("type");
-- idx_resource_name (resource_id, name) dropped in 2.7.0: now a left prefix of uk_metric_identity.

-- TimescaleDB chunk sizing baseline (nanoseconds):
--   1 hour  =  3600000000000
--   6 hours = 21600000000000
--   12 hours= 43200000000000
--   1 day   = 86400000000000
--
-- Initial Phase 1 target:
-- - Keep chunks in the ~256 MB to 1 GB range under normal ingest.
-- - Use shorter chunks for higher-volume signals (logs), longer chunks for
--   lower-volume metric point tables until real ingest data is available.

-- exemplars_json (2.9.0) replaces the former single exemplar_id column and the shared
-- `exemplars` table, which no writer ever populated. OTLP declares `repeated Exemplar
-- exemplars` on every data point except Summary, so one id per row could never hold more than
-- the first. The list is stored as JSON on the data point itself: a child table would need each
-- data point's generated id, which the bulk-load path (binary COPY / bulk copy) does not hand
-- back, and JSON is already how bucket_counts, explicit_bounds and quantile_values are stored.
-- Trade-off: an exemplar's trace_id is no longer indexable. No read path queries it.
-- Gauge data points (TimescaleDB hypertable on TimeUnixNano)
-- No PRIMARY KEY: TimescaleDB requires unique constraints to include the partition
-- column; since nothing FK-references this table's Id, a DB-level PK is not needed.
CREATE TABLE gauge_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "value_double"       DOUBLE PRECISION,
    "value_int"          BIGINT,
    "flags"             INTEGER          DEFAULT 0,
    "attributes_json"    JSONB,
    "exemplars_json"     JSONB,
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('gauge_data_points', 'time_unix_nano',
    chunk_time_interval => 43200000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points ("time_unix_nano" DESC);

-- Sum data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE sum_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "value_double"            DOUBLE PRECISION,
    "value_int"               BIGINT,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "is_monotonic"            BOOLEAN          DEFAULT FALSE,
    "flags"                  INTEGER          DEFAULT 0,
    "attributes_json"         JSONB,
    "exemplars_json"          JSONB,
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('sum_data_points', 'time_unix_nano',
    chunk_time_interval => 43200000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_temporality     ON sum_data_points ("aggregation_temporality");

-- Histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "count"                  BIGINT           NOT NULL,
    "sum_value"               DOUBLE PRECISION,
    "bucket_counts"           JSONB,
    "explicit_bounds"         JSONB,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "flags"                  INTEGER          DEFAULT 0,
    "min_value"              DOUBLE PRECISION,
    "max_value"              DOUBLE PRECISION,
    "attributes_json"         JSONB,
    "exemplars_json"          JSONB,
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('histogram_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);

-- Exponential histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE exponential_histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"               BIGINT           NOT NULL,
    "start_time_unix_nano"      BIGINT,
    "time_unix_nano"           BIGINT           NOT NULL,
    "count"                  BIGINT           NOT NULL,
    "sum_value"               DOUBLE PRECISION,
    "scale"                  INTEGER          NOT NULL,
    "zero_count"              BIGINT           NOT NULL,
    "positive_offset"         INTEGER,
    "positive_bucket_counts"   JSONB,
    "negative_offset"         INTEGER,
    "negative_bucket_counts"   JSONB,
    "aggregation_temporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("aggregation_temporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "flags"                  INTEGER          DEFAULT 0,
    "min_value"              DOUBLE PRECISION,
    "max_value"              DOUBLE PRECISION,
    "attributes_json"         JSONB,
    "exemplars_json"          JSONB,
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('exponential_histogram_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);

-- Summary data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE summary_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "count"             BIGINT           NOT NULL,
    "sum_value"          DOUBLE PRECISION NOT NULL,
    "quantile_values"    JSONB,
    "flags"             INTEGER          DEFAULT 0,
    "attributes_json"    JSONB,
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY ("metric_id") REFERENCES metrics ("id") ON DELETE CASCADE
);
SELECT create_hypertable('summary_data_points', 'time_unix_nano',
    chunk_time_interval => 86400000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records (TimescaleDB hypertable on TimeUnixNano)
-- TimeUnixNano is NOT NULL (required for hypertable partition column).
-- Default 0 handles any edge-case OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "resource_id"             BIGINT       NOT NULL,
    "scope_id"                BIGINT       NOT NULL,
    "time_unix_nano"           BIGINT       NOT NULL DEFAULT 0,
    "observed_time_unix_nano"   BIGINT,
    "severity_number"         INTEGER,
    "severity_text"           TEXT,
    "event_name"              VARCHAR(256),
    "body_type"               TEXT         DEFAULT 'STRING'
        CHECK ("body_type" IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    "body_value"              TEXT,
    "dropped_attributes_count" INTEGER      DEFAULT 0,
    "flags"                  INTEGER      DEFAULT 0,
    "trace_id"                CHAR(32),
    "span_id"                 CHAR(16),
    "created_at"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json"         JSONB,
    CONSTRAINT fk_log_records_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id")
);
SELECT create_hypertable('log_records', 'time_unix_nano',
    chunk_time_interval => 21600000000000,
    if_not_exists => TRUE
);
CREATE INDEX idx_log_time          ON log_records ("time_unix_nano"         DESC);
CREATE INDEX idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX idx_severity          ON log_records ("severity_number");
CREATE INDEX idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);
-- idx_log_attributes_gin dropped in 2.8.0, same reasoning as idx_spans_attributes_gin above:
-- no read-path query does JSONB containment on attributes_json.

-- =============================================================================
-- TIMESCALEDB LIFECYCLE POLICIES (PHASE 2)
-- =============================================================================

-- Integer-time policy constants (nanoseconds)
--   7 days   =   604800000000000
--   90 days  =  7776000000000000
--   180 days = 15552000000000000

-- Integer time source for BIGINT nanosecond hypertables.
CREATE OR REPLACE FUNCTION telemetry_now_ns()
RETURNS BIGINT
LANGUAGE SQL
STABLE
AS $$
    SELECT (EXTRACT(EPOCH FROM NOW()) * 1000000000)::BIGINT;
$$;

-- Register integer-now function for each hypertable.
SELECT set_integer_now_func('spans', 'telemetry_now_ns');
SELECT set_integer_now_func('gauge_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('sum_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('histogram_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('exponential_histogram_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('summary_data_points', 'telemetry_now_ns');
SELECT set_integer_now_func('log_records', 'telemetry_now_ns');

-- Enable compression with segment/order strategy tuned for common query paths.
-- spans segments by "resource_id": it is the column idx_spans_resource_time already pairs
-- with start_time_unix_nano, and it is what every tenant-scoped read filters through.
ALTER TABLE spans SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"resource_id"',
    timescaledb.compress_orderby = '"start_time_unix_nano" DESC'
);
ALTER TABLE gauge_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE sum_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE histogram_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE exponential_histogram_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE summary_data_points SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"metric_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);
ALTER TABLE log_records SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"resource_id", "scope_id"',
    timescaledb.compress_orderby = '"time_unix_nano" DESC'
);

-- Compression policies (cold data).
SELECT add_compression_policy('spans', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('gauge_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('sum_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('exponential_histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('summary_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('log_records', BIGINT '604800000000000', if_not_exists => TRUE);

-- Retention (drop old data) is no longer a native TimescaleDB policy as of schema 2.10.0: the
-- application-level RetentionWorker (Keryhe.Telemetry.Api) is now the one mechanism for
-- retention on every provider, including Timescale, reading its windows from the
-- retention_settings table below instead of a job registered here. See CLAUDE.md's telemetry
-- retention notes. An UPGRADE from a pre-2.10.0 install must also run
-- SELECT remove_retention_policy(...) for log_records and each of the five metric data-point
-- tables — deleting these six lines from this script does not unregister an already-scheduled
-- job on an existing database (see the upgrade notes near schema/apply-schema.sh).

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables, views, and continuous-aggregate policies), so a partial/failed apply never
-- records a version that the apply-schema.sh version gate would treat as "applied".

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    "id"              INTEGER      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"        BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "name"            TEXT         NOT NULL,
    "type"            VARCHAR(50)  NOT NULL,
    "service_name"     VARCHAR(255),
    "condition_json"   JSONB        NOT NULL,
    "webhook_url"      TEXT         NOT NULL,
    "cooldown_minutes" INTEGER      NOT NULL DEFAULT 60,
    "enabled"         BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_fired_at"     TIMESTAMPTZ
);
CREATE INDEX idx_alert_rules_tenant_id ON alert_rules ("tenant_id");
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules ("tenant_id", "enabled") WHERE "enabled" = TRUE;

CREATE TABLE alert_events (
    "id"          BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "rule_id"      INTEGER      NOT NULL,
    "fired_at"     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "details_json" JSONB        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY ("rule_id") REFERENCES alert_rules ("id") ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events ("rule_id");
CREATE INDEX idx_alert_events_fired_at ON alert_events ("fired_at" DESC);

-- =============================================================================
-- RETENTION TABLES
-- =============================================================================

-- Single global row (id = 1, enforced by the CHECK below) — see
-- IRetentionSettingsRepository for why this is untenanted and why UPDATE, never INSERT,
-- is the only mutation the app issues against it after the seed row below. A plain table, not
-- a hypertable: this is a one-row control-plane setting, not a time series.
CREATE TABLE retention_settings (
    "id"                    SMALLINT     PRIMARY KEY DEFAULT 1,
    "trace_retention_days"   INTEGER      NOT NULL,
    "log_retention_days"     INTEGER      NOT NULL,
    "metric_retention_days"  INTEGER      NOT NULL,
    "updated_at"             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_retention_settings_singleton CHECK ("id" = 1)
);
-- Seeded with today's implicit defaults (traces 90d, logs 90d, metrics 180d). ON CONFLICT
-- DO NOTHING makes re-running this script against an already-seeded database a no-op rather
-- than an error.
INSERT INTO retention_settings ("id", "trace_retention_days", "log_retention_days", "metric_retention_days")
VALUES (1, 90, 90, 180)
ON CONFLICT ("id") DO NOTHING;

-- =============================================================================
-- VIEWS
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP MATERIALIZED VIEW IF EXISTS log_severity_stats_daily;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;

-- Trace summary: aggregated span counts and timing per trace
CREATE VIEW trace_summary AS
SELECT
    s."trace_id"::TEXT                                       AS "trace_id_hex",
    s."trace_id",
    COUNT(*)                                                AS "span_count",
    MIN(s."start_time_unix_nano")                              AS "trace_start_time",
    MAX(s."end_time_unix_nano")                                AS "trace_end_time",
    MAX(s."end_time_unix_nano") - MIN(s."start_time_unix_nano")   AS "trace_duration_ns",
    r."id"                                                  AS "resource_id"
FROM spans s
JOIN resources r ON s."resource_id" = r."id"
GROUP BY s."trace_id", r."id";

-- Service map: service-to-service call relationships extracted from span parent-child pairs
CREATE VIEW service_map AS
SELECT
    parent_res."attributes_json" ->> 'service.name'   AS "parent_service",
    child_res."attributes_json"  ->> 'service.name'   AS "child_service",
    child."kind"                                     AS "span_kind",
    COUNT(*)                                         AS "call_count"
FROM spans child
INNER JOIN spans parent
    ON child."parent_span_id" = parent."span_id"
    AND child."trace_id"     = parent."trace_id"
INNER JOIN resources parent_res ON parent."resource_id" = parent_res."id"
INNER JOIN resources child_res  ON child."resource_id"  = child_res."id"
WHERE
    parent_res."attributes_json" ->> 'service.name' IS NOT NULL
    AND child_res."attributes_json"  ->> 'service.name' IS NOT NULL
    AND parent_res."attributes_json" ->> 'service.name' <>
        child_res."attributes_json"  ->> 'service.name'
GROUP BY
    parent_res."attributes_json" ->> 'service.name',
    child_res."attributes_json"  ->> 'service.name',
    child."kind";

-- Service map with performance metrics
CREATE VIEW service_map_detailed AS
SELECT
    parent_res."attributes_json" ->> 'service.name'   AS "parent_service",
    child_res."attributes_json"  ->> 'service.name'   AS "child_service",
    child."kind"                                     AS "span_kind",
    COUNT(*)                                         AS "call_count",
    AVG(CAST(child."end_time_unix_nano" - child."start_time_unix_nano" AS DOUBLE PRECISION)) / 1000000 AS "avg_duration_ms",
    MIN(child."end_time_unix_nano" - child."start_time_unix_nano") / 1000000                            AS "min_duration_ms",
    MAX(child."end_time_unix_nano" - child."start_time_unix_nano") / 1000000                            AS "max_duration_ms",
    SUM(CASE WHEN child."status_code" = 'ERROR' THEN 1 ELSE 0 END)                                AS "error_count",
    (CAST(SUM(CASE WHEN child."status_code" = 'ERROR' THEN 1 ELSE 0 END) AS DOUBLE PRECISION)
        / COUNT(*)) * 100                                                                          AS "error_rate"
FROM spans child
INNER JOIN spans parent
    ON child."parent_span_id" = parent."span_id"
    AND child."trace_id"     = parent."trace_id"
INNER JOIN resources parent_res ON parent."resource_id" = parent_res."id"
INNER JOIN resources child_res  ON child."resource_id"  = child_res."id"
WHERE
    parent_res."attributes_json" ->> 'service.name' IS NOT NULL
    AND child_res."attributes_json"  ->> 'service.name' IS NOT NULL
    AND parent_res."attributes_json" ->> 'service.name' <>
        child_res."attributes_json"  ->> 'service.name'
GROUP BY
    parent_res."attributes_json" ->> 'service.name',
    child_res."attributes_json"  ->> 'service.name',
    child."kind";

-- Log severity distribution by day (continuous aggregate on log_records hypertable)
CREATE MATERIALIZED VIEW log_severity_stats_daily
WITH (timescaledb.continuous) AS
SELECT
    to_timestamp(time_bucket(86400000000000::BIGINT, "time_unix_nano") / 1000000000.0) AS "bucket_day",
    "severity_text",
    "severity_number",
    COUNT(*) AS "count"
FROM log_records
WHERE "time_unix_nano" > 0
GROUP BY
    time_bucket(86400000000000::BIGINT, "time_unix_nano"),
    "severity_text",
    "severity_number"
WITH NO DATA;

CREATE INDEX idx_log_severity_stats_daily_bucket
    ON log_severity_stats_daily ("bucket_day" DESC, "severity_number");

SELECT add_continuous_aggregate_policy(
    'log_severity_stats_daily',
    start_offset => 3024000000000000::BIGINT,
    end_offset => 300000000000::BIGINT,
    schedule_interval => INTERVAL '5 minutes',
    if_not_exists => TRUE
);

ALTER MATERIALIZED VIEW log_severity_stats_daily SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = '"severity_number", "severity_text"',
    timescaledb.compress_orderby = '"bucket_day" DESC'
);

SELECT add_compression_policy('log_severity_stats_daily', 1209600000000000::BIGINT, if_not_exists => TRUE);
SELECT add_retention_policy('log_severity_stats_daily', 34560000000000000::BIGINT, if_not_exists => TRUE);

-- Backward-compatible view name retained for existing query surfaces.
CREATE VIEW log_severity_stats AS
SELECT
    "severity_text",
    "severity_number",
    "count",
    CAST("bucket_day" AS DATE) AS "log_date"
FROM log_severity_stats_daily;

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO schema_version ("version") VALUES ('2.12.0')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();

-- =============================================================================
-- NOTES
-- =============================================================================
--
-- Key conversion notes from SQL Server to PostgreSQL + TimescaleDB:
--
-- 1.  BIGINT IDENTITY(1,1)   BIGINT GENERATED ALWAYS AS IDENTITY
-- 2.  NVARCHAR(n)            VARCHAR(n)  (PostgreSQL is Unicode by default)
-- 3.  NVARCHAR(MAX)          TEXT
-- 4.  FLOAT                  DOUBLE PRECISION
-- 5.  BIT                    BOOLEAN
-- 6.  DATETIME2              TIMESTAMPTZ
-- 7.  SYSDATETIME()          NOW()
-- 8.  ISJSON(col) = 1        Removed; JSONB type enforces valid JSON natively
-- 9.  JSON columns           JSONB for efficient operator-based querying
-- 10. JSON_VALUE(col, '$."key"')  col ->> 'key'
-- 11. DATEADD(SECOND, ns/1e9, '1970-01-01')  to_timestamp(ns / 1000000000.0)
-- 12. CONVERT(NVARCHAR, col)      col::TEXT
-- 13. CAST(x AS FLOAT)     CAST(x AS DOUBLE PRECISION)
-- 14. GO batch separator    Removed (not used in PostgreSQL)
-- 15. uk_trace_span         (TraceId, SpanId, StartTimeUnixNano) -- widened in 2.11.0
--                            so it includes spans' partition column
-- 16. Index names are globally unique (prefixed by table abbreviation where needed)
--
-- TimescaleDB hypertables (partitioned by TimeUnixNano, or StartTimeUnixNano for spans):
--   spans                              = 6-hour chunks (2.11.0)
--   log_records                        = 6-hour chunks
--   gauge_data_points, sum_data_points = 12-hour chunks
--   histogram_data_points,
--   exponential_histogram_data_points,
--   summary_data_points                = 1-day chunks
--
-- spans became a hypertable in schema 2.11.0:
--   span_events and span_links used to hold FK references to spans("id"), which
--   TimescaleDB cannot support once spans is chunked (every unique/PK constraint must
--   include the partition column). 2.11.0 collapsed both child tables into the
--   "events_json"/"links_json" columns on spans, so the PK could be widened to
--   (id, start_time_unix_nano) and uk_trace_span to (trace_id, span_id,
--   start_time_unix_nano) -- and spans finally gets native compression.
--
-- Hypertable leaf tables have no PRIMARY KEY constraint (only GENERATED ALWAYS
-- AS IDENTITY). TimescaleDB disallows unique constraints that exclude the
-- partition column. Since nothing FK-references these tables by Id, a DB-level
-- PK is not needed. EF Core uses Id as the logical primary key and reads the
-- generated value via RETURNING on INSERT.
--
-- Scaling considerations:
-- 1. Adjust chunk_time_interval based on ingestion volume
--    (e.g., 1 hour = 3600000000000 ns for very high-volume environments)
-- 2. Compression defaults are enabled at 7 days for all hypertables.
-- 3. Retention defaults are enabled:
--    - logs: 90 days
--    - metric point hypertables: 180 days
-- 4. Integer-time now function (telemetry_now_ns) is registered for each
--    hypertable so policy jobs operate correctly with BIGINT nanosecond time.
-- 5. Phase 3 query-path indexes include:
--    - resources(service.name expression)
--    - spans(trace,parent-span) and spans/log_records JSONB GIN
--    - log_records(severity,time)
-- 6. Phase 4 adds a continuous aggregate for daily log severity trends with
--    refresh/compression/retention policies and a compatibility view.
-- 7. Phase 5 hardening:
--    - create_hypertable uses if_not_exists => TRUE
--    - views/continuous aggregate are dropped and recreated safely
--    - schema_version write is idempotent via ON CONFLICT
-- 8. Consider continuous aggregates for pre-computed service map metrics

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) Hypertables and chunk interval overview
--    SELECT hypertable_name, chunk_interval
--    FROM timescaledb_information.dimensions
--    WHERE hypertable_name IN (
--      'spans', 'log_records', 'gauge_data_points', 'sum_data_points',
--      'histogram_data_points', 'exponential_histogram_data_points',
--      'summary_data_points'
--    )
--    ORDER BY hypertable_name;
--
-- 2) Compression and policy jobs
--    SELECT hypertable_name, compression_enabled
--    FROM timescaledb_information.hypertables
--    WHERE hypertable_name IN (
--      'spans', 'log_records', 'gauge_data_points', 'sum_data_points',
--      'histogram_data_points', 'exponential_histogram_data_points',
--      'summary_data_points'
--    )
--    ORDER BY hypertable_name;
--
--    SELECT proc_name, hypertable_name, schedule_interval
--    FROM timescaledb_information.jobs
--    ORDER BY proc_name, hypertable_name;
--
-- 3) Continuous aggregate status
--    SELECT view_name, materialized_only, compression_enabled
--    FROM timescaledb_information.continuous_aggregates
--    WHERE view_name = 'log_severity_stats_daily';

