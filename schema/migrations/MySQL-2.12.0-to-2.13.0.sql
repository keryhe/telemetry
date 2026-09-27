-- Migration: schema 2.12.0 -> 2.13.0 (MySQL)
--
-- Usage:
--   mysql telemetry < MySQL-2.12.0-to-2.13.0.sql
--
-- MySQL's DDL is not transactional (implicit commit per statement), so this cannot be wrapped in
-- one all-or-nothing transaction the way the other providers' migrations are -- each statement
-- below is independently idempotent instead, so a re-run after a partial failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 2 section):
--
--   1. idx_log_time (time_unix_nano DESC) is replaced by idx_log_time_id
--      (time_unix_nano DESC, id DESC), the keyset-paging tiebreak GetLogPageAsync needs. The old
--      index is a pure left prefix of the new one, so it is dropped (same reasoning as the 2.8.0
--      spans index cleanup).
--   2. rollup_state/log_rollup_minute/log_rollup_hour are added, backing the new RollupWorker
--      (decisions 37-38). Seeded with the two rows this phase needs (logs/minute, logs/hour);
--      phase 3 adds traces/*.

-- =============================================================================
-- 2.13.0 -- keyset tiebreak index for log_records
-- =============================================================================

DROP PROCEDURE IF EXISTS _drop_idx_log_time;
DELIMITER //
CREATE PROCEDURE _drop_idx_log_time()
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_time'
    ) THEN
        ALTER TABLE log_records DROP INDEX idx_log_time;
    END IF;
END //
DELIMITER ;
CALL _drop_idx_log_time();
DROP PROCEDURE _drop_idx_log_time;

DROP PROCEDURE IF EXISTS _add_idx_log_time_id;
DELIMITER //
CREATE PROCEDURE _add_idx_log_time_id()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_time_id'
    ) THEN
        CREATE INDEX idx_log_time_id ON log_records (time_unix_nano DESC, id DESC);
    END IF;
END //
DELIMITER ;
CALL _add_idx_log_time_id();
DROP PROCEDURE _add_idx_log_time_id;

-- =============================================================================
-- 2.13.0 -- rollup tables
-- =============================================================================

CREATE TABLE IF NOT EXISTS rollup_state (
    signal_name                   VARCHAR(20)  NOT NULL,
    granularity              VARCHAR(10)  NOT NULL,
    coverage_start_unix_nano BIGINT,
    rolled_until_unix_nano   BIGINT       NOT NULL DEFAULT 0,
    repassed_until_unix_nano BIGINT       NOT NULL DEFAULT 0,
    lease_owner              VARCHAR(100),
    lease_expires_at         DATETIME(6)  NOT NULL DEFAULT '1970-01-01 00:00:00',
    PRIMARY KEY (signal_name, granularity)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS log_rollup_minute (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS _add_idx_log_rollup_minute_bucket;
DELIMITER //
CREATE PROCEDURE _add_idx_log_rollup_minute_bucket()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'log_rollup_minute' AND index_name = 'idx_log_rollup_minute_bucket'
    ) THEN
        CREATE INDEX idx_log_rollup_minute_bucket ON log_rollup_minute (bucket_unix_nano);
    END IF;
END //
DELIMITER ;
CALL _add_idx_log_rollup_minute_bucket();
DROP PROCEDURE _add_idx_log_rollup_minute_bucket;

CREATE TABLE IF NOT EXISTS log_rollup_hour (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

DROP PROCEDURE IF EXISTS _add_idx_log_rollup_hour_bucket;
DELIMITER //
CREATE PROCEDURE _add_idx_log_rollup_hour_bucket()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'log_rollup_hour' AND index_name = 'idx_log_rollup_hour_bucket'
    ) THEN
        CREATE INDEX idx_log_rollup_hour_bucket ON log_rollup_hour (bucket_unix_nano);
    END IF;
END //
DELIMITER ;
CALL _add_idx_log_rollup_hour_bucket();
DROP PROCEDURE _add_idx_log_rollup_hour_bucket;

INSERT INTO rollup_state (signal_name, granularity) VALUES ('logs', 'minute'), ('logs', 'hour')
ON DUPLICATE KEY UPDATE signal_name = signal_name;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version (version, applied_at)
VALUES ('2.13.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);
