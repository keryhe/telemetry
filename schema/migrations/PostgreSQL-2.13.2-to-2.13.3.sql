-- Migration: schema 2.13.2 -> 2.13.3 (PostgreSQL, plain)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f PostgreSQL-2.13.2-to-2.13.3.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.3 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.13.3 or it is left exactly as it
-- was. CREATE INDEX CONCURRENTLY cannot run inside a transaction block, so the GIN/trigram
-- indexes below use plain CREATE INDEX -- on a large existing installation this holds a
-- SHARE lock on the table for the duration of the build (writes blocked, reads unaffected).
-- Consider running the CREATE INDEX statements individually with CONCURRENTLY outside this
-- transaction on a live production database instead of pasting this whole script in one go.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 7 section, decision
-- 5/6/7/8/39):
--
--   resources.service_name: a real column, backfilled below from attributes_json's
--   "service.name" key, replacing the expression index idx_resources_service_name used to sit
--   on. ResourceServiceNameExpr() is now a plain column reference everywhere. Written by the
--   bulk writer's resource upsert going forward.
--
--   pg_trgm GIN indexes on spans.name / spans.status_message / log_records.body_value back
--   FreeTextPredicate's existing ILIKE '%text%' predicate with no SQL change -- the planner
--   picks the index up automatically once it exists.
--
--   GIN jsonb_path_ops indexes on spans.attributes_json / log_records.attributes_json back
--   AttributePredicate's new typed-containment form (@>) for positive (non-negated) key:value
--   matches; negation stays on the phase 1 text-comparison form (containment can't efficiently
--   express "absent or different").
--
--   No GIN index is added on the five metric data-point tables' attributes_json -- EXPLAIN
--   against a live container showed the existing (metric_id, time_unix_nano) index scan is
--   already selective enough that a GIN index made no measurable difference to the label-filter
--   query plan, so it was left out per the plan's "add it only where it wins" instruction. See
--   plans/list-pages-server-side.md's Phase 7 section for the recorded EXPLAIN findings.

BEGIN;

CREATE EXTENSION IF NOT EXISTS pg_trgm;

ALTER TABLE resources ADD COLUMN IF NOT EXISTS "service_name" VARCHAR(255);

UPDATE resources
SET "service_name" = "attributes_json" ->> 'service.name'
WHERE "service_name" IS NULL AND "attributes_json" ? 'service.name';

DROP INDEX IF EXISTS idx_resources_service_name;
CREATE INDEX IF NOT EXISTS idx_resources_service_name ON resources ("service_name");

CREATE INDEX IF NOT EXISTS idx_spans_attributes_gin ON spans USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_spans_name_trgm ON spans USING GIN ("name" gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_spans_status_message_trgm ON spans USING GIN ("status_message" gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_log_attributes_gin ON log_records USING GIN ("attributes_json" jsonb_path_ops);
CREATE INDEX IF NOT EXISTS idx_log_body_trgm ON log_records USING GIN ("body_value" gin_trgm_ops);

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.13.3')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
