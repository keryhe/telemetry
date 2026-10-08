-- OpenTelemetry PostgreSQL telemetry schema -- telemetry schema 4.0.0
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
--
-- This script is the TELEMETRY half of the schema. Tenants, API keys, alert rules and retention
-- settings are the control plane, in PostgreSQL-ControlPlane.sql (plans/control-plane-split.md). No
-- foreign key crosses the two, so they may share a database or live in two, and either script may
-- be applied first (control plane first is natural: a running collector needs keys before it
-- accepts data). Telemetry schema versions are recorded in telemetry_schema_version.
--
-- Column names use snake_case (double-quoted) while C# models remain PascalCase.
--
-- Spans, log_records and the metric data-point tables are plain heap tables, and data-point time
-- access for retention is served by BRIN indexes on "time_unix_nano". Because the hot tables carry
-- no unique keys, day partitioning can be added later without re-keying.
--
-- Schema 4.0.0 is a fresh-install schema: there is no upgrade path from 2.x or 3.x.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f PostgreSQL-ControlPlane.sql
--   psql -U postgres -d telemetry -f PostgreSQL-Telemetry.sql
--
-- Optional clean reset in an existing local DB before re-running this script:
--   psql -U postgres -d telemetry -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"


-- =============================================================================
-- REFERENCE TABLES (shared across signals)
-- =============================================================================

-- Resource represents the entity producing telemetry. A resource hash is not a resource identity
-- (two tenants running the same service share one), hence UNIQUE (tenant_id, resource_hash).
CREATE TABLE resources (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"       BIGINT       NOT NULL,
    "resource_hash"   CHAR(64)     NOT NULL,
    "schema_url"      VARCHAR(2048),
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    -- Extracted from attributes_json's "service.name" at upsert, and copied onto spans,
    -- log_records and metrics so hot reads filter on a column instead of joining here.
    "service_name"    VARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX idx_resources_tenant_service ON resources ("tenant_id", "service_name");

-- Instrumentation scope (library). Shared across tenants on purpose: UNIQUE (scope_hash).
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

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans. Events and links live in the "events_json"/"links_json" columns on this row.
--
-- A plain append target: no primary key, no unique key (a re-delivered span is stored twice and
-- reads tolerate it -- schema-simplification decision 7), and no foreign keys (reference rows
-- are committed before the data transaction). "id" is an identity used only as the keyset-paging
-- tiebreak. "tenant_id" and "service_name" are copied from the resolved resource at ingest.
-- Ids are TEXT so Npgsql's default text parameter matches the column and uses the indexes.
CREATE TABLE spans (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "tenant_id"               BIGINT       NOT NULL,
    "service_name"            VARCHAR(255),
    "trace_id"                TEXT         NOT NULL,
    "span_id"                 TEXT         NOT NULL,
    "parent_span_id"           TEXT,
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
    "links_json"              JSONB
);

-- Trace detail, span by id, spans by parent within a trace, service-map parent join.
CREATE INDEX idx_spans_trace_span ON spans ("trace_id", "span_id");
-- Unscoped anchors, window scans, search within a window, service-map child range, tenant
-- last-seen, per-tenant retention.
CREATE INDEX idx_spans_tenant_time ON spans ("tenant_id", "start_time_unix_nano");
-- Service-scoped anchors (the trace list's anchor is the selected service's earliest span).
CREATE INDEX idx_spans_tenant_service_time ON spans ("tenant_id", "service_name", "start_time_unix_nano");
-- Errors mode. Error rows are rare, so this stays small.
CREATE INDEX idx_spans_error ON spans ("tenant_id", "start_time_unix_nano") WHERE "status_code" = 'ERROR';

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table. One row per (resource, scope, name, type); "tenant_id" and "service_name"
-- are copied from the resolved resource when the row is first written (cannot go stale: the
-- resource hash includes service.name, so a rename produces a new resource and new metrics rows).
CREATE TABLE metrics (
    "id"          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"    BIGINT       NOT NULL,
    "service_name" VARCHAR(255),
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
    -- Ingestion upsert key. "type" is part of it: the write path picks a data-point table from the
    -- incoming type while the read path picks from the stored type.
    CONSTRAINT uk_metric_identity UNIQUE ("resource_id", "name", "type", "scope_id")
);
-- Catalog (with or without a service filter) and by-name lookups.
CREATE INDEX idx_metrics_tenant_service_name ON metrics ("tenant_id", "service_name", "name");

-- Data-point tables: no primary key, no foreign key, no unique key. "id" is the keyset tiebreak.
-- exemplars_json holds the OTLP exemplar list (every data point except Summary).

CREATE TABLE gauge_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "value_double"       DOUBLE PRECISION,
    "value_int"          BIGINT,
    "flags"             INTEGER          DEFAULT 0,
    "attributes_json"    JSONB,
    "exemplars_json"     JSONB
);

-- Series reads and raw-point keyset paging.
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano", "id");
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_gauge_time_brin ON gauge_data_points USING BRIN ("time_unix_nano");

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
    "exemplars_json"          JSONB
);

-- Series reads and raw-point keyset paging.
CREATE INDEX idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano", "id");
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_sum_time_brin ON sum_data_points USING BRIN ("time_unix_nano");

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
    "exemplars_json"          JSONB
);

-- Series reads and raw-point keyset paging.
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano", "id");
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_histogram_time_brin ON histogram_data_points USING BRIN ("time_unix_nano");

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
    "exemplars_json"          JSONB
);

-- Series reads and raw-point keyset paging.
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano", "id");
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_exp_histogram_time_brin ON exponential_histogram_data_points USING BRIN ("time_unix_nano");

CREATE TABLE summary_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "metric_id"          BIGINT           NOT NULL,
    "start_time_unix_nano" BIGINT,
    "time_unix_nano"      BIGINT           NOT NULL,
    "count"             BIGINT           NOT NULL,
    "sum_value"          DOUBLE PRECISION NOT NULL,
    "quantile_values"    JSONB,
    "flags"             INTEGER          DEFAULT 0,
    "attributes_json"    JSONB
);

-- Series reads and raw-point keyset paging.
CREATE INDEX idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano", "id");
-- Time access for retention (a batched delete by time).
CREATE INDEX idx_summary_time_brin ON summary_data_points USING BRIN ("time_unix_nano");

-- metric_last_seen: the metrics catalog's "has data in range" check reads this instead of scanning
-- the five data-point tables. No FK to "metrics" (a FK would make every touch lock-check the
-- metrics row). Written by the collector's MetricTouchWorker on a periodic interval.
CREATE TABLE metric_last_seen (
    "metric_id"            BIGINT NOT NULL PRIMARY KEY,
    "last_seen_unix_nano"  BIGINT NOT NULL
);
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen ("last_seen_unix_nano");

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records. Same append-target shape as spans: no key, no foreign keys. TimeUnixNano is
-- NOT NULL with DEFAULT 0 to handle edge-case OTLP records.
CREATE TABLE log_records (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "tenant_id"               BIGINT       NOT NULL,
    "service_name"            VARCHAR(255),
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
    "trace_id"                TEXT,
    "span_id"                 TEXT,
    "created_at"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json"         JSONB
);

-- List paging, windows, search, per-tenant retention. The service filter is a residual predicate.
CREATE INDEX idx_log_tenant_time_id ON log_records ("tenant_id", "time_unix_nano", "id");
-- Logs for a trace.
CREATE INDEX idx_log_trace ON log_records ("trace_id");

-- =============================================================================
-- SUMMARY ROLLUPS (schema 3.2.0; plans/summary-rollups.md)
-- =============================================================================

-- Per-minute rollups the dashboard, trace list and logs page read instead of scanning spans and
-- log records. request_rollup_minute counts INBOUND spans (kind SERVER or CONSUMER) per
-- (tenant, service, minute of start time): request_count, error_count, duration sum/max and a
-- 24-band doubling duration histogram (h00 = under 0.25 ms, hNN = [0.25 ms * 2^(NN-1), 0.25 ms * 2^NN),
-- h23 = 1,048.6 s and over). log_rollup_minute counts log records per (tenant, service, severity
-- number, minute); severity_number is -1 for a record whose severity is NULL. Rows are partial:
-- no unique key, no foreign keys, reads SUM them. service_name is '' for a span or log without one.
CREATE TABLE request_rollup_minute (
    "tenant_id"              BIGINT       NOT NULL,
    "service_name"            VARCHAR(255) NOT NULL DEFAULT '',
    "bucket_start_unix_nano"   BIGINT       NOT NULL,
    "request_count"           BIGINT       NOT NULL,
    "error_count"             BIGINT       NOT NULL,
    "sum_duration_nanos"       BIGINT       NOT NULL,
    "max_duration_nanos"       BIGINT       NOT NULL,
    "h00" BIGINT NOT NULL DEFAULT 0,
    "h01" BIGINT NOT NULL DEFAULT 0,
    "h02" BIGINT NOT NULL DEFAULT 0,
    "h03" BIGINT NOT NULL DEFAULT 0,
    "h04" BIGINT NOT NULL DEFAULT 0,
    "h05" BIGINT NOT NULL DEFAULT 0,
    "h06" BIGINT NOT NULL DEFAULT 0,
    "h07" BIGINT NOT NULL DEFAULT 0,
    "h08" BIGINT NOT NULL DEFAULT 0,
    "h09" BIGINT NOT NULL DEFAULT 0,
    "h10" BIGINT NOT NULL DEFAULT 0,
    "h11" BIGINT NOT NULL DEFAULT 0,
    "h12" BIGINT NOT NULL DEFAULT 0,
    "h13" BIGINT NOT NULL DEFAULT 0,
    "h14" BIGINT NOT NULL DEFAULT 0,
    "h15" BIGINT NOT NULL DEFAULT 0,
    "h16" BIGINT NOT NULL DEFAULT 0,
    "h17" BIGINT NOT NULL DEFAULT 0,
    "h18" BIGINT NOT NULL DEFAULT 0,
    "h19" BIGINT NOT NULL DEFAULT 0,
    "h20" BIGINT NOT NULL DEFAULT 0,
    "h21" BIGINT NOT NULL DEFAULT 0,
    "h22" BIGINT NOT NULL DEFAULT 0,
    "h23" BIGINT NOT NULL DEFAULT 0
);
CREATE TABLE log_rollup_minute (
    "tenant_id"              BIGINT       NOT NULL,
    "service_name"            VARCHAR(255) NOT NULL DEFAULT '',
    "severity_number"         INTEGER      NOT NULL,
    "bucket_start_unix_nano"   BIGINT       NOT NULL,
    "record_count"            BIGINT       NOT NULL
);
CREATE INDEX idx_request_rollup_minute ON request_rollup_minute ("tenant_id", "bucket_start_unix_nano", "service_name");
CREATE INDEX idx_log_rollup_minute ON log_rollup_minute ("tenant_id", "bucket_start_unix_nano", "service_name");

-- =============================================================================
-- SCHEMA VERSION TABLE
-- =============================================================================

CREATE TABLE telemetry_schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- NOTE: the telemetry_schema_version row is seeded at the very END of this script, so a partial/failed
-- apply never records a version that the apply-schema.sh version gate would wrongly treat as
-- "already applied".

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO telemetry_schema_version ("version") VALUES ('4.0.0')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();
