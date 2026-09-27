-- Migration: schema 2.13.0 -> 2.13.1 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-2.13.0-to-2.13.1.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction.
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.1 does
-- nothing but refresh the schema_version timestamp.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 3 section):
--
--   1. idx_spans_root_time: a filtered index anchoring the trace page/summary on root spans
--      (INCLUDE end_time_unix_nano so mode=slow's duration check runs inside the index).
--   2. orphan_roots: traces whose root span never arrived (decision 41), written by the rollup
--      worker.
--   3. trace_rollup_minute/trace_rollup_hour: per-minute/hour trace summary tables (decisions
--      37-38), plus their rollup_state seed rows.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

-- =============================================================================
-- 2.13.1 -- root-span index
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_spans_root_time' AND object_id = OBJECT_ID('spans'))
    CREATE INDEX idx_spans_root_time ON spans (start_time_unix_nano DESC) INCLUDE (end_time_unix_nano)
        WHERE parent_span_id IS NULL;
GO

-- =============================================================================
-- 2.13.1 -- orphan_roots
-- =============================================================================

IF OBJECT_ID('orphan_roots', 'U') IS NULL
BEGIN
    CREATE TABLE orphan_roots (
        trace_id             CHAR(32)     NOT NULL,
        span_id              CHAR(16)     NOT NULL,
        resource_id          BIGINT       NOT NULL,
        start_time_unix_nano BIGINT       NOT NULL,
        end_time_unix_nano   BIGINT       NOT NULL,
        detected_at          DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT pk_orphan_roots PRIMARY KEY (trace_id)
    );
    CREATE INDEX idx_orphan_roots_start ON orphan_roots (start_time_unix_nano DESC, trace_id);
END
GO

-- =============================================================================
-- 2.13.1 -- trace rollup tables
-- =============================================================================

IF OBJECT_ID('trace_rollup_minute', 'U') IS NULL
BEGIN
    CREATE TABLE trace_rollup_minute (
        bucket_unix_nano BIGINT        NOT NULL,
        resource_id      BIGINT        NOT NULL,
        root_name        NVARCHAR(255) NOT NULL,
        inbound          TINYINT       NOT NULL,
        trace_count      INT           NOT NULL DEFAULT 0,
        error_count      INT           NOT NULL DEFAULT 0,
        duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
        duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
        lb_00 INT NOT NULL DEFAULT 0, lb_01 INT NOT NULL DEFAULT 0, lb_02 INT NOT NULL DEFAULT 0,
        lb_03 INT NOT NULL DEFAULT 0, lb_04 INT NOT NULL DEFAULT 0, lb_05 INT NOT NULL DEFAULT 0,
        lb_06 INT NOT NULL DEFAULT 0, lb_07 INT NOT NULL DEFAULT 0, lb_08 INT NOT NULL DEFAULT 0,
        lb_09 INT NOT NULL DEFAULT 0, lb_10 INT NOT NULL DEFAULT 0, lb_11 INT NOT NULL DEFAULT 0,
        lb_12 INT NOT NULL DEFAULT 0, lb_13 INT NOT NULL DEFAULT 0, lb_14 INT NOT NULL DEFAULT 0,
        lb_15 INT NOT NULL DEFAULT 0, lb_16 INT NOT NULL DEFAULT 0, lb_17 INT NOT NULL DEFAULT 0,
        lb_18 INT NOT NULL DEFAULT 0, lb_19 INT NOT NULL DEFAULT 0, lb_20 INT NOT NULL DEFAULT 0,
        lb_21 INT NOT NULL DEFAULT 0, lb_22 INT NOT NULL DEFAULT 0, lb_23 INT NOT NULL DEFAULT 0,
        lb_24 INT NOT NULL DEFAULT 0, lb_25 INT NOT NULL DEFAULT 0, lb_26 INT NOT NULL DEFAULT 0,
        lb_27 INT NOT NULL DEFAULT 0, lb_28 INT NOT NULL DEFAULT 0, lb_29 INT NOT NULL DEFAULT 0,
        lb_30 INT NOT NULL DEFAULT 0, lb_31 INT NOT NULL DEFAULT 0, lb_32 INT NOT NULL DEFAULT 0,
        lb_33 INT NOT NULL DEFAULT 0, lb_34 INT NOT NULL DEFAULT 0, lb_35 INT NOT NULL DEFAULT 0,
        lb_36 INT NOT NULL DEFAULT 0, lb_37 INT NOT NULL DEFAULT 0, lb_38 INT NOT NULL DEFAULT 0,
        lb_39 INT NOT NULL DEFAULT 0,
        CONSTRAINT pk_trace_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
    );
    CREATE INDEX idx_trace_rollup_minute_bucket ON trace_rollup_minute (bucket_unix_nano);
END
GO

IF OBJECT_ID('trace_rollup_hour', 'U') IS NULL
BEGIN
    CREATE TABLE trace_rollup_hour (
        bucket_unix_nano BIGINT        NOT NULL,
        resource_id      BIGINT        NOT NULL,
        root_name        NVARCHAR(255) NOT NULL,
        inbound          TINYINT       NOT NULL,
        trace_count      INT           NOT NULL DEFAULT 0,
        error_count      INT           NOT NULL DEFAULT 0,
        duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
        duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
        lb_00 INT NOT NULL DEFAULT 0, lb_01 INT NOT NULL DEFAULT 0, lb_02 INT NOT NULL DEFAULT 0,
        lb_03 INT NOT NULL DEFAULT 0, lb_04 INT NOT NULL DEFAULT 0, lb_05 INT NOT NULL DEFAULT 0,
        lb_06 INT NOT NULL DEFAULT 0, lb_07 INT NOT NULL DEFAULT 0, lb_08 INT NOT NULL DEFAULT 0,
        lb_09 INT NOT NULL DEFAULT 0, lb_10 INT NOT NULL DEFAULT 0, lb_11 INT NOT NULL DEFAULT 0,
        lb_12 INT NOT NULL DEFAULT 0, lb_13 INT NOT NULL DEFAULT 0, lb_14 INT NOT NULL DEFAULT 0,
        lb_15 INT NOT NULL DEFAULT 0, lb_16 INT NOT NULL DEFAULT 0, lb_17 INT NOT NULL DEFAULT 0,
        lb_18 INT NOT NULL DEFAULT 0, lb_19 INT NOT NULL DEFAULT 0, lb_20 INT NOT NULL DEFAULT 0,
        lb_21 INT NOT NULL DEFAULT 0, lb_22 INT NOT NULL DEFAULT 0, lb_23 INT NOT NULL DEFAULT 0,
        lb_24 INT NOT NULL DEFAULT 0, lb_25 INT NOT NULL DEFAULT 0, lb_26 INT NOT NULL DEFAULT 0,
        lb_27 INT NOT NULL DEFAULT 0, lb_28 INT NOT NULL DEFAULT 0, lb_29 INT NOT NULL DEFAULT 0,
        lb_30 INT NOT NULL DEFAULT 0, lb_31 INT NOT NULL DEFAULT 0, lb_32 INT NOT NULL DEFAULT 0,
        lb_33 INT NOT NULL DEFAULT 0, lb_34 INT NOT NULL DEFAULT 0, lb_35 INT NOT NULL DEFAULT 0,
        lb_36 INT NOT NULL DEFAULT 0, lb_37 INT NOT NULL DEFAULT 0, lb_38 INT NOT NULL DEFAULT 0,
        lb_39 INT NOT NULL DEFAULT 0,
        CONSTRAINT pk_trace_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
    );
    CREATE INDEX idx_trace_rollup_hour_bucket ON trace_rollup_hour (bucket_unix_nano);
END
GO

IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'traces' AND granularity = N'minute')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'traces', N'minute');
IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'traces' AND granularity = N'hour')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'traces', N'hour');
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS tgt
USING (VALUES (N'2.13.1')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO
