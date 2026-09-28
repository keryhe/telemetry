-- Migration: schema 2.13.2 -> 2.13.3 (MySQL)
--
-- Usage:
--   mysql telemetry < MySQL-2.13.2-to-2.13.3.sql
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 7 section, decision 8/39):
--
--   resources.service_name: a real column, backfilled below from attributes_json's
--   "service.name" key via ->> (the same extraction ResourceServiceNameExpr used to compile per
--   query). ResourceServiceNameExpr() is now just "{alias}.service_name" on every provider,
--   including this one -- the service_name column itself is NOT part of the decision 39 tiering
--   split. MySQL stays on the standard tier for search: no index changes to the phase 1
--   unindexed LIKE/JSON_EXTRACT predicates, still bounded by the 24-hour raw search window
--   (ProviderCapabilities). No MySQL FULLTEXT index is added -- out of scope per tiering.
--
-- MySQL has no "ADD COLUMN IF NOT EXISTS" before 8.0 information_schema guards are used instead,
-- mirroring this repository's other MySQL migrations' idiom of plain idempotent DDL where the
-- server supports it (8.0+ is already required elsewhere in this schema for the JSON type and
-- CHECK constraints, so IF NOT EXISTS on ADD COLUMN and ADD INDEX -- both 8.0.29+ -- are used
-- directly below).

ALTER TABLE resources ADD COLUMN IF NOT EXISTS service_name VARCHAR(255);

UPDATE resources
SET service_name = attributes_json ->> '$."service.name"'
WHERE service_name IS NULL AND attributes_json IS NOT NULL;

ALTER TABLE resources ADD INDEX IF NOT EXISTS idx_resources_service_name (service_name);

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version (version, applied_at)
VALUES ('2.13.3', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);
