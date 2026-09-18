-- Migration: schema 2.10.0 -> 2.11.0 (TimescaleDB)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f Timescale-2.10.0-to-2.11.0.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.11.0 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.11.0 or it is left exactly as it
-- was. Run this during a quiet window on a large database -- converting spans into a hypertable
-- with migrate_data => TRUE rewrites every existing row into chunks, and enabling compression
-- afterwards is a separate, unavoidably slow step on a table this size.
--
-- What changed, and why (see plans/span-events-links-json-hypertable.md for the full rationale):
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

BEGIN;

-- =============================================================================
-- 2.11.0 -- add events_json/links_json to spans
-- =============================================================================

ALTER TABLE spans ADD COLUMN IF NOT EXISTS "events_json" JSONB;
ALTER TABLE spans ADD COLUMN IF NOT EXISTS "links_json"  JSONB;

-- =============================================================================
-- 2.11.0 -- fold existing span_events/span_links rows into spans.events_json/links_json
-- =============================================================================

-- Guarded on to_regclass so this whole step is a no-op on a database already migrated (or
-- created fresh at 2.11.0+, where span_events/span_links never existed).
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

-- =============================================================================
-- 2.11.0 -- drop span_events/span_links now that their data lives on
-- spans.events_json/links_json (this also drops fk_span_events_spans/fk_span_links_spans and
-- idx_span_time/idx_span_link, since they belong to the dropped tables)
-- =============================================================================

DROP TABLE IF EXISTS span_events;
DROP TABLE IF EXISTS span_links;

-- =============================================================================
-- 2.11.0 -- widen spans' primary key and uk_trace_span to include the partition column
-- =============================================================================

-- The pre-2.11.0 primary key on spans("id") alone has whatever name PostgreSQL assigned it
-- (typically spans_pkey), not a name this script can assume -- look it up rather than hardcoding
-- it, and skip entirely if pk_spans already exists (idempotent re-run).
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
    IF EXISTS (
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

-- =============================================================================
-- 2.11.0 -- convert spans into a hypertable, partitioned on start_time_unix_nano
-- =============================================================================

-- migrate_data => TRUE rewrites every existing spans row into chunks; if_not_exists => TRUE
-- makes this safe to re-run once spans is already a hypertable. Same 6-hour chunk interval as
-- log_records: spans is the other candidate for "highest-volume table in the system".
SELECT create_hypertable(
    'spans', 'start_time_unix_nano',
    chunk_time_interval => 21600000000000,
    migrate_data => TRUE,
    if_not_exists => TRUE
);

-- telemetry_now_ns() was already created for the other six hypertables before 2.10.0; reused
-- here rather than redefined. Wrapped to tolerate "already set" on a re-run.
DO $$
BEGIN
    PERFORM set_integer_now_func('spans', 'telemetry_now_ns');
EXCEPTION WHEN OTHERS THEN
    NULL; -- already registered (idempotent re-run)
END $$;

-- =============================================================================
-- 2.11.0 -- enable compression on spans, matching the other six hypertables' 7-day policy
-- =============================================================================

-- resource_id is the column idx_spans_resource_time already pairs with time, and what every
-- tenant-scoped read funnels through -- the same segmentby reasoning as the metric data-point
-- tables' metric_id.
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

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.11.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
