-- Consolidated migration: brings a ClickHouse `telemetry` database from ANY existing schema
-- version up to 2.13.3 (current).
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-Migrate.sql
--   (or:  schema/apply-schema.sh clickhouse telemetry   -- fresh installs only, see that script)
--
-- Replaces the five previously-hand-sequenced files this repo used to ship:
--   ClickHouse-2.11.0-to-2.12.0.sql, ClickHouse-2.12.0-to-2.13.0.sql,
--   ClickHouse-2.13.0-to-2.13.1.sql, ClickHouse-2.13.1-to-2.13.2.sql,
--   ClickHouse-2.13.2-to-2.13.3.sql
-- Those five are deleted; this one file supersedes them and is safe to run against a database
-- currently at ANY of 2.11.0, 2.12.0, 2.13.0, 2.13.1, 2.13.2, 2.13.3 (already current -- clean
-- no-op), or with no `schema_version` row/table at all.
--
-- Scope note: like the five files it replaces, this script only carries the INCREMENTAL DDL
-- introduced from 2.11.0 onward (rollups, metric_last_seen, service_name, skip indexes, plus the
-- view fix documented below) -- it assumes the pre-2.11.0 base schema (tenants, api_keys,
-- resources, instrumentation_scopes, spans, metrics, the five data-point tables, log_records,
-- alert_rules, alert_events, retention_settings, and the trace_summary/log_severity_stats views)
-- is already present, as it would be on any database that was ever actually running this
-- application. A genuinely empty database (no tables at all) should instead use
-- `ClickHouse-Schema.sql` directly (`schema/apply-schema.sh clickhouse` -- fresh installs only),
-- which creates that base schema plus everything this script adds, all in one already-current
-- pass.
--
-- Why one big idempotent script works for ClickHouse specifically (unlike the relational
-- providers' version-gated migrations): ClickHouse DDL has no multi-statement transactions
-- anyway, so every one of those five files was already written as a sequence of independently
-- idempotent statements (IF NOT EXISTS / IF EXISTS). Concatenating them in dependency order
-- (tables before the materialized views that read them, etc.) and re-running each statement's
-- own idempotency check is sufficient -- there is no real conditional control flow to lean on,
-- and none is needed. The one exception is the two backfills (metric_last_seen, and
-- resources.service_name), which need their own re-run safety; see the notes at each below.
--
-- schema_version rows for every version this script advances through are inserted along the way
-- (ReplacingMergeTree on `version`, so re-inserting an existing version is a harmless duplicate
-- that collapses at merge time / is deduped by `LIMIT 1 BY version` on read). This lets a
-- database that stops partway through an upgrade (e.g. crash) resume correctly on next run: every
-- statement below is independently idempotent, so it simply continues from wherever it left off.

-- =============================================================================
-- SCHEMA VERSION TABLE (created first: later steps gate backfills on rows in it, and a
-- database old enough to predate this table needs it created before anything else runs)
-- =============================================================================
CREATE TABLE IF NOT EXISTS schema_version
(
    version    String,
    applied_at DateTime64(9) DEFAULT now64(9)
)
ENGINE = ReplacingMergeTree
ORDER BY version;

-- =============================================================================
-- 2.11.0 -> 2.12.0
-- =============================================================================
-- No-op bump for ClickHouse alone, like 2.8.0 before it: the other four providers add
-- idx_spans_error (a status_code-filtered index on start_time_unix_nano) for mode=errors trace
-- queries, but the spans table's daily partition plus its own ORDER BY (trace_id, span_id)
-- already lets a status_code='ERROR' scan skip whole partitions/granules outside the query's
-- time bounds without a dedicated index -- see the comment on that table in ClickHouse-Schema.sql.
-- No DDL to run for this step; only the version marker below.

-- =============================================================================
-- 2.12.0 -> 2.13.0
-- =============================================================================
-- rollup_state/log_rollup_minute/log_rollup_hour are added, backing the new RollupWorker
-- (list-pages-server-side plan decisions 37-38). log_records' own ORDER BY
-- (time_unix_nano, resource_id) needs no change -- see the comment on that table in
-- ClickHouse-Schema.sql for why the keyset tiebreak index the other four providers add is
-- unnecessary here.

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

-- =============================================================================
-- 2.13.0 -> 2.13.1
-- =============================================================================
-- orphan_roots/trace_rollup_minute/trace_rollup_hour are added, backing the trace half of the
-- RollupWorker (decisions 37-38, 41). No idx_spans_root_time equivalent and no change to spans'
-- own ORDER BY -- see the comment on the spans table in ClickHouse-Schema.sql for why (spans
-- already carries created_at, unlike the other four providers before this phase).

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

-- =============================================================================
-- 2.13.1 -> 2.13.2
-- =============================================================================
-- metric_last_seen: an AggregatingMergeTree holding a partial maxState(time_unix_nano) per
-- metric_id, fed going forward by one materialized view per data-point table (created below).
-- Backfilled once via INSERT ... SELECT ... maxState(...) over each existing data-point table --
-- cheap and single-pass on ClickHouse (a columnar aggregate over an already time-partitioned
-- MergeTree, no locks, no mutations), unlike the batched/chunked backfill the relational
-- providers' migrations need to avoid a long-held scan/lock.

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

-- One-time backfill from data already in the five data-point tables. The materialized views
-- above only fire on INSERTs from here forward, so existing rows need an explicit backfill pass
-- -- same reasoning as every other Phase 5 provider's migration.
--
-- Re-run safety (this is the piece the original per-step migration files did not need to worry
-- about, since each ran exactly once by hand in sequence -- this consolidated script must
-- tolerate being run an arbitrary number of times, including after more data has been ingested
-- since the last run): metric_last_seen is an AggregatingMergeTree, so re-inserting the same
-- maxState(time_unix_nano) partial for a metric_id is not a duplicate-count problem the way a
-- plain sum would be -- maxMerge() over any number of identical (or differing) partial states for
-- the same metric_id still converges to the correct overall maximum, because max is idempotent
-- and commutative. Concretely: if this backfill runs again after new data-point rows have been
-- ingested (which also feeds the materialized views above), the SELECT below simply recomputes
-- maxState(time_unix_nano) over the table's *current* contents (old + new rows) and inserts that
-- partial state alongside whatever the materialized views already contributed; maxMerge() on read
-- takes the max across all of it and is unaffected by how many times, or over what overlapping
-- windows, the partials were produced. So this INSERT is gated only on not having a version
-- recorded yet strictly for defensiveness/clarity, not because a bare re-run would corrupt
-- anything -- guard kept anyway to avoid writing redundant parts on every migration run once the
-- database is already current.
INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM gauge_data_points
WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2')
GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM sum_data_points
WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2')
GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM histogram_data_points
WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2')
GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM exponential_histogram_data_points
WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2')
GROUP BY metric_id;

INSERT INTO metric_last_seen (metric_id, last_seen_state)
SELECT metric_id, maxState(time_unix_nano) FROM summary_data_points
WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2')
GROUP BY metric_id;

-- =============================================================================
-- 2.13.2 -> 2.13.3
-- =============================================================================
-- resources.service_name: a real column, backfilled below from attributes_json's "service.name"
-- key via JSONExtractString. ResourceServiceNameExpr() is now just "{alias}.service_name" on
-- every provider. Because resources is a ReplacingMergeTree, the backfill UPDATE is a mutation
-- (ALTER TABLE ... UPDATE), matching this provider's existing "control-plane is best-effort"
-- mutation-based pattern (CLAUDE.md).
--
-- ngrambf_v1 skip indexes on spans.name / spans.status_message / log_records.body_value, and
-- tokenbf_v1 skip indexes on spans.attributes_json / log_records.attributes_json, added via
-- ALTER TABLE ... ADD INDEX + MATERIALIZE INDEX so they apply to existing parts, not just new
-- ones. The predicates themselves (FreeTextPredicate / AttributePredicate) are unchanged -- these
-- only let ClickHouse skip granules that provably cannot match before evaluating the real
-- predicate.

ALTER TABLE resources ADD COLUMN IF NOT EXISTS service_name Nullable(String);

-- The WHERE clause (service_name IS NULL AND attributes_json IS NOT NULL) already makes this
-- mutation self-limiting on a re-run: rows already backfilled are skipped, and a resource that
-- later arrives with no attributes_json stays NULL rather than being reprocessed forever. Safe to
-- run again after new resources have been ingested since the last run -- it only ever touches
-- rows that still need it.
ALTER TABLE resources UPDATE service_name = JSONExtractString(assumeNotNull(attributes_json), 'service.name')
WHERE service_name IS NULL AND attributes_json IS NOT NULL;

ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_name_ngram name TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_status_message_ngram assumeNotNull(status_message) TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_attributes_tokenbf assumeNotNull(attributes_json) TYPE tokenbf_v1(8192, 3, 0) GRANULARITY 4;
ALTER TABLE spans MATERIALIZE INDEX IF EXISTS idx_spans_name_ngram;
ALTER TABLE spans MATERIALIZE INDEX IF EXISTS idx_spans_status_message_ngram;
ALTER TABLE spans MATERIALIZE INDEX IF EXISTS idx_spans_attributes_tokenbf;

ALTER TABLE log_records ADD INDEX IF NOT EXISTS idx_log_body_ngram assumeNotNull(body_value) TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE log_records ADD INDEX IF NOT EXISTS idx_log_attributes_tokenbf assumeNotNull(attributes_json) TYPE tokenbf_v1(8192, 3, 0) GRANULARITY 4;
ALTER TABLE log_records MATERIALIZE INDEX IF EXISTS idx_log_body_ngram;
ALTER TABLE log_records MATERIALIZE INDEX IF EXISTS idx_log_attributes_tokenbf;

-- DEVIATION from the original ClickHouse-2.13.2-to-2.13.3.sql (documented per the consolidation
-- brief): that file added resources.service_name but never updated the service_map/
-- service_map_detailed views to read it -- they were left doing
-- JSONExtractString(assumeNotNull(...attributes_json), 'service.name') from schema 2.11.0. A
-- database migrated through the original five files by hand therefore ends up with views whose
-- parent_service/child_service columns are plain String (JSONExtractString's return type),
-- while a fresh ClickHouse-Schema.sql install's views (which reference
-- parent_res.service_name/child_res.service_name directly) return Nullable(String) --
-- discovered here by diffing system.columns between a migrated and a fresh-install database,
-- and confirmed to have been latent since 2.13.3 shipped (neither original migration file
-- touches these views). CREATE VIEW IF NOT EXISTS is a no-op against an existing view, so it
-- cannot fix this on databases migrated by the old chain; CREATE OR REPLACE VIEW is used instead,
-- unconditionally bringing both views to the current definition regardless of starting version
-- (harmless on a database that already has the current definition, e.g. one that started at
-- 2.13.3 or a fresh install run through this script).
CREATE OR REPLACE VIEW service_map AS
SELECT
    parent_res.service_name AS parent_service,
    child_res.service_name AS child_service,
    child.kind                                                                   AS span_kind,
    count()                                                                      AS call_count
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id AND child.trace_id = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE parent_service != '' AND child_service != '' AND parent_service != child_service
GROUP BY parent_service, child_service, child.kind;

CREATE OR REPLACE VIEW service_map_detailed AS
SELECT
    parent_res.service_name AS parent_service,
    child_res.service_name AS child_service,
    child.kind                                                                   AS span_kind,
    count()                                                                      AS call_count,
    avg(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000         AS avg_duration_ms,
    min(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000         AS min_duration_ms,
    max(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000         AS max_duration_ms,
    countIf(child.status_code = 'ERROR')                                         AS error_count,
    (countIf(child.status_code = 'ERROR') / count()) * 100                       AS error_rate
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id AND child.trace_id = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE parent_service != '' AND child_service != '' AND parent_service != child_service
GROUP BY parent_service, child_service, child.kind;

-- =============================================================================
-- SCHEMA VERSION MARKERS
-- =============================================================================
-- Record every version this script brings the database through. ReplacingMergeTree on `version`
-- collapses re-inserted rows at merge time (and a plain SELECT without FINAL/LIMIT 1 BY version
-- would show at most a harmless duplicate row per version in the interim) -- consistent with
-- every other insert in this file being safe to repeat.
--
-- Gated as a block on "not already at 2.13.3", rather than each left unconditional, so a
-- database that is already fully current (verification scenario: ClickHouse-Schema.sql applied
-- fresh, or this script already run to completion) is a true no-op here -- no new parts written,
-- nothing for a later `OPTIMIZE ... FINAL` to collapse. A database mid-upgrade (e.g. starting at
-- 2.13.1) still gets every marker it's missing; re-inserting a marker it already has (2.12.0,
-- 2.13.0, 2.13.1 in that example) is harmless for the reason above, so one shared condition is
-- sufficient -- no need to gate each version individually on its predecessor.
INSERT INTO schema_version (version)
SELECT '2.12.0' WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3');
INSERT INTO schema_version (version)
SELECT '2.13.0' WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3');
INSERT INTO schema_version (version)
SELECT '2.13.1' WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3');
INSERT INTO schema_version (version)
SELECT '2.13.2' WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3');
INSERT INTO schema_version (version)
SELECT '2.13.3' WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3');
