-- Migration: schema 2.13.1 -> 2.13.2 (ClickHouse)
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-2.13.1-to-2.13.2.sql
--
-- ClickHouse DDL has no multi-statement transactions; every statement below is independently
-- idempotent (IF NOT EXISTS), so a re-run after a partial failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 5 section, decision 27):
--
--   metric_last_seen: an AggregatingMergeTree holding a partial maxState(time_unix_nano) per
--   metric_id, fed going forward by one materialized view per data-point table (created below).
--   Backfilled once via INSERT ... SELECT ... maxState(...) over each existing data-point table
--   -- cheap and single-pass on ClickHouse (a columnar aggregate over an already time-partitioned
--   MergeTree, no locks, no mutations), unlike the batched/chunked backfill the relational
--   providers' migrations need to avoid a long-held scan/lock.

CREATE TABLE IF NOT EXISTS metric_last_seen
(
    metric_id       Int64,
    last_seen_state AggregateFunction(max, Int64)
)
ENGINE = AggregatingMergeTree
ORDER BY metric_id;

CREATE MATERIALIZED VIEW IF NOT EXISTS mv_metric_last_seen_gauge
TO metric_last_seen AS
SELECT metric_id, maxState(time_unix_nano) AS last_seen_state
FROM gauge_data_points
GROUP BY metric_id;

CREATE MATERIALIZED VIEW IF NOT EXISTS mv_metric_last_seen_sum
TO metric_last_seen AS
SELECT metric_id, maxState(time_unix_nano) AS last_seen_state
FROM sum_data_points
GROUP BY metric_id;

CREATE MATERIALIZED VIEW IF NOT EXISTS mv_metric_last_seen_histogram
TO metric_last_seen AS
SELECT metric_id, maxState(time_unix_nano) AS last_seen_state
FROM histogram_data_points
GROUP BY metric_id;

CREATE MATERIALIZED VIEW IF NOT EXISTS mv_metric_last_seen_exponential_histogram
TO metric_last_seen AS
SELECT metric_id, maxState(time_unix_nano) AS last_seen_state
FROM exponential_histogram_data_points
GROUP BY metric_id;

CREATE MATERIALIZED VIEW IF NOT EXISTS mv_metric_last_seen_summary
TO metric_last_seen AS
SELECT metric_id, maxState(time_unix_nano) AS last_seen_state
FROM summary_data_points
GROUP BY metric_id;

-- =============================================================================
-- 2.13.2 -- one-time backfill from data already in the five data-point tables
-- =============================================================================
-- The materialized views above only fire on INSERTs from here forward, so existing rows need an
-- explicit backfill pass -- same reasoning as every other Phase 5 provider's migration.

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM gauge_data_points GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM sum_data_points GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM histogram_data_points GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM exponential_histogram_data_points GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM summary_data_points GROUP BY metric_id;

INSERT INTO schema_version (version) VALUES ('2.13.2');
