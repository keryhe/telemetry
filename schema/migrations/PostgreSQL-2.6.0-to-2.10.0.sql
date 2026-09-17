-- Migration: schema 2.6.0 -> 2.10.0
-- Applies to BOTH plain PostgreSQL and PostgreSQL + TimescaleDB. Physical storage
-- (hypertable/compression/continuous-aggregate setup) is untouched except for the native
-- retention-policy removal in the final step, which detects TimescaleDB automatically and is a
-- no-op on plain PostgreSQL.
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f PostgreSQL-2.6.0-to-2.10.0.sql
--
-- Every step is idempotent and guarded, so this is also the correct script for a database
-- already at 2.7.0, 2.8.0, or 2.9.0 -- the steps it has already had become no-ops. Re-running it
-- on a database already at 2.10.0 does nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.10.0 or it is left exactly as it
-- was. Under TimescaleDB the UPDATEs in step 1 will transparently decompress and recompress any
-- affected compressed chunks, which is why this is worth running during a quiet window on a
-- large database.
--
-- What each version step changed, and why (see CLAUDE.md / plans/telemetry-retention.md for the
-- full rationale):
--
--   2.7.0  metrics gained uk_metric_identity UNIQUE (resource_id, name, type, scope_id), so the
--          catalog holds one row per metric identity instead of one row per OTLP export cycle.
--          Existing databases therefore carry duplicates that MUST be collapsed before the
--          constraint can be created -- step 1 below. idx_resource_name became an exact left
--          prefix of the new constraint and is dropped.
--   2.8.0  Six indexes dropped from spans/log_records as provably redundant or unused: four
--          B-tree (idx_trace_id, idx_start_time, idx_kind, idx_status) and the two GIN indexes
--          on attributes_json, which no read-path query ever used for containment.
--   2.9.0  Exemplars moved onto the data point that owns them. The single exemplar_id column
--          could hold only one exemplar where OTLP allows many, and no writer ever populated it,
--          so no data is lost by dropping it or the shared exemplars table.
--   2.10.0 Retention scheduling moved to a single application-level mechanism (Keryhe.Telemetry.
--          Api's RetentionWorker) driven by a new retention_settings table, on every provider.
--          Under TimescaleDB this makes the native add_retention_policy jobs on log_records and
--          the five metric data-point tables redundant, so this migration removes them --
--          removing the CREATE-script lines alone does NOT do this: add_retention_policy
--          registers a persistent background job independent of the script that created it
--          (timescaledb_information.jobs). Compression policies and log_severity_stats_daily's
--          own retention policy are untouched (Decision 1/2 in plans/telemetry-retention.md).
--          On plain PostgreSQL there is nothing to remove (no TimescaleDB extension, so no
--          native policies ever existed); this script detects that and skips the removal step.

BEGIN;

-- =============================================================================
-- 2.7.0 -- collapse duplicate metrics rows, then add uk_metric_identity
-- =============================================================================

-- Survivor per identity = MIN(id), i.e. the earliest row, whose created_at is the earliest this
-- metric was seen. That is exactly what created_at means from 2.7.0 on ("first seen"), so the
-- surviving row already carries the right timestamp.
CREATE TEMP TABLE metric_dedup_map ON COMMIT DROP AS
SELECT "id" AS old_id,
       min("id") OVER (PARTITION BY "resource_id", "name", "type", "scope_id") AS new_id
FROM metrics;

-- Keep only the rows that actually move; on an already-deduplicated database this empties the
-- table and every statement below becomes a no-op.
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

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uk_metric_identity' AND conrelid = 'metrics'::regclass
    ) THEN
        ALTER TABLE metrics
            ADD CONSTRAINT uk_metric_identity UNIQUE ("resource_id", "name", "type", "scope_id");
    END IF;
END $$;

-- Redundant once uk_metric_identity exists: (resource_id, name) is its exact left prefix.
DROP INDEX IF EXISTS idx_resource_name;

-- =============================================================================
-- 2.8.0 -- drop the redundant / unused spans and log_records indexes
-- =============================================================================

DROP INDEX IF EXISTS idx_trace_id;              -- left prefix of uk_trace_span (trace_id, span_id)
DROP INDEX IF EXISTS idx_start_time;            -- left prefix of idx_duration (start, end)
DROP INDEX IF EXISTS idx_kind;                  -- 6 distinct values; never chosen by the planner
DROP INDEX IF EXISTS idx_status;                -- 3 distinct values; never chosen by the planner
DROP INDEX IF EXISTS idx_spans_attributes_gin;  -- no JSONB containment query in the read path
DROP INDEX IF EXISTS idx_log_attributes_gin;    -- likewise

-- =============================================================================
-- 2.9.0 -- exemplars move onto the data point rows
-- =============================================================================

-- exemplar_id was never written by any provider, so these columns are empty everywhere and the
-- drop discards nothing. Summary data points are untouched: OTLP Summary has no exemplars.
ALTER TABLE gauge_data_points                     DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE sum_data_points                       DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE histogram_data_points                 DROP COLUMN IF EXISTS "exemplar_id";
ALTER TABLE exponential_histogram_data_points     DROP COLUMN IF EXISTS "exemplar_id";

ALTER TABLE gauge_data_points                 ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE sum_data_points                   ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE histogram_data_points             ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;
ALTER TABLE exponential_histogram_data_points ADD COLUMN IF NOT EXISTS "exemplars_json" JSONB;

-- Always empty (nothing ever inserted into it), and nothing references it: the data-point
-- exemplar_id columns carried no foreign key.
DROP TABLE IF EXISTS exemplars;

-- =============================================================================
-- 2.10.0 -- retention_settings table (single global row)
-- =============================================================================

CREATE TABLE IF NOT EXISTS retention_settings (
    "id"                    SMALLINT     PRIMARY KEY DEFAULT 1,
    "trace_retention_days"   INTEGER      NOT NULL,
    "log_retention_days"     INTEGER      NOT NULL,
    "metric_retention_days"  INTEGER      NOT NULL,
    "updated_at"             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT chk_retention_settings_singleton CHECK ("id" = 1)
);

-- Seeded with today's implicit defaults (traces 90d, logs 90d, metrics 180d). Guarded so
-- re-running this migration never fails or duplicates the row.
INSERT INTO retention_settings ("id", "trace_retention_days", "log_retention_days", "metric_retention_days")
VALUES (1, 90, 90, 180)
ON CONFLICT ("id") DO NOTHING;

-- =============================================================================
-- 2.10.0 -- remove Timescale's native retention jobs (TimescaleDB installs only)
-- =============================================================================

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

-- log_severity_stats_daily's own retention policy (pruning the continuous aggregate, not raw
-- log_records) is a different concern and is intentionally left untouched.

-- =============================================================================
-- Record the new version (matches what the full schema scripts write)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.10.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
