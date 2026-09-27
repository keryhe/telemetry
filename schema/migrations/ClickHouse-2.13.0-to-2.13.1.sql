-- Migration: schema 2.13.0 -> 2.13.1 (ClickHouse)
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-2.13.0-to-2.13.1.sql
--
-- ClickHouse DDL has no multi-statement transactions; every statement below is independently
-- idempotent (IF NOT EXISTS), so a re-run after a partial failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 3 section):
--
--   orphan_roots/trace_rollup_minute/trace_rollup_hour are added, backing the trace half of the
--   RollupWorker (decisions 37-38, 41). No idx_spans_root_time equivalent and no change to
--   spans' own ORDER BY -- see the comment on the spans table in ClickHouse-Schema.sql for why
--   (spans already carries created_at, unlike the other four providers before this phase).

CREATE TABLE IF NOT EXISTS orphan_roots
(
    trace_id             String,
    span_id              String,
    resource_id          Int64,
    start_time_unix_nano Int64,
    end_time_unix_nano   Int64,
    detected_at          DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree(detected_at)
ORDER BY trace_id;

CREATE TABLE IF NOT EXISTS trace_rollup_minute
(
    bucket_unix_nano Int64,
    resource_id      Int64,
    root_name        String,
    inbound          UInt8,
    trace_count      Int32 DEFAULT 0,
    error_count      Int32 DEFAULT 0,
    duration_sum_ms  Float64 DEFAULT 0,
    duration_max_ms  Float64 DEFAULT 0,
    lb_00 Int32 DEFAULT 0,
    lb_01 Int32 DEFAULT 0,
    lb_02 Int32 DEFAULT 0,
    lb_03 Int32 DEFAULT 0,
    lb_04 Int32 DEFAULT 0,
    lb_05 Int32 DEFAULT 0,
    lb_06 Int32 DEFAULT 0,
    lb_07 Int32 DEFAULT 0,
    lb_08 Int32 DEFAULT 0,
    lb_09 Int32 DEFAULT 0,
    lb_10 Int32 DEFAULT 0,
    lb_11 Int32 DEFAULT 0,
    lb_12 Int32 DEFAULT 0,
    lb_13 Int32 DEFAULT 0,
    lb_14 Int32 DEFAULT 0,
    lb_15 Int32 DEFAULT 0,
    lb_16 Int32 DEFAULT 0,
    lb_17 Int32 DEFAULT 0,
    lb_18 Int32 DEFAULT 0,
    lb_19 Int32 DEFAULT 0,
    lb_20 Int32 DEFAULT 0,
    lb_21 Int32 DEFAULT 0,
    lb_22 Int32 DEFAULT 0,
    lb_23 Int32 DEFAULT 0,
    lb_24 Int32 DEFAULT 0,
    lb_25 Int32 DEFAULT 0,
    lb_26 Int32 DEFAULT 0,
    lb_27 Int32 DEFAULT 0,
    lb_28 Int32 DEFAULT 0,
    lb_29 Int32 DEFAULT 0,
    lb_30 Int32 DEFAULT 0,
    lb_31 Int32 DEFAULT 0,
    lb_32 Int32 DEFAULT 0,
    lb_33 Int32 DEFAULT 0,
    lb_34 Int32 DEFAULT 0,
    lb_35 Int32 DEFAULT 0,
    lb_36 Int32 DEFAULT 0,
    lb_37 Int32 DEFAULT 0,
    lb_38 Int32 DEFAULT 0,
    lb_39 Int32 DEFAULT 0,
    rolled_at        DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree(rolled_at)
PARTITION BY toYYYYMMDD(fromUnixTimestamp64Nano(bucket_unix_nano))
ORDER BY (bucket_unix_nano, resource_id, root_name, inbound);

CREATE TABLE IF NOT EXISTS trace_rollup_hour
(
    bucket_unix_nano Int64,
    resource_id      Int64,
    root_name        String,
    inbound          UInt8,
    trace_count      Int32 DEFAULT 0,
    error_count      Int32 DEFAULT 0,
    duration_sum_ms  Float64 DEFAULT 0,
    duration_max_ms  Float64 DEFAULT 0,
    lb_00 Int32 DEFAULT 0,
    lb_01 Int32 DEFAULT 0,
    lb_02 Int32 DEFAULT 0,
    lb_03 Int32 DEFAULT 0,
    lb_04 Int32 DEFAULT 0,
    lb_05 Int32 DEFAULT 0,
    lb_06 Int32 DEFAULT 0,
    lb_07 Int32 DEFAULT 0,
    lb_08 Int32 DEFAULT 0,
    lb_09 Int32 DEFAULT 0,
    lb_10 Int32 DEFAULT 0,
    lb_11 Int32 DEFAULT 0,
    lb_12 Int32 DEFAULT 0,
    lb_13 Int32 DEFAULT 0,
    lb_14 Int32 DEFAULT 0,
    lb_15 Int32 DEFAULT 0,
    lb_16 Int32 DEFAULT 0,
    lb_17 Int32 DEFAULT 0,
    lb_18 Int32 DEFAULT 0,
    lb_19 Int32 DEFAULT 0,
    lb_20 Int32 DEFAULT 0,
    lb_21 Int32 DEFAULT 0,
    lb_22 Int32 DEFAULT 0,
    lb_23 Int32 DEFAULT 0,
    lb_24 Int32 DEFAULT 0,
    lb_25 Int32 DEFAULT 0,
    lb_26 Int32 DEFAULT 0,
    lb_27 Int32 DEFAULT 0,
    lb_28 Int32 DEFAULT 0,
    lb_29 Int32 DEFAULT 0,
    lb_30 Int32 DEFAULT 0,
    lb_31 Int32 DEFAULT 0,
    lb_32 Int32 DEFAULT 0,
    lb_33 Int32 DEFAULT 0,
    lb_34 Int32 DEFAULT 0,
    lb_35 Int32 DEFAULT 0,
    lb_36 Int32 DEFAULT 0,
    lb_37 Int32 DEFAULT 0,
    lb_38 Int32 DEFAULT 0,
    lb_39 Int32 DEFAULT 0,
    rolled_at        DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree(rolled_at)
ORDER BY (bucket_unix_nano, resource_id, root_name, inbound);

INSERT INTO rollup_state (signal_name, granularity)
SELECT 'traces', 'minute'
WHERE NOT EXISTS (SELECT 1 FROM rollup_state WHERE signal_name = 'traces' AND granularity = 'minute');

INSERT INTO rollup_state (signal_name, granularity)
SELECT 'traces', 'hour'
WHERE NOT EXISTS (SELECT 1 FROM rollup_state WHERE signal_name = 'traces' AND granularity = 'hour');

INSERT INTO schema_version (version) VALUES ('2.13.1');
