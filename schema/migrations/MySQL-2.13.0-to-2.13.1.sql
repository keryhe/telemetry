-- Migration: schema 2.13.0 -> 2.13.1 (MySQL)
--
-- Usage:
--   mysql telemetry < MySQL-2.13.0-to-2.13.1.sql
--
-- MySQL's DDL is not transactional (implicit commit per statement), so this cannot be wrapped in
-- one all-or-nothing transaction the way the other providers' migrations are -- each statement
-- below is independently idempotent instead, so a re-run after a partial failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 3 section):
--
--   1. spans gains a stored generated column is_root (parent_span_id IS NULL), plus
--      idx_spans_root_time on (is_root, start_time_unix_nano DESC, end_time_unix_nano) -- MySQL
--      has no filtered index, so root-anchored queries seek this generated column instead.
--   2. orphan_roots: traces whose root span never arrived (decision 41), written by the rollup
--      worker.
--   3. trace_rollup_minute/trace_rollup_hour: per-minute/hour trace summary tables (decisions
--      37-38), plus their rollup_state seed rows.

-- =============================================================================
-- 2.13.1 -- is_root generated column + root-span index
-- =============================================================================

DROP PROCEDURE IF EXISTS _add_spans_is_root;
DELIMITER //
CREATE PROCEDURE _add_spans_is_root()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'spans' AND column_name = 'is_root'
    ) THEN
        ALTER TABLE spans ADD COLUMN is_root BOOLEAN GENERATED ALWAYS AS (parent_span_id IS NULL) STORED;
    END IF;
END //
DELIMITER ;
CALL _add_spans_is_root();
DROP PROCEDURE _add_spans_is_root;

DROP PROCEDURE IF EXISTS _add_idx_spans_root_time;
DELIMITER //
CREATE PROCEDURE _add_idx_spans_root_time()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_root_time'
    ) THEN
        CREATE INDEX idx_spans_root_time ON spans (is_root, start_time_unix_nano DESC, end_time_unix_nano);
    END IF;
END //
DELIMITER ;
CALL _add_idx_spans_root_time();
DROP PROCEDURE _add_idx_spans_root_time;

-- =============================================================================
-- 2.13.1 -- orphan_roots
-- =============================================================================

CREATE TABLE IF NOT EXISTS orphan_roots (
    trace_id             CHAR(32)     NOT NULL PRIMARY KEY,
    span_id              CHAR(16)     NOT NULL,
    resource_id          BIGINT       NOT NULL,
    start_time_unix_nano BIGINT       NOT NULL,
    end_time_unix_nano   BIGINT       NOT NULL,
    detected_at          DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS _add_idx_orphan_roots_start;
DELIMITER //
CREATE PROCEDURE _add_idx_orphan_roots_start()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'orphan_roots' AND index_name = 'idx_orphan_roots_start'
    ) THEN
        CREATE INDEX idx_orphan_roots_start ON orphan_roots (start_time_unix_nano DESC, trace_id);
    END IF;
END //
DELIMITER ;
CALL _add_idx_orphan_roots_start();
DROP PROCEDURE _add_idx_orphan_roots_start;

-- =============================================================================
-- 2.13.1 -- trace rollup tables
-- =============================================================================

CREATE TABLE IF NOT EXISTS trace_rollup_minute (
    bucket_unix_nano BIGINT       NOT NULL,
    resource_id      BIGINT       NOT NULL,
    root_name        VARCHAR(255) NOT NULL,
    inbound          TINYINT      NOT NULL,
    trace_count      INT          NOT NULL DEFAULT 0,
    error_count      INT          NOT NULL DEFAULT 0,
    duration_sum_ms  DOUBLE       NOT NULL DEFAULT 0,
    duration_max_ms  DOUBLE       NOT NULL DEFAULT 0,
    lb_00 INT NOT NULL DEFAULT 0,
    lb_01 INT NOT NULL DEFAULT 0,
    lb_02 INT NOT NULL DEFAULT 0,
    lb_03 INT NOT NULL DEFAULT 0,
    lb_04 INT NOT NULL DEFAULT 0,
    lb_05 INT NOT NULL DEFAULT 0,
    lb_06 INT NOT NULL DEFAULT 0,
    lb_07 INT NOT NULL DEFAULT 0,
    lb_08 INT NOT NULL DEFAULT 0,
    lb_09 INT NOT NULL DEFAULT 0,
    lb_10 INT NOT NULL DEFAULT 0,
    lb_11 INT NOT NULL DEFAULT 0,
    lb_12 INT NOT NULL DEFAULT 0,
    lb_13 INT NOT NULL DEFAULT 0,
    lb_14 INT NOT NULL DEFAULT 0,
    lb_15 INT NOT NULL DEFAULT 0,
    lb_16 INT NOT NULL DEFAULT 0,
    lb_17 INT NOT NULL DEFAULT 0,
    lb_18 INT NOT NULL DEFAULT 0,
    lb_19 INT NOT NULL DEFAULT 0,
    lb_20 INT NOT NULL DEFAULT 0,
    lb_21 INT NOT NULL DEFAULT 0,
    lb_22 INT NOT NULL DEFAULT 0,
    lb_23 INT NOT NULL DEFAULT 0,
    lb_24 INT NOT NULL DEFAULT 0,
    lb_25 INT NOT NULL DEFAULT 0,
    lb_26 INT NOT NULL DEFAULT 0,
    lb_27 INT NOT NULL DEFAULT 0,
    lb_28 INT NOT NULL DEFAULT 0,
    lb_29 INT NOT NULL DEFAULT 0,
    lb_30 INT NOT NULL DEFAULT 0,
    lb_31 INT NOT NULL DEFAULT 0,
    lb_32 INT NOT NULL DEFAULT 0,
    lb_33 INT NOT NULL DEFAULT 0,
    lb_34 INT NOT NULL DEFAULT 0,
    lb_35 INT NOT NULL DEFAULT 0,
    lb_36 INT NOT NULL DEFAULT 0,
    lb_37 INT NOT NULL DEFAULT 0,
    lb_38 INT NOT NULL DEFAULT 0,
    lb_39 INT NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS _add_idx_trace_rollup_minute_bucket;
DELIMITER //
CREATE PROCEDURE _add_idx_trace_rollup_minute_bucket()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'trace_rollup_minute' AND index_name = 'idx_trace_rollup_minute_bucket'
    ) THEN
        CREATE INDEX idx_trace_rollup_minute_bucket ON trace_rollup_minute (bucket_unix_nano);
    END IF;
END //
DELIMITER ;
CALL _add_idx_trace_rollup_minute_bucket();
DROP PROCEDURE _add_idx_trace_rollup_minute_bucket;

CREATE TABLE IF NOT EXISTS trace_rollup_hour (
    bucket_unix_nano BIGINT       NOT NULL,
    resource_id      BIGINT       NOT NULL,
    root_name        VARCHAR(255) NOT NULL,
    inbound          TINYINT      NOT NULL,
    trace_count      INT          NOT NULL DEFAULT 0,
    error_count      INT          NOT NULL DEFAULT 0,
    duration_sum_ms  DOUBLE       NOT NULL DEFAULT 0,
    duration_max_ms  DOUBLE       NOT NULL DEFAULT 0,
    lb_00 INT NOT NULL DEFAULT 0,
    lb_01 INT NOT NULL DEFAULT 0,
    lb_02 INT NOT NULL DEFAULT 0,
    lb_03 INT NOT NULL DEFAULT 0,
    lb_04 INT NOT NULL DEFAULT 0,
    lb_05 INT NOT NULL DEFAULT 0,
    lb_06 INT NOT NULL DEFAULT 0,
    lb_07 INT NOT NULL DEFAULT 0,
    lb_08 INT NOT NULL DEFAULT 0,
    lb_09 INT NOT NULL DEFAULT 0,
    lb_10 INT NOT NULL DEFAULT 0,
    lb_11 INT NOT NULL DEFAULT 0,
    lb_12 INT NOT NULL DEFAULT 0,
    lb_13 INT NOT NULL DEFAULT 0,
    lb_14 INT NOT NULL DEFAULT 0,
    lb_15 INT NOT NULL DEFAULT 0,
    lb_16 INT NOT NULL DEFAULT 0,
    lb_17 INT NOT NULL DEFAULT 0,
    lb_18 INT NOT NULL DEFAULT 0,
    lb_19 INT NOT NULL DEFAULT 0,
    lb_20 INT NOT NULL DEFAULT 0,
    lb_21 INT NOT NULL DEFAULT 0,
    lb_22 INT NOT NULL DEFAULT 0,
    lb_23 INT NOT NULL DEFAULT 0,
    lb_24 INT NOT NULL DEFAULT 0,
    lb_25 INT NOT NULL DEFAULT 0,
    lb_26 INT NOT NULL DEFAULT 0,
    lb_27 INT NOT NULL DEFAULT 0,
    lb_28 INT NOT NULL DEFAULT 0,
    lb_29 INT NOT NULL DEFAULT 0,
    lb_30 INT NOT NULL DEFAULT 0,
    lb_31 INT NOT NULL DEFAULT 0,
    lb_32 INT NOT NULL DEFAULT 0,
    lb_33 INT NOT NULL DEFAULT 0,
    lb_34 INT NOT NULL DEFAULT 0,
    lb_35 INT NOT NULL DEFAULT 0,
    lb_36 INT NOT NULL DEFAULT 0,
    lb_37 INT NOT NULL DEFAULT 0,
    lb_38 INT NOT NULL DEFAULT 0,
    lb_39 INT NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS _add_idx_trace_rollup_hour_bucket;
DELIMITER //
CREATE PROCEDURE _add_idx_trace_rollup_hour_bucket()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'trace_rollup_hour' AND index_name = 'idx_trace_rollup_hour_bucket'
    ) THEN
        CREATE INDEX idx_trace_rollup_hour_bucket ON trace_rollup_hour (bucket_unix_nano);
    END IF;
END //
DELIMITER ;
CALL _add_idx_trace_rollup_hour_bucket();
DROP PROCEDURE _add_idx_trace_rollup_hour_bucket;

INSERT INTO rollup_state (signal_name, granularity) VALUES ('traces', 'minute'), ('traces', 'hour')
ON DUPLICATE KEY UPDATE signal_name = signal_name;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version (version, applied_at)
VALUES ('2.13.1', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);
