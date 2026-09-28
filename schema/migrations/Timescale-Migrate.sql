-- Consolidated migration: brings a TimescaleDB telemetry database from ANY prior schema version
-- (2.10.0, 2.11.0, 2.12.0, 2.13.0, 2.13.1, 2.13.2, or 2.13.3) -- or a completely empty database
-- with no schema_version table at all -- up to schema 2.13.3.
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f Timescale-Migrate.sql
--
-- Replaces the former chain of six hand-run files (Timescale-2.10.0-to-2.11.0.sql,
-- -2.11.0-to-2.12.0.sql, -2.12.0-to-2.13.0.sql, -2.13.0-to-2.13.1.sql, -2.13.1-to-2.13.2.sql,
-- -2.13.2-to-2.13.3.sql). This single script is a superset of all six: every statement is
-- guarded so it is safe to run against a database at ANY of those versions, and safe to run
-- again afterwards (including against a database that was freshly installed straight from
-- Timescale-Schema.sql at 2.13.3). It never needs to be told which version a database starts
-- at -- every step figures out for itself whether it has already been applied.
--
-- Ground truth for the end state is Timescale-Schema.sql: after this script, a database looks
-- structurally identical to a fresh install of that file, whatever version it started from.
--
-- DEVIATION FROM THE SIX ORIGINAL FILES -- transaction scope. Each original file wrapped its
-- entire body in one BEGIN/COMMIT. This script deliberately does NOT wrap the whole thing in a
-- single explicit transaction. Two reasons:
--   1. The "views" section below (re)creates a continuous aggregate (log_severity_stats_daily)
--      the first time it runs against a truly empty database. TimescaleDB's continuous
--      aggregate creation is finicky about transactional context in ways that CREATE TABLE /
--      ALTER TABLE / CREATE INDEX are not, and Timescale-Schema.sql itself -- the ground truth
--      for a fresh install -- never wraps that section in an explicit transaction either.
--   2. Because every individual statement here is already independently idempotent (native
--      IF NOT EXISTS, ON CONFLICT, or a DO block that checks catalog state first), atomicity
--      across the whole script buys little: a run that fails partway through can simply be
--      re-run, and it will pick up exactly where it left off (this is the same "safe to re-run"
--      property the task asks for, just achieved per-statement instead of via one big
--      transaction). Where the original per-version files' own atomicity mattered (e.g. the
--      spans hypertable conversion + PK/UK widen must not be left half-applied), the individual
--      steps are still ordered so that a half-applied state is self-describing and resumable:
--      e.g. the PK/UK widen DO blocks below detect the pre-migration shape directly rather than
--      relying on a schema_version row.
--
-- DEVIATION -- views. Timescale-Schema.sql unconditionally DROPs and recreates
-- trace_summary/service_map/service_map_detailed/log_severity_stats/log_severity_stats_daily on
-- every run, because a fresh install has no data to lose. Doing the same here for
-- log_severity_stats_daily would DROP the continuous aggregate on every re-run of this migration
-- against a live database, discarding its materialized history and forcing a full re-refresh.
-- Instead:
--   - the three plain views (trace_summary, service_map, service_map_detailed) and the
--     log_severity_stats compatibility view are DROP IF EXISTS + CREATE, same as
--     Timescale-Schema.sql -- NOT CREATE OR REPLACE VIEW, because a database that only ever ran
--     the six original per-version migration files still has service_map/service_map_detailed
--     defined against attributes_json ->> 'service.name' (TEXT): those migrations added the
--     resources.service_name column (VARCHAR(255)) but never touched the views, and PostgreSQL's
--     CREATE OR REPLACE VIEW rejects changing an existing output column's type. Dropping first
--     sidesteps that; it is still idempotent and loses nothing since plain views hold no data of
--     their own.
--   - the continuous aggregate log_severity_stats_daily is created only if it does not already
--     exist (CREATE MATERIALIZED VIEW IF NOT EXISTS ... WITH (timescaledb.continuous)), and is
--     never dropped by this script.
--
-- DATA BACKFILLS. Two backfills appear below (resources.service_name, metric_last_seen). Both
-- are naturally idempotent (they only touch rows that still need it), but metric_last_seen's
-- backfill scans all five metric data-point tables and can take minutes to hours on a large
-- installation (see its own comment below) -- re-running that full scan on every invocation of
-- this script against an already-migrated database would be wasteful. Both backfills are
-- therefore additionally gated behind a check of whether their originating version's row is
-- already present in schema_version, and only run (and only then record that row) if not --
-- exactly the "version-stamped steps" shape the task calls for.
--
-- TIMESCALE-SPECIFIC IDEMPOTENCY NOTES:
--   - create_hypertable(..., if_not_exists => TRUE) is a no-op if the target is already a
--     hypertable (and, for spans specifically, if_not_exists also protects the migrate_data
--     rewrite from re-running against data already in chunks).
--   - add_compression_policy / add_continuous_aggregate_policy / add_retention_policy all take
--     if_not_exists => TRUE natively (used throughout, exactly as Timescale-Schema.sql does) --
--     no extra guard needed for those three calls.
--   - ALTER TABLE ... SET (timescaledb.compress, ...) has NO if_not_exists form and errors if
--     compression is already configured on that hypertable, so every such call below is guarded
--     by checking timescaledb_information.hypertables.compression_enabled first.
--   - set_integer_now_func can be re-registered safely on recent TimescaleDB, but is wrapped in
--     an exception-swallowing DO block anyway (matching the original 2.10.0-to-2.11.0 file's own
--     defensive pattern for spans) since re-registering an already-set integer-now function is
--     not something this script needs to rely on being a silent no-op across every Timescale
--     version it might run against.
--
-- Run during a quiet window on a large database: the spans hypertable conversion (only actually
-- executes when starting from 2.10.0) uses migrate_data => TRUE, which rewrites every existing
-- spans row into chunks, and enabling compression afterwards is a separate, unavoidably slow
-- step on a table that size.

-- =============================================================================
-- SCHEMA_VERSION -- created first (if missing) so every later step can query it for gating.
-- =============================================================================

CREATE TABLE IF NOT EXISTS schema_version (
    "version"   VARCHAR(20) PRIMARY KEY,
    "applied_at" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- =============================================================================
-- EXTENSIONS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;
-- pg_trgm backs the free-text search GIN indexes added in 2.13.3 (see that section below).
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- =============================================================================
-- COMMON TABLES (shared across signals) -- unchanged by any of the six migrations except
-- resources gaining "service_name" in 2.13.3, handled below.
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

-- Resource represents the entity producing telemetry. Created here WITHOUT service_name so a
-- database starting from 2.10.0 (where resources already exists, without that column) and a
-- completely empty database both converge the same way: through the ADD COLUMN IF NOT EXISTS
-- step immediately below, which is the only path that can run against a pre-existing table.
CREATE TABLE IF NOT EXISTS resources (
    "id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "tenant_id"       BIGINT       NOT NULL DEFAULT 1 REFERENCES tenants("id"),
    "resource_hash"   CHAR(64)     NOT NULL,
    "schema_url"      VARCHAR(2048),
    "created_at"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "attributes_json" JSONB,
    CONSTRAINT uk_resource_tenant_hash UNIQUE ("tenant_id", "resource_hash")
);
CREATE INDEX IF NOT EXISTS idx_resources_tenant_id ON resources ("tenant_id");
CREATE INDEX IF NOT EXISTS idx_created_at ON resources ("created_at");

-- Instrumentation scope (library)
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

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- spans is created here in its pre-2.11.0 shape deliberately (2-column PK, no
-- events_json/links_json) -- NOT the final 2.13.3 shape -- so that CREATE TABLE IF NOT EXISTS is
-- a genuine no-op against a 2.10.0 database (which already has exactly this shape), and the
-- 2.11.0 migration block immediately below is what carries a freshly-created table (and a
-- 2.10.0-shaped existing one) forward to the final shape identically. Splitting it this way
-- means there is exactly one code path -- not two -- that produces the final spans shape.
CREATE TABLE IF NOT EXISTS spans (
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
    CONSTRAINT fk_spans_resources FOREIGN KEY ("resource_id") REFERENCES resources ("id"),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY ("scope_id")    REFERENCES instrumentation_scopes ("id")
);
CREATE INDEX IF NOT EXISTS idx_span_id            ON spans ("span_id");
CREATE INDEX IF NOT EXISTS idx_parent_span        ON spans ("parent_span_id");
CREATE INDEX IF NOT EXISTS idx_spans_trace_parent ON spans ("trace_id", "parent_span_id");
CREATE INDEX IF NOT EXISTS idx_end_time           ON spans ("end_time_unix_nano"   DESC);
CREATE INDEX IF NOT EXISTS idx_duration           ON spans ("start_time_unix_nano", "end_time_unix_nano");
CREATE INDEX IF NOT EXISTS idx_spans_name         ON spans ("name");
CREATE INDEX IF NOT EXISTS idx_spans_resource_time ON spans ("resource_id", "start_time_unix_nano" DESC);

-- =============================================================================
-- 2.11.0 -- events_json/links_json, fold span_events/span_links, widen PK/UK, hypertable,
-- compression (see plans/span-events-links-json-hypertable.md for the full rationale, preserved
-- from Timescale-2.10.0-to-2.11.0.sql)
-- =============================================================================
--
--   1. span_events and span_links were never read or written independently of their parent span
--      -- every read loads them by span_id and immediately re-attaches them in memory (the same
--      access pattern exemplars_json was introduced for in 2.9.0) -- so both collapse into two
--      new nullable columns on spans: events_json/links_json. NULL means "no events"/"no links".
--      The JSON shape matches exactly what System.Text.Json produces for
--      List<SpanEventModel>/List<SpanLinkModel> with no naming policy configured, i.e. the C#
--      property names verbatim (PascalCase) -- that is what the read path
--      (Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs) deserializes on the other end.
--   2. Removing span_events/span_links also removes the FK reference to spans("id") that
--      previously forced spans to keep a simple (non-composite) primary key -- TimescaleDB
--      requires every unique/PK constraint on a hypertable to include the partition column. With
--      that FK gone, spans' primary key widens to (id, start_time_unix_nano) and uk_trace_span to
--      (trace_id, span_id, start_time_unix_nano), and spans becomes a hypertable partitioned on
--      start_time_unix_nano, matching the other six hypertables in this schema -- spans is very
--      likely the largest table in the system, so it is also the one where compression has the
--      most storage impact.

ALTER TABLE spans ADD COLUMN IF NOT EXISTS "events_json" JSONB;
ALTER TABLE spans ADD COLUMN IF NOT EXISTS "links_json"  JSONB;

-- Guarded on to_regclass so this whole step is a no-op on a database already migrated (or
-- created fresh, where span_events/span_links never existed).
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
        WHERE s."id" = e."span_id";
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
        WHERE s."id" = l."span_id";
    END IF;
END $$;

-- Drop span_events/span_links now that their data lives on spans.events_json/links_json (this
-- also drops fk_span_events_spans/fk_span_links_spans and idx_span_time/idx_span_link, since
-- they belong to the dropped tables).
DROP TABLE IF EXISTS span_events;
DROP TABLE IF EXISTS span_links;

-- The pre-2.11.0 primary key on spans("id") alone has whatever name PostgreSQL assigned it
-- (typically spans_pkey), not a name this script can assume -- look it up rather than hardcoding
-- it, and skip entirely if pk_spans already exists (idempotent re-run, or a table just created
-- fresh below would already have it -- but see note above: this table is created in its
-- pre-2.11.0 shape, so a fresh install DOES pass through this block once).
DO $$
DECLARE
    old_pk TEXT;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'pk_spans' AND conrelid = 'spans'::regclass
    ) THEN
        SELECT conname INTO old_pk
        FROM pg_constraint
        WHERE conrelid = 'spans'::regclass AND contype = 'p';

        IF old_pk IS NOT NULL THEN
            EXECUTE format('ALTER TABLE spans DROP CONSTRAINT %I', old_pk);
        END IF;

        ALTER TABLE spans ADD CONSTRAINT pk_spans PRIMARY KEY ("id", "start_time_unix_nano");
    END IF;
END $$;

-- uk_trace_span keeps its name across this migration; only its column list changes, so detect
-- the pre-2.11.0 (2-column) shape specifically rather than assuming it needs replacing.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        WHERE c.conname = 'uk_trace_span' AND c.conrelid = 'spans'::regclass
    ) THEN
        ALTER TABLE spans ADD CONSTRAINT uk_trace_span
            UNIQUE ("trace_id", "span_id", "start_time_unix_nano");
    ELSIF EXISTS (
        SELECT 1 FROM pg_constraint c
        WHERE c.conname = 'uk_trace_span'
          AND c.conrelid = 'spans'::regclass
          AND array_length(c.conkey, 1) = 2
    ) THEN
        ALTER TABLE spans DROP CONSTRAINT uk_trace_span;
        ALTER TABLE spans ADD CONSTRAINT uk_trace_span
            UNIQUE ("trace_id", "span_id", "start_time_unix_nano");
    END IF;
END $$;

-- migrate_data => TRUE rewrites every existing spans row into chunks; if_not_exists => TRUE
-- makes this safe to re-run once spans is already a hypertable. Same 6-hour chunk interval as
-- log_records: spans is the other candidate for "highest-volume table in the system".
SELECT create_hypertable(
    'spans', 'start_time_unix_nano',
    chunk_time_interval => 21600000000000,
    migrate_data => TRUE,
    if_not_exists => TRUE
);

-- telemetry_now_ns() is (re)created below in the TIMESCALEDB LIFECYCLE POLICIES section, but
-- spans needs its integer-now function registered right after becoming a hypertable, before any
-- compression policy is added against it -- so it is created here too (CREATE OR REPLACE is
-- idempotent) rather than deferring to that later section.
CREATE OR REPLACE FUNCTION telemetry_now_ns()
RETURNS BIGINT
LANGUAGE SQL
STABLE
AS $$
    SELECT (EXTRACT(EPOCH FROM NOW()) * 1000000000)::BIGINT;
$$;

DO $$
BEGIN
    PERFORM set_integer_now_func('spans', 'telemetry_now_ns');
EXCEPTION WHEN OTHERS THEN
    NULL; -- already registered (idempotent re-run)
END $$;

-- resource_id is the column idx_spans_resource_time already pairs with time, and what every
-- tenant-scoped read funnels through -- the same segmentby reasoning as the metric data-point
-- tables' metric_id. ALTER TABLE ... SET (timescaledb.compress) has no IF NOT EXISTS form and
-- errors if compression is already configured, hence the guard.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM timescaledb_information.hypertables
        WHERE hypertable_name = 'spans' AND compression_enabled
    ) THEN
        ALTER TABLE spans SET (
            timescaledb.compress,
            timescaledb.compress_segmentby = '"resource_id"',
            timescaledb.compress_orderby = '"start_time_unix_nano" DESC'
        );
    END IF;
END $$;

SELECT add_compression_policy('spans', BIGINT '604800000000000', if_not_exists => TRUE);

INSERT INTO schema_version ("version") VALUES ('2.11.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

-- =============================================================================
-- 2.12.0 -- idx_spans_error for mode=errors trace queries (see CLAUDE.md's "spans index set"
-- notes). Not a reversal of idx_status's 2.8.0 removal -- that was a plain B-tree over all three
-- status values, too low-cardinality for the planner to ever choose; this covers only the rare
-- error rows. Created after spans became a hypertable, so TimescaleDB propagates it to every
-- chunk.
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_spans_error ON spans ("start_time_unix_nano" DESC) WHERE "status_code" = 'ERROR';

INSERT INTO schema_version ("version") VALUES ('2.12.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

-- =============================================================================
-- 2.13.1 -- root-span index (see plans/list-pages-server-side.md's Phase 3 section). Trace
-- page/summary anchor on roots: this covers end_time_unix_nano too, so mode=slow's duration
-- check runs inside the index without fetching each row.
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_spans_root_time ON spans ("start_time_unix_nano" DESC) INCLUDE ("end_time_unix_nano")
    WHERE "parent_span_id" IS NULL;

-- =============================================================================
-- 2.13.3 -- search indexes (analytics tier only). jsonb_path_ops/gin_trgm_ops reasoning: see
-- PostgreSQL-Schema.sql's identical comment. Compressed chunks (>7 days) don't use these and
-- fall back to a scan -- see CLAUDE.md.
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_spans_attributes_gin ON spans USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_spans_name_trgm ON spans USING GIN ("name" gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_spans_status_message_trgm ON spans USING GIN ("status_message" gin_trgm_ops);

-- span_events and span_links were dropped in 2.11.0 (above): neither was ever read or written
-- independently of its parent span, so both collapsed into spans."events_json"/"links_json",
-- which in turn removed the FK that kept spans from being a hypertable.

-- =============================================================================
-- METRICS TABLES -- unchanged in shape by any of the six migrations; only metric_last_seen
-- (2.13.2, below) is new.
-- =============================================================================

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
CREATE INDEX IF NOT EXISTS idx_gauge_metric_time ON gauge_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_gauge_time        ON gauge_data_points ("time_unix_nano" DESC);

CREATE TABLE IF NOT EXISTS sum_data_points (
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
CREATE INDEX IF NOT EXISTS idx_sum_metric_time ON sum_data_points ("metric_id", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_temporality     ON sum_data_points ("aggregation_temporality");

CREATE TABLE IF NOT EXISTS histogram_data_points (
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
CREATE INDEX IF NOT EXISTS idx_histogram_metric_time ON histogram_data_points ("metric_id", "time_unix_nano" DESC);

CREATE TABLE IF NOT EXISTS exponential_histogram_data_points (
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
CREATE INDEX IF NOT EXISTS idx_exp_histogram_metric_time ON exponential_histogram_data_points ("metric_id", "time_unix_nano" DESC);

CREATE TABLE IF NOT EXISTS summary_data_points (
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
CREATE INDEX IF NOT EXISTS idx_summary_metric_time ON summary_data_points ("metric_id", "time_unix_nano" DESC);

-- =============================================================================
-- 2.13.2 -- metric_last_seen (see plans/list-pages-server-side.md's Phase 5 section, decision
-- 27): a new table (NOT a column on metrics) that the metrics catalog reads for its "has data in
-- range" check instead of scanning the five data-point tables (three of which are hypertables on
-- this provider). No FK to metrics -- see the table's own comment in Timescale-Schema.sql.
-- Deliberately a plain table, not a hypertable: it is keyed/updated by metric_id, not appended by
-- time.
-- =============================================================================

CREATE TABLE IF NOT EXISTS metric_last_seen (
    "metric_id"            BIGINT NOT NULL PRIMARY KEY,
    "last_seen_unix_nano"  BIGINT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_metric_last_seen_last_seen ON metric_last_seen ("last_seen_unix_nano");

-- Backfilled from MAX(time_unix_nano) per metric_id across the five data-point tables, in
-- batches of 5,000 metric ids at a time so the migration does not hold one enormous scan/lock
-- per table. On a large installation (millions of metric catalog rows, each with a deep history
-- in one or more data-point tables) this backfill is the slow part of the upgrade -- expect it
-- to run for minutes to hours depending on data-point table size and available index cache; each
-- table is scanned once per metric-id batch via the existing (metric_id, time_unix_nano DESC)
-- index, so cost scales with total row count, not with how long ago the migration is run. The
-- batch loop is safe to interrupt and re-run: every INSERT is an idempotent upsert keeping the
-- greater value, matching MetricTouchWorker's own conflict resolution.
--
-- Gated on schema_version so a database already at 2.13.2+ never repeats this scan on a later
-- re-run of this consolidated script.
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

INSERT INTO schema_version ("version") VALUES ('2.13.2')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS log_records (
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
CREATE INDEX IF NOT EXISTS idx_observed_time     ON log_records ("observed_time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_severity          ON log_records ("severity_number");
CREATE INDEX IF NOT EXISTS idx_log_severity_time ON log_records ("severity_number", "time_unix_nano" DESC);
CREATE INDEX IF NOT EXISTS idx_log_trace_span    ON log_records ("trace_id", "span_id");
CREATE INDEX IF NOT EXISTS idx_log_resource_time ON log_records ("resource_id", "time_unix_nano" DESC);

-- =============================================================================
-- 2.13.0 -- keyset tiebreak index for log_records (see plans/list-pages-server-side.md's Phase 2
-- section). idx_log_time (time_unix_nano DESC) is a pure left prefix of idx_log_time_id below, so
-- it is dropped (same reasoning as the 2.8.0 spans index cleanup documented in CLAUDE.md).
-- =============================================================================

DROP INDEX IF EXISTS idx_log_time;
CREATE INDEX IF NOT EXISTS idx_log_time_id ON log_records ("time_unix_nano" DESC, "id" DESC);

-- =============================================================================
-- 2.13.3 -- search indexes on log_records (analytics tier only) -- see PostgreSQL-Schema.sql's
-- identical comment; same compressed-chunk caveat as the spans indexes above.
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_log_attributes_gin ON log_records USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_log_body_trgm ON log_records USING GIN ("body_value" gin_trgm_ops);

-- =============================================================================
-- 2.13.0 / 2.13.1 -- ROLLUP TABLES (see plans/list-pages-server-side.md decisions 37-38, 41).
-- Plain (non-hypertable) tables: the summary rows they hold are small and rewritten in place by
-- RollupWorker, not appended at ingestion volume, so they get none of the benefit hypertables
-- give log_records/spans/the data-point tables.
-- =============================================================================

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

INSERT INTO schema_version ("version") VALUES ('2.13.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

-- Traces whose root span never arrived (decision 41): one row per trace, holding the anchor span
-- (its earliest span, whose own parent does not exist anywhere) that the rollup worker detected
-- per finished minute. No foreign keys: the worker's writes must never lock resources. Indexed to
-- merge with idx_spans_root_time in the same order.
CREATE TABLE IF NOT EXISTS orphan_roots (
    "trace_id"             CHAR(32)     NOT NULL PRIMARY KEY,
    "span_id"              CHAR(16)     NOT NULL,
    "resource_id"          BIGINT       NOT NULL,
    "start_time_unix_nano" BIGINT       NOT NULL,
    "end_time_unix_nano"   BIGINT       NOT NULL,
    "detected_at"          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_orphan_roots_start ON orphan_roots ("start_time_unix_nano" DESC, "trace_id");

-- Per-minute trace summary (decisions 37-38, 41): one row per minute, per anchor span's resource,
-- operation name (folded to '__other__' past the 200-distinct-name cardinality guard) and inbound
-- flag (anchor kind SERVER/CONSUMER). Counts traces whose ANCHOR (null-parent root, or its
-- orphan_roots span) starts in that minute; error flag and duration are aggregated over the
-- trace's full span set. lb_00..lb_39 are the fixed latency-bucket counts (LatencyBucketSql) as
-- plain columns so SQL can sum them across rows.
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

-- Same shape, one row per hour.
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

-- Seed all four rows this and the prior phase need (logs/traces x minute/hour) in one INSERT --
-- the original 2.13.0/2.13.1 files seeded them in two separate steps (logs, then traces); since
-- ON CONFLICT DO NOTHING makes this idempotent either way, seeding all four together here is
-- simpler and produces the exact same end state.
INSERT INTO rollup_state ("signal_name", "granularity")
VALUES ('logs', 'minute'), ('logs', 'hour'), ('traces', 'minute'), ('traces', 'hour')
ON CONFLICT ("signal_name", "granularity") DO NOTHING;

INSERT INTO schema_version ("version") VALUES ('2.13.1')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

-- =============================================================================
-- TIMESCALEDB LIFECYCLE POLICIES -- unchanged by any of the six migrations (present since before
-- 2.10.0 for every hypertable except spans, whose own compression/integer-now-func steps are
-- handled above, in the 2.11.0 section, since they had to happen immediately after spans became
-- a hypertable there).
-- =============================================================================

CREATE OR REPLACE FUNCTION telemetry_now_ns()
RETURNS BIGINT
LANGUAGE SQL
STABLE
AS $$
    SELECT (EXTRACT(EPOCH FROM NOW()) * 1000000000)::BIGINT;
$$;

DO $$ BEGIN PERFORM set_integer_now_func('gauge_data_points', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;
DO $$ BEGIN PERFORM set_integer_now_func('sum_data_points', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;
DO $$ BEGIN PERFORM set_integer_now_func('histogram_data_points', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;
DO $$ BEGIN PERFORM set_integer_now_func('exponential_histogram_data_points', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;
DO $$ BEGIN PERFORM set_integer_now_func('summary_data_points', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;
DO $$ BEGIN PERFORM set_integer_now_func('log_records', 'telemetry_now_ns'); EXCEPTION WHEN OTHERS THEN NULL; END $$;

-- Enable compression with segment/order strategy tuned for common query paths. Guarded because
-- ALTER TABLE ... SET (timescaledb.compress) has no IF NOT EXISTS form and errors if compression
-- is already configured -- true for every one of these six on any database that ever ran
-- Timescale-Schema.sql, since none of the six incremental migrations touch them.
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'gauge_data_points' AND compression_enabled) THEN
        ALTER TABLE gauge_data_points SET (timescaledb.compress, timescaledb.compress_segmentby = '"metric_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'sum_data_points' AND compression_enabled) THEN
        ALTER TABLE sum_data_points SET (timescaledb.compress, timescaledb.compress_segmentby = '"metric_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'histogram_data_points' AND compression_enabled) THEN
        ALTER TABLE histogram_data_points SET (timescaledb.compress, timescaledb.compress_segmentby = '"metric_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'exponential_histogram_data_points' AND compression_enabled) THEN
        ALTER TABLE exponential_histogram_data_points SET (timescaledb.compress, timescaledb.compress_segmentby = '"metric_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'summary_data_points' AND compression_enabled) THEN
        ALTER TABLE summary_data_points SET (timescaledb.compress, timescaledb.compress_segmentby = '"metric_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = 'log_records' AND compression_enabled) THEN
        ALTER TABLE log_records SET (timescaledb.compress, timescaledb.compress_segmentby = '"resource_id", "scope_id"', timescaledb.compress_orderby = '"time_unix_nano" DESC');
    END IF;
END $$;

-- Compression policies (cold data). Native if_not_exists => TRUE covers idempotency directly.
SELECT add_compression_policy('gauge_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('sum_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('exponential_histogram_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('summary_data_points', BIGINT '604800000000000', if_not_exists => TRUE);
SELECT add_compression_policy('log_records', BIGINT '604800000000000', if_not_exists => TRUE);

-- Retention (drop old data) is no longer a native TimescaleDB policy as of schema 2.10.0: the
-- application-level RetentionWorker (Keryhe.Telemetry.Api) is the one mechanism for retention on
-- every provider, including Timescale, reading its windows from retention_settings below instead
-- of a job registered here. See CLAUDE.md's telemetry retention notes. This consolidated script's
-- floor starting version is already 2.10.0, so -- unlike Timescale-Schema.sql's own comment,
-- which calls out an UPGRADE FROM a pre-2.10.0 install needing an explicit
-- SELECT remove_retention_policy(...) -- there is nothing to remove here: no schema version this
-- script supports starting from ever had a native retention policy registered.

-- =============================================================================
-- UTILITY, ALERTING AND RETENTION TABLES -- unchanged by any of the six migrations.
-- =============================================================================

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
INSERT INTO retention_settings ("id", "trace_retention_days", "log_retention_days", "metric_retention_days")
VALUES (1, 90, 90, 180)
ON CONFLICT ("id") DO NOTHING;

-- =============================================================================
-- 2.13.3 -- resources.service_name (see plans/list-pages-server-side.md's Phase 7 section,
-- decision 5/6/7/8/39). Backfilled from attributes_json ->> 'service.name' for existing rows.
-- Gated on schema_version so a database already at 2.13.3 never rescans resources on a later
-- re-run.
-- =============================================================================

ALTER TABLE resources ADD COLUMN IF NOT EXISTS "service_name" VARCHAR(255);

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM schema_version WHERE "version" = '2.13.3') THEN
        UPDATE resources
        SET "service_name" = "attributes_json" ->> 'service.name'
        WHERE "service_name" IS NULL AND "attributes_json" ? 'service.name';
    END IF;
END $$;

-- idx_resources_service_name pre-2.13.3 is an EXPRESSION index on
-- (attributes_json ->> 'service.name'), predating the service_name column -- CREATE INDEX IF NOT
-- EXISTS below would silently keep that stale expression index forever, since the name already
-- exists (verified live: re-running this migration from a pre-2.13.3 database left the old
-- expression index in place instead of indexing the new column, matching the original
-- Timescale-2.13.2-to-2.13.3.sql migration's own explicit DROP INDEX before its CREATE INDEX).
-- Detect the old shape by its definition text rather than assuming every pre-2.13.3 database still
-- has it (idempotent re-run against an already-correct index is a no-op).
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'idx_resources_service_name'
          AND indexdef LIKE '%attributes_json%'
    ) THEN
        DROP INDEX idx_resources_service_name;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_resources_service_name ON resources ("service_name");

-- =============================================================================
-- VIEWS
-- =============================================================================
-- DEVIATION from Timescale-Schema.sql: that fresh-install script unconditionally DROPs and
-- recreates every view here, including the continuous aggregate, since a fresh install has
-- nothing to lose. This migration script must not do that against a live database: dropping
-- log_severity_stats_daily would discard its materialized history and force a full re-refresh.
--
-- The three plain views and the compatibility view are DROP IF EXISTS + CREATE (NOT
-- CREATE OR REPLACE VIEW): a database that only ever ran the six original per-version migration
-- files still has service_map/service_map_detailed defined against
-- attributes_json ->> 'service.name' (TEXT) -- those two migrations never touched the views, only
-- added the resources.service_name column (VARCHAR(255)) alongside the old view definitions --
-- and PostgreSQL's CREATE OR REPLACE VIEW rejects changing an existing output column's type
-- (verified live: "cannot change data type of view column ... from text to character varying").
-- Dropping first sidesteps that; it is still safe because plain views hold no data of their own.
-- The continuous aggregate is created only if it does not already exist, and is never dropped by
-- this script.

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

-- Log severity distribution by day (continuous aggregate on log_records hypertable). Created
-- only if missing -- see the DEVIATION note above for why this script never drops it.
CREATE MATERIALIZED VIEW IF NOT EXISTS log_severity_stats_daily
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

CREATE INDEX IF NOT EXISTS idx_log_severity_stats_daily_bucket
    ON log_severity_stats_daily ("bucket_day" DESC, "severity_number");

SELECT add_continuous_aggregate_policy(
    'log_severity_stats_daily',
    start_offset => 3024000000000000::BIGINT,
    end_offset => 300000000000::BIGINT,
    schedule_interval => INTERVAL '5 minutes',
    if_not_exists => TRUE
);

-- ALTER MATERIALIZED VIEW ... SET (timescaledb.compress) has no IF NOT EXISTS form either;
-- swallow "already configured" the same way the hypertable set_integer_now_func calls do, rather
-- than trying to probe continuous-aggregate compression state through catalog views whose shape
-- is less stable across TimescaleDB versions than timescaledb_information.hypertables is.
DO $$ BEGIN
    ALTER MATERIALIZED VIEW log_severity_stats_daily SET (
        timescaledb.compress,
        timescaledb.compress_segmentby = '"severity_number", "severity_text"',
        timescaledb.compress_orderby = '"bucket_day" DESC'
    );
EXCEPTION WHEN OTHERS THEN
    NULL; -- already configured (idempotent re-run)
END $$;

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
-- SCHEMA VERSION (recorded LAST) -- only reached when every statement above succeeded.
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.13.3')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();
