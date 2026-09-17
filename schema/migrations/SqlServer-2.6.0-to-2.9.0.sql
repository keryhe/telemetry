-- Migration: schema 2.6.0 -> 2.9.0 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-2.6.0-to-2.9.0.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction. Requires SQL Server 2016 or later (DROP ... IF EXISTS).
--
-- The SQL Server counterpart of PostgreSQL-2.6.0-to-2.9.0.sql. Same three version steps and the
-- same logical outcome; the differences from the PostgreSQL script are only where the two engines
-- differ -- SQL Server has no GIN indexes to drop, and it needs the standalone time indexes that
-- PostgreSQL gets for free (from the TimescaleDB hypertable index, or from the BRIN index on
-- plain PostgreSQL).
--
-- Every step is guarded, so this is also the correct script for a database already at 2.7.0 or
-- 2.8.0 -- the steps it has already had become no-ops. Re-running it on a database already at
-- 2.9.0 does nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.9.0 or it is left exactly as it
-- was. XACT_ABORT is ON so any error rolls the whole thing back rather than continuing on a
-- half-migrated database.
--
-- What each version step changed, and why (see CLAUDE.md for the full rationale):
--
--   2.7.0  metrics gained uk_metric_identity UNIQUE (resource_id, name, type, scope_id), so the
--          catalog holds one row per metric identity instead of one row per OTLP export cycle.
--          Existing databases therefore carry duplicates that MUST be collapsed before the
--          constraint can be created -- step 1 below. idx_resource_name became an exact left
--          prefix of the new constraint and is dropped. The four data-point tables that lacked
--          one gained a standalone time index, because metric retention deletes from them
--          directly on time_unix_nano instead of cascading from metrics.
--   2.8.0  Four redundant B-tree indexes dropped from spans: idx_trace_id, idx_start_time,
--          idx_kind, idx_status.
--   2.9.0  Exemplars moved onto the data point that owns them. The single exemplar_id column
--          could hold only one exemplar where OTLP allows many, and no writer ever populated it,
--          so no data is lost by dropping it or the shared exemplars table.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
GO

BEGIN TRANSACTION;
GO

-- =============================================================================
-- 2.7.0 -- collapse duplicate metrics rows, then add uk_metric_identity
-- =============================================================================

-- Survivor per identity = MIN(id), i.e. the earliest row, whose created_at is the earliest this
-- metric was seen. That is exactly what created_at means from 2.7.0 on ("first seen"), so the
-- surviving row already carries the right timestamp.
--
-- NOTE on collation: uk_metric_identity compares `name` under the database collation, which is
-- case-insensitive by default. This dedup deliberately matches that, so the rows it collapses are
-- exactly the rows the constraint would reject.
CREATE TABLE #metric_dedup_map (old_id BIGINT PRIMARY KEY, new_id BIGINT NOT NULL);

INSERT INTO #metric_dedup_map (old_id, new_id)
SELECT old_id, new_id
FROM (
    SELECT id AS old_id,
           MIN(id) OVER (PARTITION BY resource_id, name, [type], scope_id) AS new_id
    FROM metrics
) AS m
WHERE old_id <> new_id;   -- keep only the rows that actually move

-- Repoint data points BEFORE deleting anything: the data-point foreign keys are ON DELETE
-- CASCADE, so deleting a duplicate metrics row first would take its data points with it.
UPDATE dp SET dp.metric_id = m.new_id
  FROM gauge_data_points dp INNER JOIN #metric_dedup_map m ON dp.metric_id = m.old_id;
UPDATE dp SET dp.metric_id = m.new_id
  FROM sum_data_points dp INNER JOIN #metric_dedup_map m ON dp.metric_id = m.old_id;
UPDATE dp SET dp.metric_id = m.new_id
  FROM histogram_data_points dp INNER JOIN #metric_dedup_map m ON dp.metric_id = m.old_id;
UPDATE dp SET dp.metric_id = m.new_id
  FROM exponential_histogram_data_points dp INNER JOIN #metric_dedup_map m ON dp.metric_id = m.old_id;
UPDATE dp SET dp.metric_id = m.new_id
  FROM summary_data_points dp INNER JOIN #metric_dedup_map m ON dp.metric_id = m.old_id;

-- Now childless, so the cascade removes nothing.
DELETE m FROM metrics m INNER JOIN #metric_dedup_map d ON m.id = d.old_id;

DROP TABLE #metric_dedup_map;
GO

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'uk_metric_identity' AND parent_object_id = OBJECT_ID('metrics'))
    ALTER TABLE metrics ADD CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, [type], scope_id);
GO

-- Redundant once uk_metric_identity exists: (resource_id, name) is its exact left prefix.
DROP INDEX IF EXISTS idx_resource_name ON metrics;
GO

-- Standalone time indexes: metric retention deletes from the data-point tables directly on
-- time_unix_nano rather than cascading from metrics, so each table needs one of its own.
-- gauge_data_points already had idx_gauge_time before 2.7.0.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_gauge_time' AND object_id = OBJECT_ID('gauge_data_points'))
    CREATE INDEX idx_gauge_time ON gauge_data_points (time_unix_nano DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_sum_time' AND object_id = OBJECT_ID('sum_data_points'))
    CREATE INDEX idx_sum_time ON sum_data_points (time_unix_nano DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_histogram_time' AND object_id = OBJECT_ID('histogram_data_points'))
    CREATE INDEX idx_histogram_time ON histogram_data_points (time_unix_nano DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_exp_histogram_time' AND object_id = OBJECT_ID('exponential_histogram_data_points'))
    CREATE INDEX idx_exp_histogram_time ON exponential_histogram_data_points (time_unix_nano DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_summary_time' AND object_id = OBJECT_ID('summary_data_points'))
    CREATE INDEX idx_summary_time ON summary_data_points (time_unix_nano DESC);
GO

-- =============================================================================
-- 2.8.0 -- drop the redundant spans indexes
-- =============================================================================

DROP INDEX IF EXISTS idx_trace_id   ON spans;  -- left prefix of uk_trace_span (trace_id, span_id)
DROP INDEX IF EXISTS idx_start_time ON spans;  -- left prefix of idx_duration (start, end)
DROP INDEX IF EXISTS idx_kind       ON spans;  -- 6 distinct values; never chosen by the optimizer
DROP INDEX IF EXISTS idx_status     ON spans;  -- 3 distinct values; never chosen by the optimizer
-- No GIN equivalent on SQL Server, so the two attributes_json index drops have no counterpart here.
GO

-- =============================================================================
-- 2.9.0 -- exemplars move onto the data point rows
-- =============================================================================

-- exemplar_id was never written by any provider, so these columns are empty everywhere and the
-- drop discards nothing. Summary data points are untouched: OTLP Summary has no exemplars.
IF COL_LENGTH('gauge_data_points', 'exemplar_id') IS NOT NULL
    ALTER TABLE gauge_data_points DROP COLUMN exemplar_id;
IF COL_LENGTH('sum_data_points', 'exemplar_id') IS NOT NULL
    ALTER TABLE sum_data_points DROP COLUMN exemplar_id;
IF COL_LENGTH('histogram_data_points', 'exemplar_id') IS NOT NULL
    ALTER TABLE histogram_data_points DROP COLUMN exemplar_id;
IF COL_LENGTH('exponential_histogram_data_points', 'exemplar_id') IS NOT NULL
    ALTER TABLE exponential_histogram_data_points DROP COLUMN exemplar_id;
GO

IF COL_LENGTH('gauge_data_points', 'exemplars_json') IS NULL
    ALTER TABLE gauge_data_points ADD exemplars_json NVARCHAR(MAX);
IF COL_LENGTH('sum_data_points', 'exemplars_json') IS NULL
    ALTER TABLE sum_data_points ADD exemplars_json NVARCHAR(MAX);
IF COL_LENGTH('histogram_data_points', 'exemplars_json') IS NULL
    ALTER TABLE histogram_data_points ADD exemplars_json NVARCHAR(MAX);
IF COL_LENGTH('exponential_histogram_data_points', 'exemplars_json') IS NULL
    ALTER TABLE exponential_histogram_data_points ADD exemplars_json NVARCHAR(MAX);
GO

-- Always empty (nothing ever inserted into it), and nothing references it: the data-point
-- exemplar_id columns carried no foreign key.
DROP TABLE IF EXISTS exemplars;
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS tgt
USING (VALUES (N'2.9.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO
