-- Consolidated PostgreSQL migration: brings a database from ANY existing schema version
-- (2.6.0, 2.10.0, 2.11.0, 2.12.0, 2.13.0, 2.13.1, 2.13.2, 2.13.3, a pre-tracking database with no
-- schema_version table/row at all, or a completely fresh/empty database) up to 2.13.3.
--
-- This SUPERSEDES the former per-version chain (PostgreSQL-2.6.0-to-2.10.0.sql,
-- PostgreSQL-2.11.0-to-2.12.0.sql, PostgreSQL-2.12.0-to-2.13.0.sql,
-- PostgreSQL-2.13.0-to-2.13.1.sql, PostgreSQL-2.13.1-to-2.13.2.sql,
-- PostgreSQL-2.13.2-to-2.13.3.sql) which had to be run by hand in exact order. Those files are
-- deleted; this is the one script to run against an existing PostgreSQL database from now on.
-- For a brand-new database, PostgreSQL-Schema.sql remains the normal from-scratch install path,
-- but this script also produces an identical end state if pointed at an empty database.
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f PostgreSQL-Migrate.sql
--
-- Design:
--   * Every statement is naturally idempotent (CREATE TABLE/INDEX IF NOT EXISTS, ADD COLUMN IF
--     NOT EXISTS, DROP ... IF EXISTS) or wrapped in a DO $$ ... $$ block that inspects
--     pg_catalog/information_schema first and only acts if the change is not already present.
--     Views are DROP VIEW IF EXISTS + CREATE VIEW rather than CREATE OR REPLACE VIEW, because a
--     database migrating from before resources.service_name existed has service_map/
--     service_map_detailed defined with a TEXT-typed expression column that CREATE OR REPLACE
--     VIEW cannot retype in place -- see STEP 10's own comment.
--   * Large data backfills (metric_last_seen, resources.service_name) are gated so a database
--     already past that point does not rescan the underlying tables on every run.
--   * The whole script runs in ONE transaction: either the database ends up at 2.13.3, or (bar
--     the CREATE INDEX statements noted below) it is left exactly as it was.
--   * NOTE ON CONCURRENCY: like the former PostgreSQL-2.13.2-to-2.13.3.sql, the pg_trgm/GIN
--     indexes added below use plain CREATE INDEX, not CONCURRENTLY (which cannot run inside a
--     transaction block) -- on a large existing installation this holds a SHARE lock on the
--     table for the duration of the build (writes blocked, reads unaffected). Consider running
--     those CREATE INDEX statements individually with CONCURRENTLY outside this transaction on a
--     live production database instead of pasting this whole script in one go.
--
-- What changed from 2.6.0 to 2.13.3, and why (full detail preserved from the original per-version
-- files; see CLAUDE.md / plans/telemetry-retention.md / plans/list-pages-server-side.md /
-- plans/span-events-links-json-hypertable.md for the complete rationale):
--
--   2.7.0  metrics gained uk_metric_identity UNIQUE (resource_id, name, type, scope_id), so the
--          catalog holds one row per metric identity instead of one row per OTLP export cycle.
--          Existing databases therefore carry duplicates that must be collapsed before the
--          constraint can be created. idx_resource_name became an exact left prefix of the new
--          constraint and is dropped.
--   2.8.0  Six indexes dropped from spans/log_records as provably redundant or unused: four
--          B-tree (idx_trace_id, idx_start_time, idx_kind, idx_status) and the two GIN indexes on
--          attributes_json, which no read-path query used for containment at the time.
--   2.9.0  Exemplars moved onto the data point that owns them. The single exemplar_id column
--          could hold only one exemplar where OTLP allows many, and no writer ever populated it,
--          so no data is lost by dropping it or the shared exemplars table.
--   2.10.0 Retention scheduling moved to a single application-level mechanism (Keryhe.Telemetry.
--          Api's RetentionWorker) driven by a new retention_settings table, on every provider.
--          On plain PostgreSQL there were never any native TimescaleDB retention policies to
--          remove; this script detects TimescaleDB automatically and is a no-op without it.
--   2.11.0 span_events/span_links were never read or written independently of their parent span,
--          so both collapse into two new nullable columns on spans: events_json/links_json (NULL
--          means "no events"/"no links"), matching how exemplars_json uses NULL for the same
--          reason. Existing rows are folded into the parent span row before the child tables (and
--          their FKs/indexes) are dropped, or that data would be lost. (Plain PostgreSQL, unlike
--          TimescaleDB, never needed spans' primary key or uk_trace_span widened to include
--          start_time_unix_nano -- that requirement was specific to hypertable partitioning.)
--   2.12.0 idx_spans_error added: a partial index on spans(start_time_unix_nano DESC) WHERE
--          status_code = 'ERROR', backing mode=errors trace queries. Not a reversal of
--          idx_status's 2.8.0 removal -- that was a plain B-tree over all three status values,
--          too low-cardinality for the planner to ever choose; this covers only the rare error
--          rows.
--   2.13.0 idx_log_time (time_unix_nano DESC) replaced by idx_log_time_id (time_unix_nano DESC,
--          id DESC), the keyset-paging tiebreak GetLogPageAsync needs -- the old index is a pure
--          left prefix of the new one. rollup_state/log_rollup_minute/log_rollup_hour added,
--          backing RollupWorker, seeded with the two rows this phase needs (logs/minute,
--          logs/hour).
--   2.13.1 idx_spans_root_time: a partial index anchoring the trace page/summary on root spans
--          (INCLUDE end_time_unix_nano so mode=slow's duration check runs inside the index).
--          orphan_roots added: traces whose root span never arrived. trace_rollup_minute/
--          trace_rollup_hour added (per-minute/hour trace summary tables), plus their
--          rollup_state seed rows (traces/minute, traces/hour).
--   2.13.2 metric_last_seen: a new table (NOT a column on metrics) that the metrics catalog reads
--          for its "has data in range" check instead of scanning the five data-point tables. No
--          FK to metrics -- see the table's own comment in PostgreSQL-Schema.sql. Backfilled from
--          MAX(time_unix_nano) per metric_id across the five data-point tables, in batches of
--          5,000 metric ids at a time so the migration does not hold one enormous scan/lock per
--          table.
--   2.13.3 resources.service_name: a real column, backfilled from attributes_json's
--          "service.name" key, replacing the expression index that used to sit on it.
--          pg_trgm GIN indexes on spans.name / spans.status_message / log_records.body_value back
--          FreeTextPredicate's existing ILIKE '%text%' predicate with no SQL change. GIN
--          jsonb_path_ops indexes on spans.attributes_json / log_records.attributes_json back
--          AttributePredicate's typed-containment form (@>) for positive (non-negated) key:value
--          matches -- this SUPERSEDES the 2.8.0 removal of the (differently-defined) indexes of
--          the same name, so this script explicitly checks the existing index definition and
--          rebuilds it if it predates jsonb_path_ops, rather than assuming CREATE INDEX IF NOT
--          EXISTS is enough.

BEGIN;

-- pg_trgm is needed by the final index set below (gin_trgm_ops) and is safe to create this early.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- =============================================================================
-- STEP 0 -- utility table (must exist before anything below can be gated on it)
-- =============================================================================

CREATE TABLE IF NOT EXISTS schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- =============================================================================
-- STEP 1 -- base tables, final structure (CREATE TABLE IF NOT EXISTS)
--
-- This block alone is what makes the script correct against a completely empty database: every
-- table is created with its FINAL (2.13.3) column/constraint set. Against a database that
-- already has a given table (any older schema version from 2.6.0 up), each statement here is a
-- no-op -- the ALTER/backfill steps further down are what bring that pre-existing table's shape
-- and data up to date.
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenants (
    "id"        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"      VARCHAR(255) NOT NULL,
    "created_at" TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT uk_tenant_name UNIQUE ("name")
);

CREATE TABLE IF NOT EXISTS api_keys (
    "id"         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"   BIGINT       NOT NULL REFERENCES tenants("id") ON DELETE CASCADE,
    "key_hash"    CHAR(64)     NOT NULL,
    "name"       VARCHAR(255) NOT NULL,
    "is_active"   BOOLEAN      NOT NULL DEFAULT TRUE,
    "created_at"  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "last_used_at" TIMESTAMPTZ,
    CONSTRAINT uk_api_key_hash UNIQUE ("key_hash")
);
CREATE INDEX IF NOT EXISTS idx_api_keys_tenant_id ON api_keys ("tenant_id");

CREATE TABLE IF NOT EXISTS resources (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"       BIGINT       NOT NULL DEFAULT 1 REFERENCES tenants("id"),
    "resource_hash"   CHAR(64)     NOT NULL,
    "schema_url"      VARCHAR(2048),
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    "service_name"    VARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX IF NOT EXISTS idx_resources_tenant_id ON resources ("tenant_id");
CREATE INDEX IF NOT EXISTS idx_created_at ON resources ("created_at");

CREATE TABLE IF NOT EXISTS instrumentation_scopes (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "name"           VARCHAR(255) NOT NULL,
    "version"        VARCHAR(255),
    "schema_url"      VARCHAR(2048),
    "scope_hash"      CHAR(64)     NOT NULL,
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_scope_hash UNIQUE ("scope_hash")
);
CREATE INDEX IF NOT EXISTS idx_name_version ON instrumentation_scopes ("name", "version");

CREATE TABLE IF NOT EXISTS spans (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
    CONSTRAINT uk_trace_span      UNIQUE ("trace_id", "span_id")
);
CREATE INDEX IF NOT EXISTS idx_span_id            ON spans ("span_id");
CREATE INDEX IF NOT EXISTS idx_parent_span        ON spans ("parent_span_id");
CREATE INDEX IF NOT EXISTS idx_spans_trace_parent ON spans ("trace_id", "parent_span_id");
CREATE INDEX IF NOT EXISTS idx_end_time           ON spans ("end_time_unix_nano"   DESC);
CREATE INDEX IF NOT EXISTS idx_duration           ON spans ("start_time_unix_nano", "end_time_unix_nano");
CREATE INDEX IF NOT EXISTS idx_spans_name         ON spans ("name");
CREATE INDEX IF NOT EXISTS idx_spans_resource_time ON spans ("resource_id", "start_time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_spans_error ON spans ("start_time_unix_nano" DESC) WHERE "status_code" = 'ERROR';
CREATE INDEX IF NOT EXISTS idx_spans_root_time ON spans ("start_time_unix_nano" DESC) INCLUDE ("end_time_unix_nano")
    WHERE "parent_span_id" IS NULL;

CREATE TABLE IF NOT EXISTS metrics (
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
    CONSTRAINT uk_metric_identity UNIQUE ("resource_id", "name", "type", "scope_id")
);
CREATE INDEX IF NOT EXISTS idx_metrics_name  ON metrics ("name");
CREATE INDEX IF NOT EXISTS idx_type          ON metrics ("type");

CREATE TABLE IF NOT EXISTS gauge_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_gauge_time_brin   ON gauge_data_points USING BRIN ("time_unix_nano");

CREATE TABLE IF NOT EXISTS sum_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_sum_time_brin   ON sum_data_points USING BRIN ("time_unix_nano");
CREATE INDEX IF NOT EXISTS idx_temporality     ON sum_data_points ("aggregation_temporality");

CREATE TABLE IF NOT EXISTS histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_histogram_time_brin   ON histogram_data_points USING BRIN ("time_unix_nano");

CREATE TABLE IF NOT EXISTS exponential_histogram_data_points (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_exp_histogram_time_brin   ON exponential_histogram_data_points USING BRIN ("time_unix_nano");

CREATE TABLE IF NOT EXISTS summary_data_points (
    "id"                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_summary_time_brin   ON summary_data_points USING BRIN ("time_unix_nano");

CREATE TABLE IF NOT EXISTS metric_last_seen (
    "metric_id"            BIGINT NOT NULL PRIMARY KEY,
    "last_seen_unix_nano"  BIGINT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_metric_last_seen_last_seen ON metric_last_seen ("last_seen_unix_nano");

CREATE TABLE IF NOT EXISTS log_records (
    "id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
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
CREATE INDEX IF NOT EXISTS idx_log_time_id       ON log_records ("time_unix_nano" DESC, "id" DESC);
CREATE INDEX IF NOT EXISTS idx_log_time_brin     ON log_records USING BRIN ("time_unix_nano");
CREATE INDEX IF NOT EXISTS idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_severity          ON log_records ("severity_number");
CREATE INDEX IF NOT EXISTS idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX IF NOT EXISTS idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);

CREATE TABLE IF NOT EXISTS rollup_state (
    "signal_name"                   VARCHAR(20)  NOT NULL,
    "granularity"              VARCHAR(10)  NOT NULL,
    "coverage_start_unix_nano"  BIGINT,
    "rolled_until_unix_nano"    BIGINT       NOT NULL DEFAULT 0,
    "repassed_until_unix_nano"  BIGINT       NOT NULL DEFAULT 0,
    "lease_owner"              VARCHAR(100),
    "lease_expires_at"          TIMESTAMPTZ  NOT NULL DEFAULT 'epoch',
    PRIMARY KEY ("signal_name", "granularity")
);

CREATE TABLE IF NOT EXISTS log_rollup_minute (
    "bucket_unix_nano" BIGINT  NOT NULL,
    "resource_id"      BIGINT  NOT NULL,
    "trace_count"      INTEGER NOT NULL DEFAULT 0,
    "debug_count"      INTEGER NOT NULL DEFAULT 0,
    "info_count"       INTEGER NOT NULL DEFAULT 0,
    "warn_count"       INTEGER NOT NULL DEFAULT 0,
    "error_count"      INTEGER NOT NULL DEFAULT 0,
    "fatal_count"      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id")
);
CREATE INDEX IF NOT EXISTS idx_log_rollup_minute_bucket ON log_rollup_minute ("bucket_unix_nano");

CREATE TABLE IF NOT EXISTS log_rollup_hour (
    "bucket_unix_nano" BIGINT  NOT NULL,
    "resource_id"      BIGINT  NOT NULL,
    "trace_count"      INTEGER NOT NULL DEFAULT 0,
    "debug_count"      INTEGER NOT NULL DEFAULT 0,
    "info_count"       INTEGER NOT NULL DEFAULT 0,
    "warn_count"       INTEGER NOT NULL DEFAULT 0,
    "error_count"      INTEGER NOT NULL DEFAULT 0,
    "fatal_count"      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id")
);
CREATE INDEX IF NOT EXISTS idx_log_rollup_hour_bucket ON log_rollup_hour ("bucket_unix_nano");

CREATE TABLE IF NOT EXISTS orphan_roots (
    "trace_id"             CHAR(32)     NOT NULL PRIMARY KEY,
    "span_id"              CHAR(16)     NOT NULL,
    "resource_id"          BIGINT       NOT NULL,
    "start_time_unix_nano" BIGINT       NOT NULL,
    "end_time_unix_nano"   BIGINT       NOT NULL,
    "detected_at"          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_orphan_roots_start ON orphan_roots ("start_time_unix_nano" DESC, "trace_id");

CREATE TABLE IF NOT EXISTS trace_rollup_minute (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0, "lb_01" INTEGER NOT NULL DEFAULT 0, "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0, "lb_04" INTEGER NOT NULL DEFAULT 0, "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0, "lb_07" INTEGER NOT NULL DEFAULT 0, "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0, "lb_10" INTEGER NOT NULL DEFAULT 0, "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0, "lb_13" INTEGER NOT NULL DEFAULT 0, "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0, "lb_16" INTEGER NOT NULL DEFAULT 0, "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0, "lb_19" INTEGER NOT NULL DEFAULT 0, "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0, "lb_22" INTEGER NOT NULL DEFAULT 0, "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0, "lb_25" INTEGER NOT NULL DEFAULT 0, "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0, "lb_28" INTEGER NOT NULL DEFAULT 0, "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0, "lb_31" INTEGER NOT NULL DEFAULT 0, "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0, "lb_34" INTEGER NOT NULL DEFAULT 0, "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0, "lb_37" INTEGER NOT NULL DEFAULT 0, "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX IF NOT EXISTS idx_trace_rollup_minute_bucket ON trace_rollup_minute ("bucket_unix_nano");

CREATE TABLE IF NOT EXISTS trace_rollup_hour (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0, "lb_01" INTEGER NOT NULL DEFAULT 0, "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0, "lb_04" INTEGER NOT NULL DEFAULT 0, "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0, "lb_07" INTEGER NOT NULL DEFAULT 0, "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0, "lb_10" INTEGER NOT NULL DEFAULT 0, "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0, "lb_13" INTEGER NOT NULL DEFAULT 0, "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0, "lb_16" INTEGER NOT NULL DEFAULT 0, "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0, "lb_19" INTEGER NOT NULL DEFAULT 0, "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0, "lb_22" INTEGER NOT NULL DEFAULT 0, "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0, "lb_25" INTEGER NOT NULL DEFAULT 0, "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0, "lb_28" INTEGER NOT NULL DEFAULT 0, "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0, "lb_31" INTEGER NOT NULL DEFAULT 0, "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0, "lb_34" INTEGER NOT NULL DEFAULT 0, "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0, "lb_37" INTEGER NOT NULL DEFAULT 0, "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX IF NOT EXISTS idx_trace_rollup_hour_bucket ON trace_rollup_hour ("bucket_unix_nano");

CREATE TABLE IF NOT EXISTS alert_rules (
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
CREATE INDEX IF NOT EXISTS idx_alert_rules_tenant_id ON alert_rules ("tenant_id");
CREATE INDEX IF NOT EXISTS idx_alert_rules_tenant_enabled ON alert_rules ("tenant_id", "enabled") WHERE "enabled" = TRUE;

CREATE TABLE IF NOT EXISTS alert_events (
    "id"          BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "rule_id"      INTEGER      NOT NULL,
    "fired_at"     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "details_json" JSONB        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY ("rule_id") REFERENCES alert_rules ("id") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_alert_events_rule_id  ON alert_events ("rule_id");
CREATE INDEX IF NOT EXISTS idx_alert_events_fired_at ON alert_events ("fired_at" DESC);

CREATE TABLE IF NOT EXISTS retention_settings (
    "id"                    SMALLINT     PRIMARY KEY DEFAULT 1,
    "trace_retention_days"   INTEGER      NOT NULL,
    "log_retention_days"     INTEGER      NOT NULL,
    "metric_retention_days"  INTEGER      NOT NULL,
    "updated_at"             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_retention_settings_singleton CHECK ("id" = 1)
);

-- =============================================================================
-- STEP 2 -- obsolete objects removed unconditionally
--
-- These index/table/column names are never reused with a different definition anywhere in the
-- final (2.13.3) schema, so an unconditional DROP ... IF EXISTS is always correct and, after the
-- first run, a cheap catalog-only no-op.
-- =============================================================================

-- 2.7.0: idx_resource_name became a left prefix of uk_metric_identity (added further below).
DROP INDEX IF EXISTS idx_resource_name;

-- 2.8.0: provably redundant/unused at the time (see header). idx_spans_attributes_gin and
-- idx_log_attributes_gin are handled separately below, because those two names were reused with a
-- different (jsonb_path_ops) definition in 2.13.3.
DROP INDEX IF EXISTS idx_trace_id;
DROP INDEX IF EXISTS idx_start_time;
DROP INDEX IF EXISTS idx_kind;
DROP INDEX IF EXISTS idx_status;

-- 2.13.0: idx_log_time (time_unix_nano DESC) is a pure left prefix of idx_log_time_id
-- (time_unix_nano DESC, id DESC), created in STEP 1 above.
DROP INDEX IF EXISTS idx_log_time;

-- 2.9.0: exemplar_id was never written by any provider, so dropping it discards nothing.
ALTER TABLE gauge_data_points                     DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE sum_data_points                       DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE histogram_data_points                 DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE exponential_histogram_data_points     DROP COLUMN IF EXISTS "exemplar_id";

-- 2.9.0: exemplars_json columns (STEP 1 already added these for both fresh and pre-existing
-- tables via the inline CREATE TABLE definitions above; ADD COLUMN IF NOT EXISTS below covers a
-- pre-existing table from before 2.9.0 that STEP 1 did not touch because the table already
-- existed).
ALTER TABLE gauge_data_points                 ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE sum_data_points                   ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE histogram_data_points             ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE exponential_histogram_data_points ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;

-- 2.9.0: always empty (nothing ever inserted into it), and nothing references it -- the
-- data-point exemplar_id columns carried no foreign key.
DROP TABLE IF EXISTS exemplars;

-- 2.13.3: resources.service_name (STEP 1 covers a fresh install; this covers a pre-existing
-- resources table from before 2.13.3).
ALTER TABLE resources ADD COLUMN IF NOT EXISTS "service_name" VARCHAR(255);

-- =============================================================================
-- STEP 3 -- 2.11.0: fold span_events/span_links into spans.events_json/links_json, then drop
-- the child tables
-- =============================================================================

-- events_json/links_json (STEP 1 covers a fresh install; this covers a pre-existing spans table
-- from before 2.11.0).
ALTER TABLE spans ADD COLUMN IF NOT EXISTS "events_json" JSONB;
ALTER TABLE spans ADD COLUMN IF NOT EXISTS "links_json"  JSONB;

-- Guarded on to_regclass so this whole step is a no-op on a database already migrated (or created
-- fresh, where span_events/span_links never existed). The JSON shape matches exactly what
-- System.Text.Json produces for List<SpanEventModel>/List<SpanLinkModel> with no naming policy
-- configured, i.e. the C# property names verbatim (PascalCase) -- that is what the read path
-- (Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs) deserializes on the other end.
-- Unlike TimescaleDB, plain PostgreSQL never needed spans' primary key or uk_trace_span widened
-- to include start_time_unix_nano (that was a hypertable-partitioning requirement only), and
-- spans is never converted into a hypertable here.
DO $$
BEGIN
    IF to_regclass('span_events') IS NOT NULL THEN
        WITH events_agg AS (
            SELECT
                "span_id",
                jsonb_agg(
                    jsonb_build_object(
                        'Name',                   "name",
                        'TimeUnixNano',           "time_unix_nano",
                        'DroppedAttributesCount', "dropped_attributes_count",
                        'Attributes',             "attributes_json"
                    )
                    ORDER BY "time_unix_nano"
                ) AS events_json
            FROM span_events
            GROUP BY "span_id"
        )
        UPDATE spans s
        SET "events_json" = e.events_json
        FROM events_agg e
        WHERE s."id" = e."span_id" AND s."events_json" IS NULL;
    END IF;

    IF to_regclass('span_links') IS NOT NULL THEN
        WITH links_agg AS (
            SELECT
                "span_id",
                jsonb_agg(
                    jsonb_build_object(
                        'LinkedTraceIdHex',       "linked_trace_id",
                        'LinkedSpanIdHex',        "linked_span_id",
                        'TraceState',             "trace_state",
                        'Flags',                  "flags",
                        'DroppedAttributesCount', "dropped_attributes_count",
                        'Attributes',             "attributes_json"
                    )
                ) AS links_json
            FROM span_links
            GROUP BY "span_id"
        )
        UPDATE spans s
        SET "links_json" = l.links_json
        FROM links_agg l
        WHERE s."id" = l."span_id" AND s."links_json" IS NULL;
    END IF;
END $$;

-- Also drops fk_span_events_spans/fk_span_links_spans and their own indexes, since those belong
-- to the dropped tables.
DROP TABLE IF EXISTS span_events;
DROP TABLE IF EXISTS span_links;

-- =============================================================================
-- STEP 4 -- 2.7.0: collapse duplicate metrics rows, then add uk_metric_identity
--
-- Gated on the constraint's own existence (not a schema_version flag) so a database that already
-- has it -- whether created fresh via STEP 1 above or migrated by an earlier run of this script --
-- never re-scans the metrics table.
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uk_metric_identity' AND conrelid = 'metrics'::regclass
    ) THEN
        -- Survivor per identity = MIN(id), i.e. the earliest row, whose created_at is the
        -- earliest this metric was seen -- exactly what created_at means from 2.7.0 on
        -- ("first seen"), so the surviving row already carries the right timestamp.
        CREATE TEMP TABLE metric_dedup_map ON COMMIT DROP AS
        SELECT "id" AS old_id,
               min("id") OVER (PARTITION BY "resource_id", "name", "type", "scope_id") AS new_id
        FROM metrics;

        DELETE FROM metric_dedup_map WHERE old_id = new_id;

        CREATE INDEX ON metric_dedup_map (old_id);

        -- Repoint data points BEFORE deleting anything: the data-point foreign keys are ON DELETE
        -- CASCADE, so deleting a duplicate metrics row first would take its data points with it.
        UPDATE gauge_data_points dp
           SET "metric_id" = m.new_id FROM metric_dedup_map m WHERE dp."metric_id" = m.old_id;
        UPDATE sum_data_points dp
           SET "metric_id" = m.new_id FROM metric_dedup_map m WHERE dp."metric_id" = m.old_id;
        UPDATE histogram_data_points dp
           SET "metric_id" = m.new_id FROM metric_dedup_map m WHERE dp."metric_id" = m.old_id;
        UPDATE exponential_histogram_data_points dp
           SET "metric_id" = m.new_id FROM metric_dedup_map m WHERE dp."metric_id" = m.old_id;
        UPDATE summary_data_points dp
           SET "metric_id" = m.new_id FROM metric_dedup_map m WHERE dp."metric_id" = m.old_id;

        -- Now childless, so the cascade removes nothing.
        DELETE FROM metrics WHERE "id" IN (SELECT old_id FROM metric_dedup_map);

        ALTER TABLE metrics
            ADD CONSTRAINT uk_metric_identity UNIQUE ("resource_id", "name", "type", "scope_id");
    END IF;
END $$;

-- =============================================================================
-- STEP 5 -- 2.13.3: rebuild idx_spans_attributes_gin / idx_log_attributes_gin if they predate
-- jsonb_path_ops
--
-- These two names were dropped in 2.8.0 (no read-path query did JSONB containment at the time)
-- and re-added in 2.13.3 with a different definition once AttributePredicate's typed-containment
-- form needed one. A plain CREATE INDEX IF NOT EXISTS would silently keep a stale pre-2.8.0
-- definition if one still exists, so the actual index definition is checked first.
-- =============================================================================

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE indexname = 'idx_spans_attributes_gin' AND indexdef NOT LIKE '%jsonb_path_ops%'
    ) THEN
        DROP INDEX idx_spans_attributes_gin;
    END IF;

    IF EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE indexname = 'idx_log_attributes_gin' AND indexdef NOT LIKE '%jsonb_path_ops%'
    ) THEN
        DROP INDEX idx_log_attributes_gin;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_spans_attributes_gin ON spans USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_spans_name_trgm ON spans USING GIN ("name" gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_spans_status_message_trgm ON spans USING GIN ("status_message" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_attributes_gin ON log_records USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_log_body_trgm ON log_records USING GIN ("body_value" gin_trgm_ops);

-- =============================================================================
-- STEP 6 -- 2.13.3: backfill resources.service_name from attributes_json, then rebuild its index
-- as a plain column index instead of the old expression index
--
-- Gated on schema_version so a database already past 2.13.3 does not rescan resources (normally a
-- much smaller table than the signal tables, but still no reason to repeat the scan on every
-- run).
-- =============================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM schema_version WHERE "version" = '2.13.3') THEN
        UPDATE resources
        SET "service_name" = "attributes_json" ->> 'service.name'
        WHERE "service_name" IS NULL AND "attributes_json" ? 'service.name';
    END IF;
END $$;

DROP INDEX IF EXISTS idx_resources_service_name;
CREATE INDEX IF NOT EXISTS idx_resources_service_name ON resources ("service_name");

-- =============================================================================
-- STEP 7 -- 2.13.2: backfill metric_last_seen from the five data-point tables, batched by
-- metric_id
--
-- Gated on schema_version so a database already past 2.13.2 never repeats this scan: on a large
-- installation this backfill is the slow part of the upgrade (each data-point table is scanned
-- once per metric-id batch via the existing (metric_id, time_unix_nano DESC) index). The batch
-- loop is safe to interrupt and re-run even without the gate: every INSERT is an idempotent
-- upsert keeping the greater value, matching MetricTouchWorker's own conflict resolution -- the
-- gate is purely to avoid redoing the scan once it has already completed.
-- =============================================================================

DO $$
DECLARE
    batch_size CONSTANT BIGINT := 5000;
    min_id BIGINT;
    max_id BIGINT;
    lo BIGINT;
    hi BIGINT;
BEGIN
    IF EXISTS (SELECT 1 FROM schema_version WHERE "version" = '2.13.2') THEN
        RETURN;
    END IF;

    SELECT MIN("id"), MAX("id") INTO min_id, max_id FROM metrics;
    IF min_id IS NULL THEN
        RETURN;
    END IF;

    lo := min_id;
    WHILE lo <= max_id LOOP
        hi := lo + batch_size - 1;

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM gauge_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM sum_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM histogram_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM exponential_histogram_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM summary_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        lo := hi + 1;
    END LOOP;
END $$;

-- =============================================================================
-- STEP 8 -- 2.10.0: retention_settings seed row (table itself created in STEP 1)
-- =============================================================================

INSERT INTO retention_settings ("id", "trace_retention_days", "log_retention_days", "metric_retention_days")
VALUES (1, 90, 90, 180)
ON CONFLICT ("id") DO NOTHING;

-- 2.10.0: remove Timescale's native retention jobs. Plain PostgreSQL never has the timescaledb
-- extension, so this is always a no-op here -- kept for parity with the original migration
-- history and in case this script is ever pointed at a database where the extension was installed
-- out of band.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') THEN
        PERFORM remove_retention_policy('log_records', if_exists => TRUE);
        PERFORM remove_retention_policy('gauge_data_points', if_exists => TRUE);
        PERFORM remove_retention_policy('sum_data_points', if_exists => TRUE);
        PERFORM remove_retention_policy('histogram_data_points', if_exists => TRUE);
        PERFORM remove_retention_policy('exponential_histogram_data_points', if_exists => TRUE);
        PERFORM remove_retention_policy('summary_data_points', if_exists => TRUE);
    END IF;
END $$;

-- =============================================================================
-- STEP 9 -- 2.13.0/2.13.1: rollup_state seed rows (tables themselves created in STEP 1)
-- =============================================================================

INSERT INTO rollup_state ("signal_name", "granularity")
VALUES ('logs', 'minute'), ('logs', 'hour'), ('traces', 'minute'), ('traces', 'hour')
ON CONFLICT ("signal_name", "granularity") DO NOTHING;

-- =============================================================================
-- STEP 10 -- views
--
-- Dropped and recreated rather than CREATE OR REPLACE: a database migrating from 2.6.0 (or any
-- version before resources.service_name existed as a real column) has service_map/
-- service_map_detailed defined with parent_res."attributes_json" ->> 'service.name' (type TEXT)
-- for parent_service/child_service, whereas the 2.13.3 shape below selects resources.service_name
-- (VARCHAR(255)) instead. CREATE OR REPLACE VIEW refuses to change an existing column's type
-- ("cannot change data type of view column ... from text to character varying"), so the old
-- definition must be dropped first -- matching what PostgreSQL-Schema.sql itself does (DROP VIEW
-- IF EXISTS ... CREATE VIEW) for exactly this reason. trace_summary/log_severity_stats never
-- changed shape, but are dropped and recreated the same way for consistency and because DROP VIEW
-- IF EXISTS is itself idempotent.
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;

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

CREATE VIEW service_map AS
SELECT
    parent_res."service_name"   AS "parent_service",
    child_res."service_name"   AS "child_service",
    child."kind"                                     AS "span_kind",
    COUNT(*)                                         AS "call_count"
FROM spans child
INNER JOIN spans parent
    ON child."parent_span_id" = parent."span_id"
    AND child."trace_id"     = parent."trace_id"
INNER JOIN resources parent_res ON parent."resource_id" = parent_res."id"
INNER JOIN resources child_res  ON child."resource_id"  = child_res."id"
WHERE
    parent_res."service_name" IS NOT NULL
    AND child_res."service_name" IS NOT NULL
    AND parent_res."service_name" <>
        child_res."service_name"
GROUP BY
    parent_res."service_name",
    child_res."service_name",
    child."kind";

CREATE VIEW service_map_detailed AS
SELECT
    parent_res."service_name"   AS "parent_service",
    child_res."service_name"   AS "child_service",
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
    parent_res."service_name" IS NOT NULL
    AND child_res."service_name" IS NOT NULL
    AND parent_res."service_name" <>
        child_res."service_name"
GROUP BY
    parent_res."service_name",
    child_res."service_name",
    child."kind";

CREATE VIEW log_severity_stats AS
SELECT
    "severity_text",
    "severity_number",
    COUNT(*)                                              AS "count",
    CAST(to_timestamp("time_unix_nano" / 1000000000.0) AS DATE) AS "log_date"
FROM log_records
WHERE "time_unix_nano" > 0
GROUP BY
    "severity_text",
    "severity_number",
    CAST(to_timestamp("time_unix_nano" / 1000000000.0) AS DATE);

-- =============================================================================
-- STEP 11 -- record every version this script brings a database through
--
-- Historical versions are inserted with ON CONFLICT DO NOTHING (their applied_at should reflect
-- when that version was first reached, not the last time this script happened to run). Only the
-- final, current version's applied_at is refreshed on every run, matching what
-- PostgreSQL-Schema.sql itself does for a fresh install.
-- =============================================================================

INSERT INTO schema_version ("version") VALUES
    ('2.7.0'), ('2.8.0'), ('2.9.0'), ('2.10.0'), ('2.11.0'), ('2.12.0'), ('2.13.0'), ('2.13.1'), ('2.13.2')
ON CONFLICT ("version") DO NOTHING;

INSERT INTO schema_version ("version") VALUES ('2.13.3')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
