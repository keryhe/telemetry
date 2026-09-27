-- Migration: schema 2.12.0 -> 2.13.0 (TimescaleDB)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f Timescale-2.12.0-to-2.13.0.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.0 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.13.0 or it is left exactly as it
-- was.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 2 section):
--
--   1. idx_log_time (time_unix_nano DESC) is replaced by idx_log_time_id
--      (time_unix_nano DESC, id DESC), the keyset-paging tiebreak GetLogPageAsync needs. The old
--      index is a pure left prefix of the new one, so it is dropped (same reasoning as the 2.8.0
--      spans index cleanup).
--   2. rollup_state/log_rollup_minute/log_rollup_hour are added as plain (non-hypertable)
--      tables, backing the new RollupWorker (decisions 37-38). Seeded with the two rows this
--      phase needs (logs/minute, logs/hour); phase 3 adds traces/*.

BEGIN;

-- =============================================================================
-- 2.13.0 -- keyset tiebreak index for log_records
-- =============================================================================

DROP INDEX IF EXISTS idx_log_time;
CREATE INDEX IF NOT EXISTS idx_log_time_id ON log_records ("time_unix_nano" DESC, "id" DESC);

-- =============================================================================
-- 2.13.0 -- rollup tables
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

INSERT INTO rollup_state ("signal_name", "granularity") VALUES ('logs', 'minute'), ('logs', 'hour')
ON CONFLICT ("signal_name", "granularity") DO NOTHING;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.13.0')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
