-- Migration: schema 2.13.2 -> 2.13.3 (Timescale)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f Timescale-2.13.2-to-2.13.3.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.3 does
-- nothing but refresh the schema_version timestamp. Runs in ONE transaction, same caveat as the
-- PostgreSQL migration about CREATE INDEX CONCURRENTLY not being usable inside a transaction
-- block on a large live installation.
--
-- What changed, and why: identical to PostgreSQL-2.13.2-to-2.13.3.sql -- see that file's header
-- for the full reasoning (decision 5/6/7/8/39). The one Timescale-specific note: spans and
-- log_records are hypertables here (schema 2.11.0/native since inception respectively), so
-- CREATE INDEX propagates to every existing chunk, which is the slow part of this migration on
-- a large installation. Compressed chunks (>7 days old) do not benefit from the new GIN/trigram
-- indexes and fall back to scanning them -- see CLAUDE.md and Timescale-Schema.sql's own note.
-- No GIN index is added on the five metric data-point tables' attributes_json for the same
-- EXPLAIN-based reason as the PostgreSQL migration.

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
