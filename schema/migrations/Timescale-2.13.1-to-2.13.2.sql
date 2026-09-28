-- Migration: schema 2.13.1 -> 2.13.2 (Timescale)
--
-- Usage:
--   psql -d telemetry -v ON_ERROR_STOP=1 -f Timescale-2.13.1-to-2.13.2.sql
--
-- Every step is idempotent and guarded, so re-running this on a database already at 2.13.2 does
-- nothing but refresh the schema_version timestamp.
--
-- It runs in ONE transaction: either the database ends up at 2.13.2 or it is left exactly as it
-- was.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 5 section, decision 27):
--
--   metric_last_seen: a new table (NOT a column on metrics) that the metrics catalog reads for
--   its "has data in range" check instead of scanning the five data-point tables (three of which
--   are hypertables on this provider). No FK to metrics -- see the table's own comment in
--   Timescale-Schema.sql. Deliberately a plain table, not a hypertable: it is keyed/updated by
--   metric_id, not appended by time. Backfilled below from
--   MAX(time_unix_nano) per metric_id across the five data-point tables, in batches of 5,000
--   metric ids at a time so the migration does not hold one enormous scan/lock per table. On a
--   large installation (millions of metric catalog rows, each with a deep history in one or more
--   data-point tables) this backfill is the slow part of the upgrade -- expect it to run for
--   minutes to hours depending on data-point table size and available index cache; each table is
--   scanned once per metric-id batch via the existing (metric_id, time_unix_nano DESC) index, so
--   cost scales with total row count, not with how long ago the migration is run. The batch loop
--   is safe to interrupt and re-run: every INSERT is an idempotent upsert keeping the greater
--   value, matching MetricTouchWorker's own conflict resolution.

BEGIN;

CREATE TABLE IF NOT EXISTS metric_last_seen (
    "metric_id"            BIGINT NOT NULL PRIMARY KEY,
    "last_seen_unix_nano"  BIGINT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_metric_last_seen_last_seen ON metric_last_seen ("last_seen_unix_nano");

-- =============================================================================
-- 2.13.2 -- backfill metric_last_seen from the five data-point tables, batched by metric_id
-- =============================================================================

DO $$
DECLARE
    batch_size CONSTANT BIGINT := 5000;
    min_id BIGINT;
    max_id BIGINT;
    lo BIGINT;
    hi BIGINT;
BEGIN
    SELECT MIN("id"), MAX("id") INTO min_id, max_id FROM metrics;
    IF min_id IS NULL THEN
        RETURN;
    END IF;

    lo := min_id;
    WHILE lo <= max_id LOOP
        hi := lo + batch_size - 1;

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM gauge_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM sum_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM histogram_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM exponential_histogram_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        INSERT INTO metric_last_seen ("metric_id", "last_seen_unix_nano")
        SELECT "metric_id", MAX("time_unix_nano") FROM summary_data_points
        WHERE "metric_id" BETWEEN lo AND hi GROUP BY "metric_id"
        ON CONFLICT ("metric_id") DO UPDATE
        SET "last_seen_unix_nano" = GREATEST(metric_last_seen."last_seen_unix_nano", EXCLUDED."last_seen_unix_nano");

        lo := hi + 1;
    END LOOP;
END $$;

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

INSERT INTO schema_version ("version") VALUES ('2.13.2')
ON CONFLICT ("version") DO UPDATE SET "applied_at" = NOW();

COMMIT;
