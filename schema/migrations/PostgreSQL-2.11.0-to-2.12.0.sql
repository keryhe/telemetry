-- Migration: schema 2.11.0 -> 2.12.0 (PostgreSQL, plain)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f PostgreSQL-2.11.0-to-2.12.0.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.12.0 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.12.0 or it is left exactly as it
-- was.
--
-- What changed, and why (see CLAUDE.md's "spans index set" notes):
--
--   1. idx_spans_error is added: a partial index on spans("start_time_unix_nano" DESC) WHERE
--      status_code = 'ERROR', backing mode=errors trace queries. Not a reversal of idx_status's
--      2.8.0 removal -- that was a plain B-tree over all three status values, too low-cardinality
--      for the planner to ever choose; this covers only the rare error rows (ERROR is the
--      minority status in practice).

BEGIN;

-- =============================================================================
-- 2.12.0 -- add idx_spans_error for mode=errors trace queries
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_spans_error ON spans ("start_time_unix_nano" DESC) WHERE "status_code" = 'ERROR';

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.12.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
