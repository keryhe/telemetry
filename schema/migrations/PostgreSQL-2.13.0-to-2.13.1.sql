-- Migration: schema 2.13.0 -> 2.13.1 (PostgreSQL, plain)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f PostgreSQL-2.13.0-to-2.13.1.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.1 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.13.1 or it is left exactly as it
-- was.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 3 section):
--
--   1. idx_spans_root_time: a partial index anchoring the trace page/summary on root spans
--      (INCLUDE end_time_unix_nano so mode=slow's duration check runs inside the index).
--   2. orphan_roots: traces whose root span never arrived (decision 41), written by the rollup
--      worker.
--   3. trace_rollup_minute/trace_rollup_hour: per-minute/hour trace summary tables (decisions
--      37-38), plus their rollup_state seed rows.

BEGIN;

-- =============================================================================
-- 2.13.1 -- root-span index
-- =============================================================================

CREATE INDEX IF NOT EXISTS idx_spans_root_time ON spans ("start_time_unix_nano" DESC) INCLUDE ("end_time_unix_nano")
    WHERE "parent_span_id" IS NULL;

-- =============================================================================
-- 2.13.1 -- orphan_roots
-- =============================================================================

CREATE TABLE IF NOT EXISTS orphan_roots (
    "trace_id"             CHAR(32)     NOT NULL PRIMARY KEY,
    "span_id"              CHAR(16)     NOT NULL,
    "resource_id"          BIGINT       NOT NULL,
    "start_time_unix_nano" BIGINT       NOT NULL,
    "end_time_unix_nano"   BIGINT       NOT NULL,
    "detected_at"          TIMESTAMPTZ  NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS idx_orphan_roots_start ON orphan_roots ("start_time_unix_nano" DESC, "trace_id");

-- =============================================================================
-- 2.13.1 -- trace rollup tables
-- =============================================================================

CREATE TABLE IF NOT EXISTS trace_rollup_minute (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0, "lb_01" INTEGER NOT NULL DEFAULT 0, "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0, "lb_04" INTEGER NOT NULL DEFAULT 0, "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0, "lb_07" INTEGER NOT NULL DEFAULT 0, "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0, "lb_10" INTEGER NOT NULL DEFAULT 0, "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0, "lb_13" INTEGER NOT NULL DEFAULT 0, "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0, "lb_16" INTEGER NOT NULL DEFAULT 0, "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0, "lb_19" INTEGER NOT NULL DEFAULT 0, "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0, "lb_22" INTEGER NOT NULL DEFAULT 0, "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0, "lb_25" INTEGER NOT NULL DEFAULT 0, "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0, "lb_28" INTEGER NOT NULL DEFAULT 0, "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0, "lb_31" INTEGER NOT NULL DEFAULT 0, "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0, "lb_34" INTEGER NOT NULL DEFAULT 0, "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0, "lb_37" INTEGER NOT NULL DEFAULT 0, "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX IF NOT EXISTS idx_trace_rollup_minute_bucket ON trace_rollup_minute ("bucket_unix_nano");

CREATE TABLE IF NOT EXISTS trace_rollup_hour (
    "bucket_unix_nano" BIGINT        NOT NULL,
    "resource_id"      BIGINT        NOT NULL,
    "root_name"        VARCHAR(255)  NOT NULL,
    "inbound"          SMALLINT      NOT NULL,
    "trace_count"      INTEGER       NOT NULL DEFAULT 0,
    "error_count"      INTEGER       NOT NULL DEFAULT 0,
    "duration_sum_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "duration_max_ms"  DOUBLE PRECISION NOT NULL DEFAULT 0,
    "lb_00" INTEGER NOT NULL DEFAULT 0, "lb_01" INTEGER NOT NULL DEFAULT 0, "lb_02" INTEGER NOT NULL DEFAULT 0,
    "lb_03" INTEGER NOT NULL DEFAULT 0, "lb_04" INTEGER NOT NULL DEFAULT 0, "lb_05" INTEGER NOT NULL DEFAULT 0,
    "lb_06" INTEGER NOT NULL DEFAULT 0, "lb_07" INTEGER NOT NULL DEFAULT 0, "lb_08" INTEGER NOT NULL DEFAULT 0,
    "lb_09" INTEGER NOT NULL DEFAULT 0, "lb_10" INTEGER NOT NULL DEFAULT 0, "lb_11" INTEGER NOT NULL DEFAULT 0,
    "lb_12" INTEGER NOT NULL DEFAULT 0, "lb_13" INTEGER NOT NULL DEFAULT 0, "lb_14" INTEGER NOT NULL DEFAULT 0,
    "lb_15" INTEGER NOT NULL DEFAULT 0, "lb_16" INTEGER NOT NULL DEFAULT 0, "lb_17" INTEGER NOT NULL DEFAULT 0,
    "lb_18" INTEGER NOT NULL DEFAULT 0, "lb_19" INTEGER NOT NULL DEFAULT 0, "lb_20" INTEGER NOT NULL DEFAULT 0,
    "lb_21" INTEGER NOT NULL DEFAULT 0, "lb_22" INTEGER NOT NULL DEFAULT 0, "lb_23" INTEGER NOT NULL DEFAULT 0,
    "lb_24" INTEGER NOT NULL DEFAULT 0, "lb_25" INTEGER NOT NULL DEFAULT 0, "lb_26" INTEGER NOT NULL DEFAULT 0,
    "lb_27" INTEGER NOT NULL DEFAULT 0, "lb_28" INTEGER NOT NULL DEFAULT 0, "lb_29" INTEGER NOT NULL DEFAULT 0,
    "lb_30" INTEGER NOT NULL DEFAULT 0, "lb_31" INTEGER NOT NULL DEFAULT 0, "lb_32" INTEGER NOT NULL DEFAULT 0,
    "lb_33" INTEGER NOT NULL DEFAULT 0, "lb_34" INTEGER NOT NULL DEFAULT 0, "lb_35" INTEGER NOT NULL DEFAULT 0,
    "lb_36" INTEGER NOT NULL DEFAULT 0, "lb_37" INTEGER NOT NULL DEFAULT 0, "lb_38" INTEGER NOT NULL DEFAULT 0,
    "lb_39" INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY ("bucket_unix_nano", "resource_id", "root_name", "inbound")
);
CREATE INDEX IF NOT EXISTS idx_trace_rollup_hour_bucket ON trace_rollup_hour ("bucket_unix_nano");

INSERT INTO rollup_state ("signal_name", "granularity") VALUES ('traces', 'minute'), ('traces', 'hour')
ON CONFLICT ("signal_name", "granularity") DO NOTHING;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.13.1')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
