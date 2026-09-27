-- Migration: schema 2.11.0 -> 2.12.0 (MySQL)
--
-- Usage:
--   mysql telemetry < MySQL-2.11.0-to-2.12.0.sql
--
-- MySQL's DDL is not transactional (implicit commit per statement), so this cannot be wrapped in
-- one all-or-nothing transaction the way the other providers' migrations are -- each statement
-- below is independently idempotent instead, so a re-run after a partial failure is safe.
--
-- What changed, and why (see CLAUDE.md's "spans index set" notes):
--
--   1. idx_spans_error is added. MySQL has no filtered/partial index, unlike the other four
--      providers' idx_spans_error -- the nearest equivalent is a plain composite index leading on
--      the low-cardinality status_code column, which still lets mode=errors seek straight to the
--      'ERROR' slice of the index instead of scanning every row. Not the same "too low-cardinality"
--      case 2.8.0's idx_status was: this indexes the rare rows a status_code predicate actually
--      selects (errors are the minority status), not an equality lookup expected to touch most of
--      the table.

-- =============================================================================
-- 2.12.0 -- add idx_spans_error for mode=errors trace queries
-- =============================================================================

DROP PROCEDURE IF EXISTS _add_idx_spans_error;
DELIMITER //
CREATE PROCEDURE _add_idx_spans_error()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_error'
    ) THEN
        CREATE INDEX idx_spans_error ON spans (status_code, start_time_unix_nano DESC);
    END IF;
END //
DELIMITER ;
CALL _add_idx_spans_error();
DROP PROCEDURE _add_idx_spans_error;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version (version, applied_at)
VALUES ('2.12.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);
