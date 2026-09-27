-- Migration: schema 2.12.0 -> 2.13.0 (ClickHouse)
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-2.12.0-to-2.13.0.sql
--
-- ClickHouse DDL has no multi-statement transactions; every statement below is independently
-- idempotent (IF NOT EXISTS), so a re-run after a partial failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 2 section):
--
--   rollup_state/log_rollup_minute/log_rollup_hour are added, backing the new RollupWorker
--   (decisions 37-38). log_records' own ORDER BY (time_unix_nano, resource_id) needs no change
--   -- see the comment on that table in ClickHouse-Schema.sql for why the keyset tiebreak index
--   the other four providers add is unnecessary here.

CREATE TABLE IF NOT EXISTS rollup_state
(
    signal_name                   String,
    granularity              String,
    coverage_start_unix_nano Nullable(Int64),
    rolled_until_unix_nano   Int64 DEFAULT 0,
    repassed_until_unix_nano Int64 DEFAULT 0,
    lease_owner              Nullable(String),
    lease_expires_at         DateTime64(9) DEFAULT toDateTime64(0, 9)
)
ENGINE = ReplacingMergeTree
ORDER BY (signal_name, granularity);

CREATE TABLE IF NOT EXISTS log_rollup_minute
(
    bucket_unix_nano Int64,
    resource_id      Int64,
    trace_count      Int32 DEFAULT 0,
    debug_count      Int32 DEFAULT 0,
    info_count       Int32 DEFAULT 0,
    warn_count       Int32 DEFAULT 0,
    error_count      Int32 DEFAULT 0,
    fatal_count      Int32 DEFAULT 0,
    rolled_at        DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree(rolled_at)
PARTITION BY toYYYYMMDD(fromUnixTimestamp64Nano(bucket_unix_nano))
ORDER BY (bucket_unix_nano, resource_id);

CREATE TABLE IF NOT EXISTS log_rollup_hour
(
    bucket_unix_nano Int64,
    resource_id      Int64,
    trace_count      Int32 DEFAULT 0,
    debug_count      Int32 DEFAULT 0,
    info_count       Int32 DEFAULT 0,
    warn_count       Int32 DEFAULT 0,
    error_count      Int32 DEFAULT 0,
    fatal_count      Int32 DEFAULT 0,
    rolled_at        DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree(rolled_at)
ORDER BY (bucket_unix_nano, resource_id);

INSERT INTO rollup_state (signal_name, granularity)
SELECT 'logs', 'minute'
WHERE NOT EXISTS (SELECT 1 FROM rollup_state WHERE signal_name = 'logs' AND granularity = 'minute');

INSERT INTO rollup_state (signal_name, granularity)
SELECT 'logs', 'hour'
WHERE NOT EXISTS (SELECT 1 FROM rollup_state WHERE signal_name = 'logs' AND granularity = 'hour');

INSERT INTO schema_version (version) VALUES ('2.13.0');
