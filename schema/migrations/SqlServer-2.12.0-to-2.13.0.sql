-- Migration: schema 2.12.0 -> 2.13.0 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-2.12.0-to-2.13.0.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction.
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.0 does
-- nothing but refresh the schema_version timestamp.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 2 section):
--
--   1. ALLOW_SNAPSHOT_ISOLATION is turned on for the database (decision 35), needed by the API's
--      read repositories' SET TRANSACTION ISOLATION LEVEL SNAPSHOT connections. This is a
--      database-level ALTER DATABASE and SQL Server refuses it inside a user transaction, so it
--      MUST run as its own batch, before BEGIN TRANSACTION below -- there is no precedent for
--      this split elsewhere in schema/migrations/, since no earlier migration needed a
--      database-level setting. It waits for in-flight transactions to finish but needs no
--      single-user mode.
--   2. idx_log_time (time_unix_nano DESC) is replaced by idx_log_time_id
--      (time_unix_nano DESC, id DESC), the keyset-paging tiebreak GetLogPageAsync needs. The old
--      index is a pure left prefix of the new one, so it is dropped (same reasoning as the 2.8.0
--      spans index cleanup).
--   3. rollup_state/log_rollup_minute/log_rollup_hour are added, backing the new RollupWorker
--      (decisions 37-38). Seeded with the two rows this phase needs (logs/minute, logs/hour);
--      phase 3 adds traces/*.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- 2.13.0 -- enable snapshot isolation (decision 35) -- OUTSIDE any transaction
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.databases
    WHERE database_id = DB_ID()
      AND snapshot_isolation_state = 1
)
    ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

-- =============================================================================
-- 2.13.0 -- keyset tiebreak index for log_records
-- =============================================================================

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_log_time' AND object_id = OBJECT_ID('log_records'))
    DROP INDEX idx_log_time ON log_records;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_log_time_id' AND object_id = OBJECT_ID('log_records'))
    CREATE INDEX idx_log_time_id ON log_records (time_unix_nano DESC, id DESC);
GO

-- =============================================================================
-- 2.13.0 -- rollup tables
-- =============================================================================

IF OBJECT_ID('rollup_state', 'U') IS NULL
BEGIN
    CREATE TABLE rollup_state (
        [signal_name]                  NVARCHAR(20)  NOT NULL,
        granularity               NVARCHAR(10)  NOT NULL,
        coverage_start_unix_nano  BIGINT,
        rolled_until_unix_nano    BIGINT        NOT NULL DEFAULT 0,
        repassed_until_unix_nano  BIGINT        NOT NULL DEFAULT 0,
        lease_owner               NVARCHAR(100),
        lease_expires_at          DATETIME2     NOT NULL DEFAULT '1970-01-01T00:00:00',
        CONSTRAINT pk_rollup_state PRIMARY KEY ([signal_name], granularity)
    );
END
GO

IF OBJECT_ID('log_rollup_minute', 'U') IS NULL
BEGIN
    CREATE TABLE log_rollup_minute (
        bucket_unix_nano BIGINT NOT NULL,
        resource_id      BIGINT NOT NULL,
        trace_count      INT    NOT NULL DEFAULT 0,
        debug_count      INT    NOT NULL DEFAULT 0,
        info_count       INT    NOT NULL DEFAULT 0,
        warn_count       INT    NOT NULL DEFAULT 0,
        error_count      INT    NOT NULL DEFAULT 0,
        fatal_count      INT    NOT NULL DEFAULT 0,
        CONSTRAINT pk_log_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id)
    );
    CREATE INDEX idx_log_rollup_minute_bucket ON log_rollup_minute (bucket_unix_nano);
END
GO

IF OBJECT_ID('log_rollup_hour', 'U') IS NULL
BEGIN
    CREATE TABLE log_rollup_hour (
        bucket_unix_nano BIGINT NOT NULL,
        resource_id      BIGINT NOT NULL,
        trace_count      INT    NOT NULL DEFAULT 0,
        debug_count      INT    NOT NULL DEFAULT 0,
        info_count       INT    NOT NULL DEFAULT 0,
        warn_count       INT    NOT NULL DEFAULT 0,
        error_count      INT    NOT NULL DEFAULT 0,
        fatal_count      INT    NOT NULL DEFAULT 0,
        CONSTRAINT pk_log_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id)
    );
    CREATE INDEX idx_log_rollup_hour_bucket ON log_rollup_hour (bucket_unix_nano);
END
GO

IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'logs' AND granularity = N'minute')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'logs', N'minute');
IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'logs' AND granularity = N'hour')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'logs', N'hour');
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS tgt
USING (VALUES (N'2.13.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO
