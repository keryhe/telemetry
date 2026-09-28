-- OpenTelemetry PostgreSQL Schema (plain PostgreSQL, no TimescaleDB)
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Targets a vanilla PostgreSQL instance WITHOUT the timescaledb extension.
--
-- Column names use snake_case (double-quoted) while C# models remain PascalCase.
--
-- This script produces the same logical table/column set as Timescale-Schema.sql.
-- The difference is purely physical storage: the metric data-point tables and
-- log_records are plain heap tables here (not hypertables). Time-series access is
-- served by BRIN indexes on "time_unix_nano" plus the natural btree lookup indexes
-- that hypertable partitioning would otherwise provide. The TimescaleDB continuous
-- aggregate is replaced by a plain on-demand view.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f PostgreSQL-Schema.sql
--
-- For a TimescaleDB-enabled instance (hypertables, compression, retention,
-- continuous aggregate) use Timescale-Schema.sql instead.
--
-- Optional clean reset in an existing local DB before re-running this script:
--   psql -U postgres -d telemetry -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

-- pg_trgm backs the free-text search GIN indexes below (schema 2.13.3, list-pages-server-side
-- plan Phase 7, decision 5/39): it supplies the gin_trgm_ops operator class that makes a plain
-- ILIKE '%text%' predicate index-searchable. No SQL change is needed in FreeTextPredicate to use
-- it -- the planner picks the GIN index for the existing ILIKE text automatically once it exists.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

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
    -- service_name (schema 2.13.3, list-pages-server-side plan Phase 7): a real column, written
    -- by the bulk writer's resource upsert going forward, extracted from attributes_json's
    -- "service.name" key the same way ExtractServiceName always has. Replaces the expression
    -- index below with a plain column index -- ResourceServiceNameExpr() is now just
    -- "{alias}.service_name" on every provider, not a per-provider JSON extraction expression.
    "service_name"    VARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX idx_resources_tenant_id ON resources ("tenant_id");
CREATE INDEX idx_created_at ON resources ("created_at");
CREATE INDEX idx_resources_service_name ON resources ("service_name");

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

-- Trace spans. Events and links live in the "events_json"/"links_json" columns on this
-- row (schema 2.11.0) rather than child tables -- they are always read and written as a
-- whole alongside their owning span, exactly like metric data points' "exemplars_json".
CREATE TABLE spans (
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
-- stays small and cheap to maintain despite the 2.8.0 reasoning not applying to it (schema 2.12.0).
CREATE INDEX idx_spans_error ON spans ("start_time_unix_nano" DESC) WHERE "status_code" = 'ERROR';

-- Trace page/summary anchor on roots (schema 2.13.1, list-pages-server-side plan Phase 3):
-- every root-anchored query used to read all spans through idx_duration and filter out
-- non-roots. This covers end_time_unix_nano too, so mode=slow's duration check runs inside
-- the index without fetching each row.
CREATE INDEX idx_spans_root_time ON spans ("start_time_unix_nano" DESC) INCLUDE ("end_time_unix_nano")
    WHERE "parent_span_id" IS NULL;

-- Search indexes (schema 2.13.3, list-pages-server-side plan Phase 7, analytics tier only --
-- decision 39). This SUPERSEDES the 2.8.0 idx_spans_attributes_gin removal note above: that
-- index was dropped because nothing queried it at the time, not because GIN on this column can
-- never be worthwhile -- Phase 7 adds search, which does. jsonb_path_ops (not the default
-- jsonb_ops) because every query against it is `@>` containment (AttributePredicate), never the
-- `?`/`?|`/`?&` key-existence operators jsonb_path_ops can't serve; it produces a smaller index
-- for the same containment queries. gin_trgm_ops backs FreeTextPredicate's existing ILIKE
-- '%text%' predicate on "name"/"status_message" with no SQL change (see the pg_trgm comment
-- above). On Timescale specifically, compressed chunks (>7 days old) don't use these indexes and
-- fall back to a scan -- see CLAUDE.md and Timescale-Schema.sql's own note.
CREATE INDEX idx_spans_attributes_gin ON spans USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX idx_spans_name_trgm ON spans USING GIN ("name" gin_trgm_ops);
CREATE INDEX idx_spans_status_message_trgm ON spans USING GIN ("status_message" gin_trgm_ops);

-- span_events and span_links were dropped in 2.11.0: neither was ever read or written
-- independently of its parent span, so both collapsed into spans."events_json"/"links_json".

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table (referenced by FK from data point tables)
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

-- exemplars_json (2.9.0) replaces the former single exemplar_id column and the shared
-- `exemplars` table, which no writer ever populated. OTLP declares `repeated Exemplar
-- exemplars` on every data point except Summary, so one id per row could never hold more than
-- the first. The list is stored as JSON on the data point itself: a child table would need each
-- data point's generated id, which the bulk-load path (binary COPY / bulk copy) does not hand
-- back, and JSON is already how bucket_counts, explicit_bounds and quantile_values are stored.
-- Trade-off: an exemplar's trace_id is no longer indexable. No read path queries it.
-- Gauge data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE gauge_data_points (
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
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_gauge_time_brin   ON gauge_data_points USING BRIN ("time_unix_nano");

-- Sum data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE sum_data_points (
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
CREATE INDEX idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_sum_time_brin   ON sum_data_points USING BRIN ("time_unix_nano");
CREATE INDEX idx_temporality     ON sum_data_points ("aggregation_temporality");

-- Histogram data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE histogram_data_points (
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
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_histogram_time_brin   ON histogram_data_points USING BRIN ("time_unix_nano");

-- Exponential histogram data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE exponential_histogram_data_points (
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
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_exp_histogram_time_brin   ON exponential_histogram_data_points USING BRIN ("time_unix_nano");

-- Summary data points (plain table; BRIN on time + btree natural lookup)
CREATE TABLE summary_data_points (
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
CREATE INDEX idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX idx_summary_time_brin   ON summary_data_points USING BRIN ("time_unix_nano");

-- metric_last_seen (schema 2.13.2, list-pages-server-side plan Phase 5, decision 27): the
-- metrics catalog's "has data in range" check reads this instead of scanning the five
-- data-point tables. Deliberately NOT a column on "metrics": ingestion's metrics upsert
-- (MERGE/ON CONFLICT) never touches this table, so the two writers can't deadlock, and
-- Timescale chunk creation (which locks "metrics" to attach foreign keys) never blocks it.
-- No FK to "metrics" either -- a FK would make every touch lock-check the metrics row,
-- reintroducing the exact contention this table exists to avoid. Nothing deletes metrics
-- rows (see CLAUDE.md's dedup notes), so orphans can't occur; if a delete of a metrics row
-- is ever added, it must delete the matching row here too. Written by the collector's
-- MetricTouchWorker on a periodic interval, not per flush -- see that type's doc comment.
CREATE TABLE metric_last_seen (
    "metric_id"            BIGINT NOT NULL PRIMARY KEY,
    "last_seen_unix_nano"  BIGINT NOT NULL
);
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen ("last_seen_unix_nano");

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records (plain table; BRIN on time + btree natural lookup).
-- TimeUnixNano is NOT NULL with DEFAULT 0 to handle edge-case OTLP records.
CREATE TABLE log_records (
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
-- idx_log_time dropped in 2.13.0: it is a pure left prefix of idx_log_time_id below, the new
-- keyset-paging tiebreak index (same provably-redundant reasoning as the 2.8.0 spans index
-- cleanup documented in CLAUDE.md).
CREATE INDEX idx_log_time_id       ON log_records ("time_unix_nano" DESC, "id" DESC);
CREATE INDEX idx_log_time_brin     ON log_records USING BRIN ("time_unix_nano");
CREATE INDEX idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX idx_severity          ON log_records ("severity_number");
CREATE INDEX idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);
-- Search indexes (schema 2.13.3, Phase 7, analytics tier only) -- supersedes the 2.8.0
-- idx_log_attributes_gin removal note above; see the identical spans-table comment for the
-- jsonb_path_ops/gin_trgm_ops reasoning, which applies here unchanged.
CREATE INDEX idx_log_attributes_gin ON log_records USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX idx_log_body_trgm ON log_records USING GIN ("body_value" gin_trgm_ops);

-- =============================================================================
-- ROLLUP TABLES (schema 2.13.0, list-pages-server-side plan decisions 37-38)
-- =============================================================================

-- One row per signal + granularity, claimed by RollupWorker with an atomic
-- UPDATE ... WHERE lease_expires_at < now, the same pattern as alert_rules'
-- TryClaimFireAsync. No foreign keys: the worker's writes must never lock resources or
-- anything ingestion touches.
CREATE TABLE rollup_state (
    "signal_name"                   VARCHAR(20)  NOT NULL,
    "granularity"              VARCHAR(10)  NOT NULL,
    "coverage_start_unix_nano"  BIGINT,
    "rolled_until_unix_nano"    BIGINT       NOT NULL DEFAULT 0,
    "repassed_until_unix_nano"  BIGINT       NOT NULL DEFAULT 0,
    "lease_owner"              VARCHAR(100),
    "lease_expires_at"          TIMESTAMPTZ  NOT NULL DEFAULT 'epoch',
    PRIMARY KEY ("signal_name", "granularity")
);

-- Per-minute log summary, recomputed from raw log_records by RollupWorker -- never
-- incremented during ingestion (decision 38). Severity groups match
-- LogReadRepositoryBase.GetLogHistogramAsync's six-group CASE exactly. No foreign key on
-- resource_id: the worker's writes must never lock resources.
CREATE TABLE log_rollup_minute (
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
CREATE INDEX idx_log_rollup_minute_bucket ON log_rollup_minute ("bucket_unix_nano");

-- Same shape, one row per hour.
CREATE TABLE log_rollup_hour (
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
CREATE INDEX idx_log_rollup_hour_bucket ON log_rollup_hour ("bucket_unix_nano");

-- Traces whose root span never arrived (schema 2.13.1, decision 41): one row per trace,
-- holding the anchor span (its earliest span, whose own parent does not exist anywhere) that
-- the rollup worker detected per finished minute. No foreign keys: the worker's writes must
-- never lock resources. Indexed to merge with idx_spans_root_time in the same order.
CREATE TABLE orphan_roots (
    "trace_id"             CHAR(32)     NOT NULL PRIMARY KEY,
    "span_id"              CHAR(16)     NOT NULL,
    "resource_id"          BIGINT       NOT NULL,
    "start_time_unix_nano" BIGINT       NOT NULL,
    "end_time_unix_nano"   BIGINT       NOT NULL,
    "detected_at"          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_orphan_roots_start ON orphan_roots ("start_time_unix_nano" DESC, "trace_id");

-- Per-minute trace summary (schema 2.13.1, decisions 37-38, 41): one row per minute, per
-- anchor span's resource, operation name (folded to '__other__' past the 200-distinct-name
-- cardinality guard) and inbound flag (anchor kind SERVER/CONSUMER). Counts traces whose
-- ANCHOR (null-parent root, or its orphan_roots span) starts in that minute; error flag and
-- duration are aggregated over the trace's full span set. lb_00..lb_39 are the fixed
-- latency-bucket counts (LatencyBucketSql) as plain columns so SQL can sum them across rows.
CREATE TABLE trace_rollup_minute (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0,
    "lb_01" INTEGER NOT NULL DEFAULT 0,
    "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0,
    "lb_04" INTEGER NOT NULL DEFAULT 0,
    "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0,
    "lb_07" INTEGER NOT NULL DEFAULT 0,
    "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0,
    "lb_10" INTEGER NOT NULL DEFAULT 0,
    "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0,
    "lb_13" INTEGER NOT NULL DEFAULT 0,
    "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0,
    "lb_16" INTEGER NOT NULL DEFAULT 0,
    "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0,
    "lb_19" INTEGER NOT NULL DEFAULT 0,
    "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0,
    "lb_22" INTEGER NOT NULL DEFAULT 0,
    "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0,
    "lb_25" INTEGER NOT NULL DEFAULT 0,
    "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0,
    "lb_28" INTEGER NOT NULL DEFAULT 0,
    "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0,
    "lb_31" INTEGER NOT NULL DEFAULT 0,
    "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0,
    "lb_34" INTEGER NOT NULL DEFAULT 0,
    "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0,
    "lb_37" INTEGER NOT NULL DEFAULT 0,
    "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX idx_trace_rollup_minute_bucket ON trace_rollup_minute ("bucket_unix_nano");

-- Same shape, one row per hour.
CREATE TABLE trace_rollup_hour (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0,
    "lb_01" INTEGER NOT NULL DEFAULT 0,
    "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0,
    "lb_04" INTEGER NOT NULL DEFAULT 0,
    "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0,
    "lb_07" INTEGER NOT NULL DEFAULT 0,
    "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0,
    "lb_10" INTEGER NOT NULL DEFAULT 0,
    "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0,
    "lb_13" INTEGER NOT NULL DEFAULT 0,
    "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0,
    "lb_16" INTEGER NOT NULL DEFAULT 0,
    "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0,
    "lb_19" INTEGER NOT NULL DEFAULT 0,
    "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0,
    "lb_22" INTEGER NOT NULL DEFAULT 0,
    "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0,
    "lb_25" INTEGER NOT NULL DEFAULT 0,
    "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0,
    "lb_28" INTEGER NOT NULL DEFAULT 0,
    "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0,
    "lb_31" INTEGER NOT NULL DEFAULT 0,
    "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0,
    "lb_34" INTEGER NOT NULL DEFAULT 0,
    "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0,
    "lb_37" INTEGER NOT NULL DEFAULT 0,
    "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX idx_trace_rollup_hour_bucket ON trace_rollup_hour ("bucket_unix_nano");

-- Seed the four rows this phase needs (logs/traces x minute/hour).
INSERT INTO rollup_state ("signal_name", "granularity") VALUES ('logs', 'minute'), ('logs', 'hour'), ('traces', 'minute'), ('traces', 'hour')
ON CONFLICT ("signal_name", "granularity") DO NOTHING;

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables and views), so a partial/failed apply never records a version that the
-- apply-schema.sh version gate would wrongly treat as "already applied".

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
-- is the only mutation the app issues against it after the seed row below.
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

-- Service map with performance metrics
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

-- Log severity distribution by day.
-- The TimescaleDB schema uses a continuous aggregate (log_severity_stats_daily) and
-- exposes log_severity_stats as a compatibility alias over it. Plain PostgreSQL has no
-- continuous aggregate, so log_severity_stats is computed on demand here. Same column
-- shape (severity_text, severity_number, count, log_date) so read repos don't branch.
-- The day bucket is computed by truncating nanoseconds-since-epoch to a DATE.
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
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
INSERT INTO schema_version ("version") VALUES ('2.13.3')
ON CONFLICT ("version") DO UPDATE
SET "applied_at" = NOW();

-- =============================================================================
-- NOTES
-- =============================================================================
--
-- Differences from Timescale-Schema.sql (same logical table/column set):
--
-- 1.  No CREATE EXTENSION timescaledb.
-- 2.  Metric data-point tables and log_records are plain heap tables with a
--     PRIMARY KEY on "id" (no create_hypertable, no partition column constraint).
-- 3.  No set_integer_now_func / telemetry_now_ns: lifecycle is not policy-driven.
-- 4.  No compression or retention policies. Manage data lifecycle externally
--     (e.g. scheduled DELETE jobs) if needed.
-- 5.  log_severity_stats is a plain on-demand view rather than a compatibility
--     alias over a continuous aggregate. Column shape is identical.
-- 6.  Time-series access uses a BRIN index on "time_unix_nano" (cheap, append-
--     friendly, what hypertable chunk exclusion approximated) plus the existing
--     (metric_id|resource_id|severity, time) btree indexes for point lookups.
--
-- jsonb columns, ON CONFLICT upserts, and all UNIQUE constraints (resource_hash,
-- scope_hash, api_keys.key_hash, uk_trace_span, uk_metric_identity) are preserved exactly.
--
-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all user tables created
--    SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;
--
-- 2) Confirm no hypertables exist (timescaledb not required/installed)
--    -- This schema intentionally creates plain tables only.
--
-- 3) Schema version
--    SELECT * FROM schema_version;
