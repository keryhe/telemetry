-- Migration: schema 2.11.0 -> 2.12.0 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-2.11.0-to-2.12.0.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction.
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.12.0 does
-- nothing but refresh the schema_version timestamp.
--
-- What changed, and why (see CLAUDE.md's "spans index set" notes):
--
--   1. idx_spans_error is added: a filtered index on spans(start_time_unix_nano DESC) WHERE
--      status_code = 'ERROR', backing mode=errors trace queries. Not a reversal of idx_status's
--      2.8.0 removal -- that was a plain index over all three status values, too low-cardinality
--      for the planner to ever choose; this covers only the rare error rows (ERROR is the
--      minority status in practice).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

-- =============================================================================
-- 2.12.0 -- add idx_spans_error for mode=errors trace queries
-- =============================================================================

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_spans_error' AND object_id = OBJECT_ID('spans'))
    CREATE INDEX idx_spans_error ON spans (start_time_unix_nano DESC) WHERE status_code = 'ERROR';
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS tgt
USING (VALUES (N'2.12.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO
