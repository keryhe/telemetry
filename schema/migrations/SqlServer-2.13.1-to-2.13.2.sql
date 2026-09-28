-- Migration: schema 2.13.1 -> 2.13.2 (SQL Server)
--
-- Usage:
--   sqlcmd -d telemetry -i SqlServer-2.13.1-to-2.13.2.sql
--
-- Every step is idempotent, so re-running this on a database already at 2.13.2 does nothing but
-- refresh the schema_version timestamp.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 5 section, decision 27):
--
--   metric_last_seen: a new table (NOT a column on metrics) that the metrics catalog reads for
--   its "has data in range" check instead of scanning the five data-point tables. No FK to
--   metrics -- see the table's own comment in SqlServer-Schema.sql. Backfilled below from
--   MAX(time_unix_nano) per metric_id across the five data-point tables, in batches of 4,000
--   metric ids at a time (the same threshold MetricTouchWorker's own batches stay under, and
--   below SQL Server's lock-escalation point) so the migration never holds one enormous scan/
--   lock per table. On a large installation this backfill is the slow part of the upgrade --
--   expect it to run for minutes to hours depending on data-point table size; each table is
--   scanned once per metric-id batch via the existing (metric_id, time_unix_nano DESC) index.
--   The batch loop is safe to interrupt and re-run: every MERGE keeps the greater value.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'metric_last_seen')
BEGIN
    CREATE TABLE metric_last_seen (
        metric_id           BIGINT NOT NULL PRIMARY KEY,
        last_seen_unix_nano BIGINT NOT NULL
    );
    CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);
END
GO

-- =============================================================================
-- 2.13.2 -- backfill metric_last_seen from the five data-point tables, batched by metric_id
-- =============================================================================

DECLARE @batchSize BIGINT = 4000;
DECLARE @minId BIGINT, @maxId BIGINT, @lo BIGINT, @hi BIGINT;

SELECT @minId = MIN(id), @maxId = MAX(id) FROM metrics;

IF @minId IS NOT NULL
BEGIN
    SET @lo = @minId;
    WHILE @lo <= @maxId
    BEGIN
        SET @hi = @lo + @batchSize - 1;

        MERGE metric_last_seen WITH (HOLDLOCK) AS t
        USING (
            SELECT metric_id, MAX(time_unix_nano) AS last_seen_unix_nano
            FROM gauge_data_points WHERE metric_id BETWEEN @lo AND @hi GROUP BY metric_id
        ) AS s ON t.metric_id = s.metric_id
        WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
        WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);

        MERGE metric_last_seen WITH (HOLDLOCK) AS t
        USING (
            SELECT metric_id, MAX(time_unix_nano) AS last_seen_unix_nano
            FROM sum_data_points WHERE metric_id BETWEEN @lo AND @hi GROUP BY metric_id
        ) AS s ON t.metric_id = s.metric_id
        WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
        WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);

        MERGE metric_last_seen WITH (HOLDLOCK) AS t
        USING (
            SELECT metric_id, MAX(time_unix_nano) AS last_seen_unix_nano
            FROM histogram_data_points WHERE metric_id BETWEEN @lo AND @hi GROUP BY metric_id
        ) AS s ON t.metric_id = s.metric_id
        WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
        WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);

        MERGE metric_last_seen WITH (HOLDLOCK) AS t
        USING (
            SELECT metric_id, MAX(time_unix_nano) AS last_seen_unix_nano
            FROM exponential_histogram_data_points WHERE metric_id BETWEEN @lo AND @hi GROUP BY metric_id
        ) AS s ON t.metric_id = s.metric_id
        WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
        WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);

        MERGE metric_last_seen WITH (HOLDLOCK) AS t
        USING (
            SELECT metric_id, MAX(time_unix_nano) AS last_seen_unix_nano
            FROM summary_data_points WHERE metric_id BETWEEN @lo AND @hi GROUP BY metric_id
        ) AS s ON t.metric_id = s.metric_id
        WHEN MATCHED THEN UPDATE SET last_seen_unix_nano = IIF(t.last_seen_unix_nano > s.last_seen_unix_nano, t.last_seen_unix_nano, s.last_seen_unix_nano)
        WHEN NOT MATCHED THEN INSERT (metric_id, last_seen_unix_nano) VALUES (s.metric_id, s.last_seen_unix_nano);

        SET @lo = @hi + 1;
    END
END
GO

-- =============================================================================
-- Record the new version (matches what the full schema script writes)
-- =============================================================================

MERGE schema_version AS target
USING (VALUES (N'2.13.2')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO
