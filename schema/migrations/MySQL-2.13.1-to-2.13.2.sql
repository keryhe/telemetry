-- Migration: schema 2.13.1 -> 2.13.2 (MySQL)
--
-- Usage:
--   mysql telemetry < MySQL-2.13.1-to-2.13.2.sql
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 5 section, decision 27):
--
--   metric_last_seen: a new table (NOT a column on metrics) that the metrics catalog reads for
--   its "has data in range" check instead of scanning the five data-point tables. No FK to
--   metrics -- see the table's own comment in MySQL-Schema.sql. Backfilled below from
--   MAX(time_unix_nano) per metric_id across the five data-point tables.
--
--   DEVIATION from the plan's "batched" wording, documented here rather than silently: unlike the
--   PostgreSQL/Timescale (PL/pgSQL DO loop) and SQL Server (T-SQL WHILE loop) migrations, this one
--   backfills each data-point table in a single INSERT ... SELECT ... GROUP BY pass rather than
--   chunking by metric_id range. plain `mysql < file` scripts have no portable loop construct
--   without wrapping the whole migration in a stored procedure, which was judged not worth the
--   added complexity for a one-time upgrade step. On a large installation this single pass can
--   run for minutes to hours and takes InnoDB's ordinary read locks on the source tables for its
--   duration (each is covered by the existing (metric_id, time_unix_nano DESC) index, so it is a
--   sequential per-metric index scan, not a full table scan) -- run it during a maintenance
--   window on a very large database. If this needs chunking in practice, wrap the five INSERTs
--   below in a stored procedure with a metric_id range loop, mirroring the SQL Server migration's
--   shape.

CREATE TABLE IF NOT EXISTS metric_last_seen (
    metric_id           BIGINT NOT NULL PRIMARY KEY,
    last_seen_unix_nano BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);

-- =============================================================================
-- 2.13.2 -- backfill metric_last_seen from the five data-point tables
-- =============================================================================

INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
SELECT metric_id, MAX(time_unix_nano) FROM gauge_data_points GROUP BY metric_id
ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));

INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
SELECT metric_id, MAX(time_unix_nano) FROM sum_data_points GROUP BY metric_id
ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));

INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
SELECT metric_id, MAX(time_unix_nano) FROM histogram_data_points GROUP BY metric_id
ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));

INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
SELECT metric_id, MAX(time_unix_nano) FROM exponential_histogram_data_points GROUP BY metric_id
ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));

INSERT INTO metric_last_seen (metric_id, last_seen_unix_nano)
SELECT metric_id, MAX(time_unix_nano) FROM summary_data_points GROUP BY metric_id
ON DUPLICATE KEY UPDATE last_seen_unix_nano = GREATEST(last_seen_unix_nano, VALUES(last_seen_unix_nano));

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version (version, applied_at)
VALUES ('2.13.2', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);
