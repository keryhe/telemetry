-- Consolidated migration: brings a SQL Server telemetry database from ANY existing schema
-- version up to 2.13.3 (current), in one file.
--
-- Usage:
--   sqlcmd -d telemetry -b -I -i SqlServer-Migrate.sql
--
-- -b matters: it makes sqlcmd stop on the first error instead of running the remaining batches
-- against a rolled-back transaction. Requires SQL Server 2016 or later (DROP ... IF EXISTS,
-- STRING_SPLIT-free FOR JSON PATH usage already present in the schema).
--
-- This file REPLACES the former chain of seven hand-run scripts:
--   SqlServer-2.6.0-to-2.10.0.sql, SqlServer-2.10.0-to-2.11.0.sql, SqlServer-2.11.0-to-2.12.0.sql,
--   SqlServer-2.12.0-to-2.13.0.sql, SqlServer-2.13.0-to-2.13.1.sql, SqlServer-2.13.1-to-2.13.2.sql,
--   SqlServer-2.13.2-to-2.13.3.sql
-- Every step from all seven is folded in below, in the same order, EVERY step individually
-- guarded so it is a no-op if already applied. That means this single script is correct to run
-- against a database at ANY of: 2.6.0, 2.7.0, 2.8.0, 2.9.0, 2.10.0, 2.11.0, 2.12.0, 2.13.0,
-- 2.13.1, 2.13.2, or already 2.13.3 (clean no-op) -- and safe to re-run any number of times,
-- including back-to-back with no intervening changes.
--
-- DESIGN NOTES (read before touching this file):
--
--   1. Guards are almost entirely STRUCTURAL (sys.columns / sys.indexes / sys.key_constraints /
--      OBJECT_ID checks, or a data predicate like "WHERE service_name IS NULL"), NOT keyed off
--      schema_version. This is deliberate: schema_version is the OUTPUT of this script, not an
--      input gate for anything except the two large backfills noted below, so the script gives
--      the right answer even against a database whose schema_version table is empty or missing
--      rows for versions it has structurally already passed through (e.g. a hand-patched
--      database, or one migrated by some means other than these files).
--
--   2. Two backfills ARE gated on schema_version, per the task brief's explicit guidance, purely
--      as a performance optimization (skip an expensive scan once we know it already ran):
--        - 2.13.2's metric_last_seen backfill (batched scan of five data-point tables)
--        - 2.13.3's resources.service_name backfill (JSON_VALUE extraction over all resources)
--      Both underlying operations are ALSO naturally idempotent on their own (the metric_last_seen
--      MERGE keeps the greater timestamp; the service_name UPDATE only touches NULL rows), so this
--      gate is belt-and-suspenders, not load-bearing for correctness -- only for avoiding a
--      pointless full scan on every re-run once a database is caught up.
--
--   3. TRANSACTION BOUNDARIES are preserved EXACTLY as they were in the seven original files,
--      rather than wrapped in one giant transaction spanning the whole script. This is
--      deliberate and matters most for the two 2.13.x backfills:
--        - The original SqlServer-2.13.1-to-2.13.2.sql (metric_last_seen backfill) and
--          SqlServer-2.13.2-to-2.13.3.sql (service_name backfill) run with NO explicit
--          transaction wrapping them, on purpose -- the batched backfill loop is designed to be
--          safe to interrupt and resume (each MERGE auto-commits individually), and wrapping it
--          in one big transaction would hold locks and grow the log for the loop's entire
--          duration on a large installation, defeating that design.
--        - Sections A-E (2.6.0 through 2.13.1) keep their original per-file
--          BEGIN TRANSACTION / COMMIT TRANSACTION scopes, so a failure partway through one
--          version step still rolls back cleanly to the last completed version step, exactly as
--          running the original files by hand in sequence would.
--      A consequence: this script is NOT strictly all-or-nothing end to end. It is all-or-nothing
--      PER ORIGINAL FILE BOUNDARY, same as before. This is a deliberate continuation of the
--      original design, not a new deviation.
--
--   4. CREATE VIEW: none of the seven original migration files touch trace_summary, service_map,
--      service_map_detailed, or log_severity_stats. Views only appear in the fresh-install
--      SqlServer-Schema.sql. So this consolidated script needs no CREATE VIEW / CREATE OR ALTER
--      VIEW batch at all -- there is nothing to reconcile here. Documented explicitly because the
--      task brief called out the "CREATE VIEW must be alone in its batch" T-SQL constraint as
--      something to watch for; it turned out not to apply to this migration chain.
--
--   5. DEVIATION from the original 2.6.0-to-2.10.0 file: the metrics dedup temp-table logic
--      (which collapses pre-2.7.0 duplicate (resource_id, name, type, scope_id) rows before
--      uk_metric_identity can be added) is now wrapped in the SAME
--      IF NOT EXISTS (... uk_metric_identity ...) guard as the constraint it enables, instead of
--      running unconditionally on every execution. Original behavior was already correct (the
--      dedup finds zero rows to move once the constraint exists, since duplicates can no longer
--      exist), but it did a full scan of metrics every single run. Gating it behind the same
--      existence check that already governs the constraint removes that scan once the database is
--      caught up. This is a performance-only deviation; the resulting end state is identical.
--
-- What each version step changed, and why (full rationale preserved from the originals; see also
-- CLAUDE.md / plans/telemetry-retention.md / plans/span-events-links-json-hypertable.md /
-- plans/list-pages-server-side.md):
--
--   2.7.0  metrics gained uk_metric_identity UNIQUE (resource_id, name, type, scope_id), so the
--          catalog holds one row per metric identity instead of one row per OTLP export cycle.
--          Existing databases therefore carry duplicates that MUST be collapsed before the
--          constraint can be created. idx_resource_name became an exact left prefix of the new
--          constraint and is dropped. The four data-point tables that lacked one gained a
--          standalone time index, because metric retention deletes from them directly on
--          time_unix_nano instead of cascading from metrics.
--   2.8.0  Four redundant B-tree indexes dropped from spans: idx_trace_id, idx_start_time,
--          idx_kind, idx_status.
--   2.9.0  Exemplars moved onto the data point that owns them. The single exemplar_id column
--          could hold only one exemplar where OTLP allows many, and no writer ever populated it,
--          so no data is lost by dropping it or the shared exemplars table.
--   2.10.0 Retention scheduling moved to a single application-level mechanism (Keryhe.Telemetry.
--          Api's RetentionWorker) driven by a new retention_settings table, on every provider.
--          SQL Server has no native retention-policy equivalent to remove, so this step is just
--          the new table.
--   2.11.0 span_events and span_links were never read or written independently of their parent
--          span, so both collapse into two new nullable columns on spans: events_json/links_json.
--          Existing rows are folded into their parent spans row (via FOR JSON PATH, matching
--          System.Text.Json's default PascalCase property names, since that is what the read path
--          deserializes) before the child tables are dropped, or that data is lost.
--   2.12.0 idx_spans_error added: a filtered index on spans(start_time_unix_nano DESC) WHERE
--          status_code = 'ERROR', backing mode=errors trace queries. Not a reversal of
--          idx_status's 2.8.0 removal -- that was a plain index over all three status values, too
--          low-cardinality for the planner to ever choose; this covers only the rare error rows.
--   2.13.0 ALLOW_SNAPSHOT_ISOLATION turned on (decision 35), needed by the API's read
--          repositories' SET TRANSACTION ISOLATION LEVEL SNAPSHOT connections. idx_log_time
--          replaced by idx_log_time_id (adds the id DESC keyset-paging tiebreak). rollup_state /
--          log_rollup_minute / log_rollup_hour added, backing RollupWorker (decisions 37-38),
--          seeded with the logs/minute and logs/hour rows this phase needs.
--   2.13.1 idx_spans_root_time added (anchors trace page/summary queries on root spans).
--          orphan_roots added (traces whose root span never arrived, decision 41).
--          trace_rollup_minute / trace_rollup_hour added (decisions 37-38), plus their
--          rollup_state seed rows (traces/minute, traces/hour).
--   2.13.2 metric_last_seen added (decision 27): the metrics catalog's "has data in range" check
--          reads this instead of scanning the five data-point tables. No FK to metrics. Backfilled
--          from MAX(time_unix_nano) per metric_id across the five data-point tables, batched by
--          metric_id (4,000 at a time, matching MetricTouchWorker's own batch size and staying
--          below SQL Server's lock-escalation threshold).
--   2.13.3 resources.service_name added as a real column (decisions 8/39), backfilled from
--          attributes_json's "service.name" key via JSON_VALUE. ResourceServiceNameExpr() is now
--          just "{alias}.service_name" on every provider, including this one.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

SET XACT_ABORT ON;
GO

-- =============================================================================================
-- SECTION A -- originally SqlServer-2.6.0-to-2.10.0.sql (covers 2.7.0, 2.8.0, 2.9.0, 2.10.0)
-- =============================================================================================

BEGIN TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- 2.7.0 -- collapse duplicate metrics rows, then add uk_metric_identity
-- ---------------------------------------------------------------------------
--
-- Survivor per identity = MIN(id), i.e. the earliest row, whose created_at is the earliest this
-- metric was seen. That is exactly what created_at means from 2.7.0 on ("first seen"), so the
-- surviving row already carries the right timestamp.
--
-- NOTE on collation: uk_metric_identity compares `name` under the database collation, which is
-- case-insensitive by default. This dedup deliberately matches that, so the rows it collapses are
-- exactly the rows the constraint would reject.
--
-- Wrapped in the same existence guard as the constraint itself (deviation from the original file,
-- documented in the header above): once uk_metric_identity exists, no duplicates can exist, so
-- there is nothing to gain from scanning metrics again on every re-run.
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'uk_metric_identity' AND parent_object_id = OBJECT_ID('metrics'))
BEGIN
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

    ALTER TABLE metrics ADD CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, [type], scope_id);
END
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

-- ---------------------------------------------------------------------------
-- 2.8.0 -- drop the redundant spans indexes
-- ---------------------------------------------------------------------------

DROP INDEX IF EXISTS idx_trace_id   ON spans;  -- left prefix of uk_trace_span (trace_id, span_id)
DROP INDEX IF EXISTS idx_start_time ON spans;  -- left prefix of idx_duration (start, end)
DROP INDEX IF EXISTS idx_kind       ON spans;  -- 6 distinct values; never chosen by the optimizer
DROP INDEX IF EXISTS idx_status     ON spans;  -- 3 distinct values; never chosen by the optimizer
-- No GIN equivalent on SQL Server, so the two attributes_json index drops have no counterpart here.
GO

-- ---------------------------------------------------------------------------
-- 2.9.0 -- exemplars move onto the data point rows
-- ---------------------------------------------------------------------------

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

-- ---------------------------------------------------------------------------
-- 2.10.0 -- retention_settings table (single global row)
-- ---------------------------------------------------------------------------

IF OBJECT_ID('retention_settings', 'U') IS NULL
BEGIN
    CREATE TABLE retention_settings (
        [id]                    SMALLINT     NOT NULL PRIMARY KEY DEFAULT 1,
        [trace_retention_days]   INT          NOT NULL,
        [log_retention_days]     INT          NOT NULL,
        [metric_retention_days]  INT          NOT NULL,
        [updated_at]             DATETIME2    NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT chk_retention_settings_singleton CHECK ([id] = 1)
    );
END
GO

-- Seeded with today's implicit defaults (traces 90d, logs 90d, metrics 180d). Guarded so
-- re-running this migration never fails or duplicates the row.
IF NOT EXISTS (SELECT 1 FROM retention_settings WHERE [id] = 1)
    INSERT INTO retention_settings ([id], [trace_retention_days], [log_retention_days], [metric_retention_days])
    VALUES (1, 90, 90, 180);
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS tgt
USING (VALUES (N'2.10.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO

-- =============================================================================================
-- SECTION B -- originally SqlServer-2.10.0-to-2.11.0.sql
-- =============================================================================================

BEGIN TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- 2.11.0 -- add events_json/links_json to spans
-- ---------------------------------------------------------------------------

IF COL_LENGTH('spans', 'events_json') IS NULL
    ALTER TABLE spans ADD events_json NVARCHAR(MAX);
IF COL_LENGTH('spans', 'links_json') IS NULL
    ALTER TABLE spans ADD links_json NVARCHAR(MAX);
GO

-- ---------------------------------------------------------------------------
-- 2.11.0 -- fold existing span_events/span_links rows into spans.events_json/links_json
-- ---------------------------------------------------------------------------
--
-- Guarded on OBJECT_ID so this whole block is a no-op on a database already migrated (or
-- created fresh at 2.11.0+, where span_events/span_links never existed). The JSON shape below
-- matches exactly what System.Text.Json produces for List<SpanEventModel>/List<SpanLinkModel>
-- with no naming policy configured -- i.e. the C# property names verbatim (PascalCase), since
-- that is what the read path (Keryhe.Telemetry.Core/Data/Read/TraceReadRepositoryBase.cs)
-- deserializes on the other end. FOR JSON PATH's default null-handling (omit properties whose
-- value is NULL) matches how a missing/absent JSON property deserializes back to a null model
-- property, so no INCLUDE_NULL_VALUES is needed.

IF OBJECT_ID('span_events', 'U') IS NOT NULL
BEGIN
    UPDATE s
    SET s.events_json = (
        SELECT
            e.[name]                   AS [Name],
            e.time_unix_nano           AS [TimeUnixNano],
            e.dropped_attributes_count AS [DroppedAttributesCount],
            JSON_QUERY(e.attributes_json) AS [Attributes]
        FROM span_events e
        WHERE e.span_id = s.id
        FOR JSON PATH
    )
    FROM spans s
    WHERE EXISTS (SELECT 1 FROM span_events e WHERE e.span_id = s.id);
END
GO

IF OBJECT_ID('span_links', 'U') IS NOT NULL
BEGIN
    UPDATE s
    SET s.links_json = (
        SELECT
            l.linked_trace_id          AS [LinkedTraceIdHex],
            l.linked_span_id           AS [LinkedSpanIdHex],
            l.trace_state              AS [TraceState],
            l.flags                    AS [Flags],
            l.dropped_attributes_count AS [DroppedAttributesCount],
            JSON_QUERY(l.attributes_json) AS [Attributes]
        FROM span_links l
        WHERE l.span_id = s.id
        FOR JSON PATH
    )
    FROM spans s
    WHERE EXISTS (SELECT 1 FROM span_links l WHERE l.span_id = s.id);
END
GO

-- ---------------------------------------------------------------------------
-- 2.11.0 -- drop span_events/span_links now that their data lives on
-- spans.events_json/links_json (this also drops fk_span_events_spans/fk_span_links_spans and
-- idx_span_time/idx_span_link, since they belong to the dropped tables)
-- ---------------------------------------------------------------------------

DROP TABLE IF EXISTS span_events;
DROP TABLE IF EXISTS span_links;
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS tgt
USING (VALUES (N'2.11.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO

-- =============================================================================================
-- SECTION C -- originally SqlServer-2.11.0-to-2.12.0.sql
-- =============================================================================================

BEGIN TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- 2.12.0 -- add idx_spans_error for mode=errors trace queries
-- ---------------------------------------------------------------------------
--
-- Not a reversal of idx_status's 2.8.0 removal -- that was a plain index over all three status
-- values, too low-cardinality for the planner to ever choose; this covers only the rare error
-- rows (ERROR is the minority status in practice).

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_spans_error' AND object_id = OBJECT_ID('spans'))
    CREATE INDEX idx_spans_error ON spans (start_time_unix_nano DESC) WHERE status_code = 'ERROR';
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS tgt
USING (VALUES (N'2.12.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO

-- =============================================================================================
-- SECTION D -- originally SqlServer-2.12.0-to-2.13.0.sql
-- =============================================================================================
--
-- 2.13.0 -- enable snapshot isolation (decision 35) -- OUTSIDE any transaction. SQL Server
-- refuses ALTER DATABASE inside a user transaction, and this MUST run before BEGIN TRANSACTION
-- below -- same constraint as the original file. By the time execution reaches here, Section A/B/C
-- above have already committed (or were no-ops), so there is no open transaction to conflict with,
-- regardless of which version this script started from. It waits for in-flight transactions to
-- finish but needs no single-user mode.

IF NOT EXISTS (
    SELECT 1 FROM sys.databases
    WHERE database_id = DB_ID()
      AND snapshot_isolation_state = 1
)
    ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;
GO

BEGIN TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- 2.13.0 -- keyset tiebreak index for log_records
-- ---------------------------------------------------------------------------
-- idx_log_time is a pure left prefix of idx_log_time_id below, the new keyset-paging tiebreak
-- index (same provably-redundant reasoning as the 2.8.0 spans index cleanup).

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_log_time' AND object_id = OBJECT_ID('log_records'))
    DROP INDEX idx_log_time ON log_records;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_log_time_id' AND object_id = OBJECT_ID('log_records'))
    CREATE INDEX idx_log_time_id ON log_records (time_unix_nano DESC, id DESC);
GO

-- ---------------------------------------------------------------------------
-- 2.13.0 -- rollup tables
-- ---------------------------------------------------------------------------

IF OBJECT_ID('rollup_state', 'U') IS NULL
BEGIN
    CREATE TABLE rollup_state (
        [signal_name]                  NVARCHAR(20)  NOT NULL,
        granularity               NVARCHAR(10)  NOT NULL,
        coverage_start_unix_nano  BIGINT,
        rolled_until_unix_nano    BIGINT        NOT NULL DEFAULT 0,
        repassed_until_unix_nano  BIGINT        NOT NULL DEFAULT 0,
        lease_owner               NVARCHAR(100),
        lease_expires_at          DATETIME2     NOT NULL DEFAULT '1970-01-01T00:00:00',
        CONSTRAINT pk_rollup_state PRIMARY KEY ([signal_name], granularity)
    );
END
GO

IF OBJECT_ID('log_rollup_minute', 'U') IS NULL
BEGIN
    CREATE TABLE log_rollup_minute (
        bucket_unix_nano BIGINT NOT NULL,
        resource_id      BIGINT NOT NULL,
        trace_count      INT    NOT NULL DEFAULT 0,
        debug_count      INT    NOT NULL DEFAULT 0,
        info_count       INT    NOT NULL DEFAULT 0,
        warn_count       INT    NOT NULL DEFAULT 0,
        error_count      INT    NOT NULL DEFAULT 0,
        fatal_count      INT    NOT NULL DEFAULT 0,
        CONSTRAINT pk_log_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id)
    );
    CREATE INDEX idx_log_rollup_minute_bucket ON log_rollup_minute (bucket_unix_nano);
END
GO

IF OBJECT_ID('log_rollup_hour', 'U') IS NULL
BEGIN
    CREATE TABLE log_rollup_hour (
        bucket_unix_nano BIGINT NOT NULL,
        resource_id      BIGINT NOT NULL,
        trace_count      INT    NOT NULL DEFAULT 0,
        debug_count      INT    NOT NULL DEFAULT 0,
        info_count       INT    NOT NULL DEFAULT 0,
        warn_count       INT    NOT NULL DEFAULT 0,
        error_count      INT    NOT NULL DEFAULT 0,
        fatal_count      INT    NOT NULL DEFAULT 0,
        CONSTRAINT pk_log_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id)
    );
    CREATE INDEX idx_log_rollup_hour_bucket ON log_rollup_hour (bucket_unix_nano);
END
GO

IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'logs' AND granularity = N'minute')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'logs', N'minute');
IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'logs' AND granularity = N'hour')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'logs', N'hour');
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS tgt
USING (VALUES (N'2.13.0')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO

-- =============================================================================================
-- SECTION E -- originally SqlServer-2.13.0-to-2.13.1.sql
-- =============================================================================================

BEGIN TRANSACTION;
GO

-- ---------------------------------------------------------------------------
-- 2.13.1 -- root-span index
-- ---------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_spans_root_time' AND object_id = OBJECT_ID('spans'))
    CREATE INDEX idx_spans_root_time ON spans (start_time_unix_nano DESC) INCLUDE (end_time_unix_nano)
        WHERE parent_span_id IS NULL;
GO

-- ---------------------------------------------------------------------------
-- 2.13.1 -- orphan_roots
-- ---------------------------------------------------------------------------

IF OBJECT_ID('orphan_roots', 'U') IS NULL
BEGIN
    CREATE TABLE orphan_roots (
        trace_id             CHAR(32)     NOT NULL,
        span_id              CHAR(16)     NOT NULL,
        resource_id          BIGINT       NOT NULL,
        start_time_unix_nano BIGINT       NOT NULL,
        end_time_unix_nano   BIGINT       NOT NULL,
        detected_at          DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT pk_orphan_roots PRIMARY KEY (trace_id)
    );
    CREATE INDEX idx_orphan_roots_start ON orphan_roots (start_time_unix_nano DESC, trace_id);
END
GO

-- ---------------------------------------------------------------------------
-- 2.13.1 -- trace rollup tables
-- ---------------------------------------------------------------------------

IF OBJECT_ID('trace_rollup_minute', 'U') IS NULL
BEGIN
    CREATE TABLE trace_rollup_minute (
        bucket_unix_nano BIGINT        NOT NULL,
        resource_id      BIGINT        NOT NULL,
        root_name        NVARCHAR(255) NOT NULL,
        inbound          TINYINT       NOT NULL,
        trace_count      INT           NOT NULL DEFAULT 0,
        error_count      INT           NOT NULL DEFAULT 0,
        duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
        duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
        lb_00 INT NOT NULL DEFAULT 0, lb_01 INT NOT NULL DEFAULT 0, lb_02 INT NOT NULL DEFAULT 0,
        lb_03 INT NOT NULL DEFAULT 0, lb_04 INT NOT NULL DEFAULT 0, lb_05 INT NOT NULL DEFAULT 0,
        lb_06 INT NOT NULL DEFAULT 0, lb_07 INT NOT NULL DEFAULT 0, lb_08 INT NOT NULL DEFAULT 0,
        lb_09 INT NOT NULL DEFAULT 0, lb_10 INT NOT NULL DEFAULT 0, lb_11 INT NOT NULL DEFAULT 0,
        lb_12 INT NOT NULL DEFAULT 0, lb_13 INT NOT NULL DEFAULT 0, lb_14 INT NOT NULL DEFAULT 0,
        lb_15 INT NOT NULL DEFAULT 0, lb_16 INT NOT NULL DEFAULT 0, lb_17 INT NOT NULL DEFAULT 0,
        lb_18 INT NOT NULL DEFAULT 0, lb_19 INT NOT NULL DEFAULT 0, lb_20 INT NOT NULL DEFAULT 0,
        lb_21 INT NOT NULL DEFAULT 0, lb_22 INT NOT NULL DEFAULT 0, lb_23 INT NOT NULL DEFAULT 0,
        lb_24 INT NOT NULL DEFAULT 0, lb_25 INT NOT NULL DEFAULT 0, lb_26 INT NOT NULL DEFAULT 0,
        lb_27 INT NOT NULL DEFAULT 0, lb_28 INT NOT NULL DEFAULT 0, lb_29 INT NOT NULL DEFAULT 0,
        lb_30 INT NOT NULL DEFAULT 0, lb_31 INT NOT NULL DEFAULT 0, lb_32 INT NOT NULL DEFAULT 0,
        lb_33 INT NOT NULL DEFAULT 0, lb_34 INT NOT NULL DEFAULT 0, lb_35 INT NOT NULL DEFAULT 0,
        lb_36 INT NOT NULL DEFAULT 0, lb_37 INT NOT NULL DEFAULT 0, lb_38 INT NOT NULL DEFAULT 0,
        lb_39 INT NOT NULL DEFAULT 0,
        CONSTRAINT pk_trace_rollup_minute PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
    );
    CREATE INDEX idx_trace_rollup_minute_bucket ON trace_rollup_minute (bucket_unix_nano);
END
GO

IF OBJECT_ID('trace_rollup_hour', 'U') IS NULL
BEGIN
    CREATE TABLE trace_rollup_hour (
        bucket_unix_nano BIGINT        NOT NULL,
        resource_id      BIGINT        NOT NULL,
        root_name        NVARCHAR(255) NOT NULL,
        inbound          TINYINT       NOT NULL,
        trace_count      INT           NOT NULL DEFAULT 0,
        error_count      INT           NOT NULL DEFAULT 0,
        duration_sum_ms  FLOAT         NOT NULL DEFAULT 0,
        duration_max_ms  FLOAT         NOT NULL DEFAULT 0,
        lb_00 INT NOT NULL DEFAULT 0, lb_01 INT NOT NULL DEFAULT 0, lb_02 INT NOT NULL DEFAULT 0,
        lb_03 INT NOT NULL DEFAULT 0, lb_04 INT NOT NULL DEFAULT 0, lb_05 INT NOT NULL DEFAULT 0,
        lb_06 INT NOT NULL DEFAULT 0, lb_07 INT NOT NULL DEFAULT 0, lb_08 INT NOT NULL DEFAULT 0,
        lb_09 INT NOT NULL DEFAULT 0, lb_10 INT NOT NULL DEFAULT 0, lb_11 INT NOT NULL DEFAULT 0,
        lb_12 INT NOT NULL DEFAULT 0, lb_13 INT NOT NULL DEFAULT 0, lb_14 INT NOT NULL DEFAULT 0,
        lb_15 INT NOT NULL DEFAULT 0, lb_16 INT NOT NULL DEFAULT 0, lb_17 INT NOT NULL DEFAULT 0,
        lb_18 INT NOT NULL DEFAULT 0, lb_19 INT NOT NULL DEFAULT 0, lb_20 INT NOT NULL DEFAULT 0,
        lb_21 INT NOT NULL DEFAULT 0, lb_22 INT NOT NULL DEFAULT 0, lb_23 INT NOT NULL DEFAULT 0,
        lb_24 INT NOT NULL DEFAULT 0, lb_25 INT NOT NULL DEFAULT 0, lb_26 INT NOT NULL DEFAULT 0,
        lb_27 INT NOT NULL DEFAULT 0, lb_28 INT NOT NULL DEFAULT 0, lb_29 INT NOT NULL DEFAULT 0,
        lb_30 INT NOT NULL DEFAULT 0, lb_31 INT NOT NULL DEFAULT 0, lb_32 INT NOT NULL DEFAULT 0,
        lb_33 INT NOT NULL DEFAULT 0, lb_34 INT NOT NULL DEFAULT 0, lb_35 INT NOT NULL DEFAULT 0,
        lb_36 INT NOT NULL DEFAULT 0, lb_37 INT NOT NULL DEFAULT 0, lb_38 INT NOT NULL DEFAULT 0,
        lb_39 INT NOT NULL DEFAULT 0,
        CONSTRAINT pk_trace_rollup_hour PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
    );
    CREATE INDEX idx_trace_rollup_hour_bucket ON trace_rollup_hour (bucket_unix_nano);
END
GO

IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'traces' AND granularity = N'minute')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'traces', N'minute');
IF NOT EXISTS (SELECT 1 FROM rollup_state WHERE [signal_name] = N'traces' AND granularity = N'hour')
    INSERT INTO rollup_state ([signal_name], granularity) VALUES (N'traces', N'hour');
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS tgt
USING (VALUES (N'2.13.1')) AS src (version)
ON tgt.version = src.version
WHEN MATCHED THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

COMMIT TRANSACTION;
GO

-- =============================================================================================
-- SECTION F -- originally SqlServer-2.13.1-to-2.13.2.sql
-- =============================================================================================
--
-- Deliberately NO explicit transaction here, same as the original file: metric_last_seen is a
-- new table (NOT a column on metrics) that the metrics catalog reads for its "has data in range"
-- check instead of scanning the five data-point tables. No FK to metrics -- see the table's own
-- comment in SqlServer-Schema.sql. Backfilled from MAX(time_unix_nano) per metric_id across the
-- five data-point tables, in batches of 4,000 metric ids at a time (the same threshold
-- MetricTouchWorker's own batches stay under, and below SQL Server's lock-escalation point) so the
-- migration never holds one enormous scan/lock per table. On a large installation this backfill is
-- the slow part of the upgrade -- expect it to run for minutes to hours depending on data-point
-- table size; each table is scanned once per metric-id batch via the existing
-- (metric_id, time_unix_nano DESC) index. The batch loop is safe to interrupt and re-run: every
-- MERGE keeps the greater value. Running it inside one big transaction (as the rest of this script
-- does) would hold locks and grow the log for the loop's entire duration and defeat that
-- interrupt-and-resume design, so it stays un-transacted here exactly as it was originally.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'metric_last_seen')
BEGIN
    CREATE TABLE metric_last_seen (
        metric_id           BIGINT NOT NULL PRIMARY KEY,
        last_seen_unix_nano BIGINT NOT NULL
    );
    CREATE INDEX idx_metric_last_seen_last_seen ON metric_last_seen (last_seen_unix_nano);
END
GO

-- ---------------------------------------------------------------------------
-- 2.13.2 -- backfill metric_last_seen from the five data-point tables, batched by metric_id
-- ---------------------------------------------------------------------------
--
-- Gated on schema_version as a performance optimization only (per the task brief's guidance):
-- once this version's row is recorded, the backfill has already run to completion (or the
-- database has no metrics at all), so skip the batch loop and its per-table scans entirely on
-- every subsequent re-run. Not load-bearing for correctness -- the loop and its MERGE statements
-- are independently idempotent -- just avoids doing the work again.
IF NOT EXISTS (SELECT 1 FROM schema_version WHERE version = N'2.13.2')
BEGIN
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
END
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS target
USING (VALUES (N'2.13.2')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

-- =============================================================================================
-- SECTION G -- originally SqlServer-2.13.2-to-2.13.3.sql
-- =============================================================================================
--
-- Deliberately NO explicit transaction here either, same as the original file: resources.
-- service_name is a real column, backfilled from attributes_json's "service.name" key via
-- JSON_VALUE (the same extraction ResourceServiceNameExpr used to compile per-query).
-- ResourceServiceNameExpr() is now just "{alias}.service_name" on every provider, including this
-- one -- the service_name column itself is NOT part of the decision 39 tiering split. SQL Server
-- stays on the standard tier for search: no index changes to the phase 1 unindexed LIKE/JSON_VALUE
-- predicates, still bounded by the 24-hour raw search window (ProviderCapabilities). No SQL Server
-- full-text catalog is added -- out of scope per tiering.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('resources') AND name = 'service_name')
BEGIN
    ALTER TABLE resources ADD service_name NVARCHAR(255) NULL;
END
GO

-- Gated on schema_version as a performance optimization only (per the task brief's guidance),
-- same reasoning as the 2.13.2 backfill above. The UPDATE's own WHERE service_name IS NULL clause
-- already makes it naturally idempotent -- this gate just avoids the full-table predicate scan
-- once the database is caught up (service_name has no index until the next step creates one).
IF NOT EXISTS (SELECT 1 FROM schema_version WHERE version = N'2.13.3')
BEGIN
    UPDATE resources
    SET service_name = JSON_VALUE(attributes_json, '$."service.name"')
    WHERE service_name IS NULL AND attributes_json IS NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_resources_service_name' AND object_id = OBJECT_ID('resources'))
BEGIN
    CREATE INDEX idx_resources_service_name ON resources (service_name);
END
GO

-- Record the new version (matches what the full schema script writes).
MERGE schema_version AS target
USING (VALUES (N'2.13.3')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO
