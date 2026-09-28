-- =============================================================================
-- MySQL-Migrate.sql -- consolidated, idempotent MySQL schema migration
-- =============================================================================
--
-- Brings a MySQL telemetry database from ANY existing schema version -- 2.11.0, 2.12.0, 2.13.0,
-- 2.13.1, 2.13.2, already-current 2.13.3, or a brand-new database with none of these tables at
-- all -- up to current (2.13.3). Safe to run any number of times in a row, including immediately
-- again after a successful run.
--
-- Usage:
--   mysql telemetry < schema/migrations/MySQL-Migrate.sql
--
-- This file REPLACES the five per-version files that previously had to be run by hand in exact
-- order (now deleted from schema/migrations/):
--   MySQL-2.11.0-to-2.12.0.sql, MySQL-2.12.0-to-2.13.0.sql, MySQL-2.13.0-to-2.13.1.sql,
--   MySQL-2.13.1-to-2.13.2.sql, MySQL-2.13.2-to-2.13.3.sql
--
-- MySQL's DDL is not transactional (implicit commit per statement), so this cannot be wrapped in
-- one all-or-nothing transaction the way the other providers' migrations are -- exactly as each of
-- the five original files noted. Every step below is independently idempotent instead, so a
-- re-run after any partial failure (this script's own, or a hand-run predecessor's) is safe.
--
-- ---------------------------------------------------------------------------------------------
-- DESIGN
-- ---------------------------------------------------------------------------------------------
--
-- 1. Every CREATE TABLE below is IF NOT EXISTS and already carries the FINAL (2.13.3) column
--    shape from schema/MySQL-Schema.sql (including the 2.13.1 spans.is_root generated column and
--    the 2.13.3 resources.service_name column). A brand-new database therefore ends up
--    structurally identical to a fresh MySQL-Schema.sql install in a single pass, with no
--    follow-up ALTER needed for tables this script itself just created. On a database that
--    already has the table (any of 2.11.0 through 2.13.3), CREATE TABLE IF NOT EXISTS is a no-op,
--    which is exactly why step 2 exists.
--
-- 2. For a table that already existed with an OLDER shape (missing a column or index added along
--    the way), a guarded ALTER TABLE ... ADD COLUMN / ADD INDEX / DROP INDEX statement brings it
--    up to date, each wrapped in an `IF NOT EXISTS (SELECT 1 FROM information_schema.columns /
--    .statistics WHERE ...) THEN ... END IF` check inside a one-off stored procedure -- the exact
--    idiom the four older per-version files (2.11.0->2.12.0 through 2.13.1->2.13.2) already used.
--
--    CORRECTION (this file previously read differently here): an earlier draft of this
--    consolidated script instead used `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` / `ADD INDEX IF
--    NOT EXISTS` / `DROP INDEX IF EXISTS` directly, on the mistaken assumption that MySQL 8.0.29+
--    added native `IF [NOT] EXISTS` support to those ALTER TABLE clauses (by analogy with the
--    then-most-recent original file, 2.13.2->2.13.3, which made that same claim in its own
--    comment). That assumption is wrong: `IF [NOT] EXISTS` on ADD COLUMN / ADD INDEX / DROP INDEX
--    is a MariaDB-only extension, not MySQL. Confirmed live against a real `mysql:8.0` container
--    (8.0.46, the image tests/Keryhe.Telemetry.IntegrationTests/Fixtures/MySqlFixture.cs uses):
--    `ALTER TABLE t ADD COLUMN IF NOT EXISTS c INT` and `ALTER TABLE t ADD INDEX IF NOT EXISTS ix
--    (c)` both fail with `ERROR 1064 (42000): ... syntax ... near 'IF NOT EXISTS ...'` -- the
--    draft failed on its very first guarded ALTER when run against a real server. Every guarded
--    structural change in this file, including the ones new in this version range, therefore goes
--    back to the information_schema-probing procedure idiom throughout, with no native-syntax
--    shortcut anywhere.
--
--    NOTE: standalone `CREATE INDEX ... IF NOT EXISTS` is likewise NOT valid MySQL syntax (also a
--    MariaDB-only extension) -- only a guarded `ALTER TABLE ... ADD INDEX` inside a stored
--    procedure supports conditional index creation in MySQL, so every secondary index below
--    (whether brand new in this version range or carried over unchanged from before 2.11.0) is
--    added that way instead of with a plain CREATE INDEX statement, since re-issuing CREATE INDEX
--    for an index that already exists is a hard error ("Duplicate key name"), not a no-op.
--
-- 3. Views are re-created with CREATE OR REPLACE VIEW, which is naturally idempotent and did not
--    change shape anywhere in this version range -- safe to reissue unconditionally on any
--    starting version, including "table doesn't exist yet".
--
-- 4. rollup_state's seed rows and retention_settings' seed row are inserted with
--    ON DUPLICATE KEY UPDATE / INSERT IGNORE respectively, which are naturally idempotent and
--    (for retention_settings) deliberately never overwrite a value the operator has since changed
--    via the settings API.
--
-- 5. The two data BACKFILLS (metric_last_seen from the five data-point tables; resources.
--    service_name from attributes_json) need a second, separate one-off stored procedure from the
--    structural one in step 2: existence checks there test schema shape (does this column/index
--    exist), but a backfill needs "have I already scanned and populated this data", which is
--    tracked by gating on whether that version's schema_version row already exists -- i.e. on this
--    script's own prior success -- so a re-run of this whole script never rescans gigabytes of
--    already-backfilled data. Both backfills are wrapped in their own one-off stored procedure,
--    CALLed exactly once, then DROPped again immediately after (see section 5 below), same as the
--    structural-change procedure in section 2 is DROPped right after its own CALL.
--
-- 6. All rationale/comments from the five original per-version files are preserved below, inline
--    at the point each corresponding change is made, so none of the original "why" is lost by
--    consolidating.
--
-- schema_version bookkeeping: every historical milestone version this script brings a database
-- through (2.12.0, 2.13.0, 2.13.1, 2.13.2, 2.13.3) is recorded with its own row, exactly as
-- running the five original files by hand would have produced, via
-- INSERT ... ON DUPLICATE KEY UPDATE applied_at = ... (so a re-run just refreshes the timestamp,
-- never errors). The backfill-gating checks in step 5 above read this table's state as it stood
-- BEFORE this script's own version-stamping runs, which is why the version stamps are the very
-- last thing this script does.

-- =============================================================================
-- 1. TABLES -- CREATE TABLE IF NOT EXISTS, final (2.13.3) shape
-- =============================================================================

CREATE TABLE IF NOT EXISTS tenants (
    id         BIGINT AUTO_INCREMENT PRIMARY KEY,
    name       VARCHAR(255) NOT NULL,
    created_at DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT uk_tenant_name UNIQUE (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS api_keys (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id    BIGINT       NOT NULL,
    key_hash     CHAR(64)     NOT NULL,
    name         VARCHAR(255) NOT NULL,
    is_active    TINYINT(1)   NOT NULL DEFAULT 1,
    created_at   DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_used_at DATETIME(6),
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash),
    CONSTRAINT fk_api_keys_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Resource represents the entity producing telemetry.
-- service_name (schema 2.13.3, list-pages-server-side plan Phase 7): a real column, written by
-- the bulk writer's resource upsert, extracted from attributes_json's "service.name" key.
-- ResourceServiceNameExpr() is now just "{alias}.service_name" on every provider.
CREATE TABLE IF NOT EXISTS resources (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    tenant_id       BIGINT       NOT NULL DEFAULT 1,
    resource_hash   CHAR(64)     NOT NULL,
    schema_url      VARCHAR(2048),
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json JSON,
    service_name    VARCHAR(255),
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash),
    CONSTRAINT fk_resources_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Instrumentation scope (library).
CREATE TABLE IF NOT EXISTS instrumentation_scopes (
    id              BIGINT AUTO_INCREMENT PRIMARY KEY,
    name            VARCHAR(255) NOT NULL,
    version         VARCHAR(255),
    schema_url      VARCHAR(2048),
    scope_hash      CHAR(64)     NOT NULL,
    created_at      DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json JSON,
    CONSTRAINT uk_scope_hash UNIQUE (scope_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Spans. is_root (schema 2.13.1, list-pages-server-side plan Phase 3): stored generated column
-- backing the root-span index -- MySQL has no filtered/partial index, so root-anchored queries
-- seek this boolean-shaped column instead.
CREATE TABLE IF NOT EXISTS spans (
    id                       BIGINT AUTO_INCREMENT PRIMARY KEY,
    trace_id                 CHAR(32)     NOT NULL,
    span_id                  CHAR(16)     NOT NULL,
    parent_span_id           CHAR(16),
    resource_id              BIGINT       NOT NULL,
    scope_id                 BIGINT       NOT NULL,
    name                     VARCHAR(255) NOT NULL,
    kind                     VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    start_time_unix_nano     BIGINT       NOT NULL,
    end_time_unix_nano       BIGINT       NOT NULL,
    dropped_attributes_count INT          DEFAULT 0,
    dropped_events_count     INT          DEFAULT 0,
    dropped_links_count      INT          DEFAULT 0,
    trace_state              TEXT,
    flags                    INT          DEFAULT 0,
    status_code              VARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK (status_code IN ('UNSET', 'OK', 'ERROR')),
    status_message           TEXT,
    created_at               DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json          JSON,
    events_json              JSON,
    links_json               JSON,
    is_root                  BOOLEAN GENERATED ALWAYS AS (parent_span_id IS NULL) STORED,
    CONSTRAINT fk_spans_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    CONSTRAINT uk_trace_span      UNIQUE (trace_id, span_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS metrics (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    resource_id BIGINT       NOT NULL,
    scope_id    BIGINT       NOT NULL,
    name        VARCHAR(255) NOT NULL,
    description TEXT,
    unit        VARCHAR(63),
    type        VARCHAR(30)  NOT NULL
        CHECK (type IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at  DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, type, scope_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 ROW_FORMAT=DYNAMIC;

CREATE TABLE IF NOT EXISTS gauge_data_points (
    id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         DOUBLE,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    attributes_json      JSON,
    exemplars_json       JSON,
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sum_data_points (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    value_double            DOUBLE,
    value_int               BIGINT,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            TINYINT(1)  DEFAULT 0,
    flags                   INT         DEFAULT 0,
    attributes_json         JSON,
    exemplars_json          JSON,
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS histogram_data_points (
    id                      BIGINT      AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    count                   BIGINT      NOT NULL,
    sum_value               DOUBLE,
    bucket_counts           JSON,
    explicit_bounds         JSON,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT         DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    attributes_json         JSON,
    exemplars_json          JSON,
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS exponential_histogram_data_points (
    id                      BIGINT      AUTO_INCREMENT PRIMARY KEY,
    metric_id               BIGINT      NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT      NOT NULL,
    count                   BIGINT      NOT NULL,
    sum_value               DOUBLE,
    scale                   INT         NOT NULL,
    zero_count              BIGINT      NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  JSON,
    negative_offset         INT,
    negative_bucket_counts  JSON,
    aggregation_temporality VARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT         DEFAULT 0,
    min_value               DOUBLE,
    max_value               DOUBLE,
    attributes_json         JSON,
    exemplars_json          JSON,
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS summary_data_points (
    id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            DOUBLE NOT NULL,
    quantile_values      JSON,
    flags                INT    DEFAULT 0,
    attributes_json      JSON,
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- metric_last_seen (schema 2.13.2, list-pages-server-side plan Phase 5, decision 27): NOT a
-- column on metrics -- see PostgreSQL-Schema.sql's identical table for the full rationale (no FK,
-- so ingestion's metrics upsert and this table's touch worker can never deadlock). Backfilled
-- below (section 5) from MAX(time_unix_nano) per metric_id across the five data-point tables.
CREATE TABLE IF NOT EXISTS metric_last_seen (
    metric_id           BIGINT NOT NULL PRIMARY KEY,
    last_seen_unix_nano BIGINT NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Default 0 for time_unix_nano handles OTLP records where TimeUnixNano is absent.
CREATE TABLE IF NOT EXISTS log_records (
    id                       BIGINT      AUTO_INCREMENT PRIMARY KEY,
    resource_id              BIGINT      NOT NULL,
    scope_id                 BIGINT      NOT NULL,
    time_unix_nano           BIGINT      NOT NULL DEFAULT 0,
    observed_time_unix_nano  BIGINT,
    severity_number          INT,
    severity_text            VARCHAR(255),
    event_name               VARCHAR(256),
    body_type                VARCHAR(20) DEFAULT 'STRING'
        CHECK (body_type IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    body_value               LONGTEXT,
    dropped_attributes_count INT         DEFAULT 0,
    flags                    INT         DEFAULT 0,
    trace_id                 CHAR(32),
    span_id                  CHAR(16),
    created_at               DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    attributes_json          JSON,
    CONSTRAINT fk_log_records_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- ROLLUP TABLES (schema 2.13.0/2.13.1, list-pages-server-side plan decisions 37-38, 41)
-- =============================================================================

-- One row per signal + granularity, claimed by RollupWorker with an atomic
-- UPDATE ... WHERE lease_expires_at < now, the same pattern as alert_rules' TryClaimFireAsync.
-- No foreign keys: the worker's writes must never lock resources or anything ingestion touches.
CREATE TABLE IF NOT EXISTS rollup_state (
    signal_name              VARCHAR(20)  NOT NULL,
    granularity              VARCHAR(10)  NOT NULL,
    coverage_start_unix_nano BIGINT,
    rolled_until_unix_nano   BIGINT       NOT NULL DEFAULT 0,
    repassed_until_unix_nano BIGINT       NOT NULL DEFAULT 0,
    lease_owner              VARCHAR(100),
    lease_expires_at         DATETIME(6)  NOT NULL DEFAULT '1970-01-01 00:00:00',
    PRIMARY KEY (signal_name, granularity)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Per-minute log summary, recomputed from raw log_records by RollupWorker -- never incremented
-- during ingestion (decision 38). Severity groups match LogReadRepositoryBase.
-- GetLogHistogramAsync's six-group CASE exactly. No foreign key on resource_id: the worker's
-- writes must never lock resources.
CREATE TABLE IF NOT EXISTS log_rollup_minute (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Same shape, one row per hour.
CREATE TABLE IF NOT EXISTS log_rollup_hour (
    bucket_unix_nano BIGINT NOT NULL,
    resource_id      BIGINT NOT NULL,
    trace_count      INT    NOT NULL DEFAULT 0,
    debug_count      INT    NOT NULL DEFAULT 0,
    info_count       INT    NOT NULL DEFAULT 0,
    warn_count       INT    NOT NULL DEFAULT 0,
    error_count      INT    NOT NULL DEFAULT 0,
    fatal_count      INT    NOT NULL DEFAULT 0,
    PRIMARY KEY (bucket_unix_nano, resource_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Traces whose root span never arrived (schema 2.13.1, decision 41): one row per trace, holding
-- the anchor span (its earliest span, whose own parent does not exist anywhere) that the rollup
-- worker detected per finished minute. No foreign keys: the worker's writes must never lock
-- resources. Indexed to merge with idx_spans_root_time in the same order.
CREATE TABLE IF NOT EXISTS orphan_roots (
    trace_id             CHAR(32)     NOT NULL PRIMARY KEY,
    span_id              CHAR(16)     NOT NULL,
    resource_id          BIGINT       NOT NULL,
    start_time_unix_nano BIGINT       NOT NULL,
    end_time_unix_nano   BIGINT       NOT NULL,
    detected_at          DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Per-minute trace summary (schema 2.13.1, decisions 37-38, 41): one row per minute, per anchor
-- span's resource, operation name (folded to '__other__' past the 200-distinct-name cardinality
-- guard) and inbound flag (anchor kind SERVER/CONSUMER). Counts traces whose ANCHOR (null-parent
-- root, or its orphan_roots span) starts in that minute; error flag and duration are aggregated
-- over the trace's full span set. lb_00..lb_39 are the fixed latency-bucket counts
-- (LatencyBucketSql) as plain columns so SQL can sum them across rows.
CREATE TABLE IF NOT EXISTS trace_rollup_minute (
    bucket_unix_nano BIGINT       NOT NULL,
    resource_id      BIGINT       NOT NULL,
    root_name        VARCHAR(255) NOT NULL,
    inbound          TINYINT      NOT NULL,
    trace_count      INT          NOT NULL DEFAULT 0,
    error_count      INT          NOT NULL DEFAULT 0,
    duration_sum_ms  DOUBLE       NOT NULL DEFAULT 0,
    duration_max_ms  DOUBLE       NOT NULL DEFAULT 0,
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
    PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Same shape, one row per hour.
CREATE TABLE IF NOT EXISTS trace_rollup_hour (
    bucket_unix_nano BIGINT       NOT NULL,
    resource_id      BIGINT       NOT NULL,
    root_name        VARCHAR(255) NOT NULL,
    inbound          TINYINT      NOT NULL,
    trace_count      INT          NOT NULL DEFAULT 0,
    error_count      INT          NOT NULL DEFAULT 0,
    duration_sum_ms  DOUBLE       NOT NULL DEFAULT 0,
    duration_max_ms  DOUBLE       NOT NULL DEFAULT 0,
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
    PRIMARY KEY (bucket_unix_nano, resource_id, root_name, inbound)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- UTILITY / ALERTING / RETENTION TABLES
-- =============================================================================

CREATE TABLE IF NOT EXISTS schema_version (
    version    VARCHAR(20) PRIMARY KEY,
    applied_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS alert_rules (
    id               INT          AUTO_INCREMENT PRIMARY KEY,
    tenant_id        BIGINT       NOT NULL,
    name             TEXT         NOT NULL,
    type             VARCHAR(50)  NOT NULL,
    service_name     VARCHAR(255),
    condition_json   JSON         NOT NULL,
    webhook_url      TEXT         NOT NULL,
    cooldown_minutes INT          NOT NULL DEFAULT 60,
    enabled          TINYINT(1)   NOT NULL DEFAULT 1,
    created_at       DATETIME(6)  NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_fired_at    DATETIME(6),
    CONSTRAINT fk_alert_rules_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS alert_events (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    rule_id      INT         NOT NULL,
    fired_at     DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    details_json JSON        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Single global row (id = 1, enforced by the CHECK below) -- see IRetentionSettingsRepository for
-- why this is untenanted and why UPDATE, never INSERT, is the only mutation the app issues
-- against it after the seed row below.
CREATE TABLE IF NOT EXISTS retention_settings (
    id                    SMALLINT    NOT NULL PRIMARY KEY DEFAULT 1,
    trace_retention_days  INT         NOT NULL,
    log_retention_days    INT         NOT NULL,
    metric_retention_days INT         NOT NULL,
    updated_at            DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT chk_retention_settings_singleton CHECK (id = 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- =============================================================================
-- 2. COLUMNS / INDEXES ADDED OR RETIRED ALONG THE WAY, AND 3. THE FULL SECONDARY INDEX SET --
--    guarded conditional DDL, via a one-off stored procedure
-- =============================================================================
--
-- MySQL (unlike MariaDB) does NOT support `IF [NOT] EXISTS` on the ADD COLUMN, ADD INDEX or
-- DROP INDEX clauses of ALTER TABLE, at any 8.0.x version -- confirmed live against a real
-- mysql:8.0 (8.0.46) container: `ALTER TABLE t ADD COLUMN IF NOT EXISTS c INT` and
-- `ALTER TABLE t ADD INDEX IF NOT EXISTS ix (c)` both fail with ERROR 1064 (syntax error). That
-- clause is a MariaDB-only extension. An earlier draft of this file assumed otherwise (reasoning
-- that 8.0.29+ added it, by analogy with the real `information_schema`-probing procedures the four
-- original per-version files it replaces already used for this exact purpose) and does not run.
-- Every conditional structural change below therefore goes back to that original, verified idiom:
-- a one-off stored procedure, CALLed exactly once, that probes `information_schema.columns` /
-- `.statistics` before each ALTER, then DROPped again immediately after. Plain `IF NOT EXISTS` on
-- CREATE TABLE and `CREATE OR REPLACE VIEW` are unaffected -- those clauses work as normal MySQL
-- DDL and need no such guard.
--
-- This covers both the version-specific changes to a table that already existed with an older
-- shape (section 2's three changes) and the full current secondary-index set (section 3) in one
-- procedure -- a table a CREATE TABLE IF NOT EXISTS above just skipped (because it already existed,
-- at any version from 2.11.0 up) still needs every one of its indexes added the same guarded way,
-- and standalone `CREATE INDEX ... IF NOT EXISTS` is equally not valid MySQL syntax (also a
-- MariaDB-only extension), so `ADD INDEX` inside a guarded IF block is used uniformly instead of a
-- plain `CREATE INDEX` for every index below, new-in-this-range or carried over unchanged.

DROP PROCEDURE IF EXISTS _mysql_migrate_alters;

DELIMITER //
CREATE PROCEDURE _mysql_migrate_alters()
BEGIN
    -- -----------------------------------------------------------------------------------------
    -- 2.13.0 -- keyset tiebreak index for log_records (see plans/list-pages-server-side.md
    -- Phase 2): idx_log_time (time_unix_nano DESC) is replaced by idx_log_time_id
    -- (time_unix_nano DESC, id DESC), the keyset-paging tiebreak GetLogPageAsync needs. The old
    -- index is a pure left prefix of the new one, so it is dropped (same reasoning as the 2.8.0
    -- spans index cleanup). idx_log_time_id itself is (re-)added in the full index set below.
    -- -----------------------------------------------------------------------------------------
    IF EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_time'
    ) THEN
        ALTER TABLE log_records DROP INDEX idx_log_time;
    END IF;

    -- -----------------------------------------------------------------------------------------
    -- 2.13.1 -- is_root generated column (see plans/list-pages-server-side.md Phase 3). Already
    -- part of the CREATE TABLE IF NOT EXISTS above for a brand-new spans table; this only fires
    -- for a spans table that already existed (2.11.0/2.12.0 starting point) without it. The
    -- matching idx_spans_root_time index is (re-)added in the full index set below.
    -- -----------------------------------------------------------------------------------------
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'spans' AND column_name = 'is_root'
    ) THEN
        ALTER TABLE spans ADD COLUMN is_root BOOLEAN GENERATED ALWAYS AS (parent_span_id IS NULL) STORED;
    END IF;

    -- -----------------------------------------------------------------------------------------
    -- 2.13.3 -- resources.service_name (see plans/list-pages-server-side.md Phase 7, decisions
    -- 8/39): a real column, backfilled in section 5 below from attributes_json's "service.name"
    -- key. Already part of the CREATE TABLE IF NOT EXISTS above for a brand-new resources table;
    -- this only fires for a resources table that already existed without it. The matching
    -- idx_resources_service_name index is (re-)added in the full index set below.
    -- -----------------------------------------------------------------------------------------
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'resources' AND column_name = 'service_name'
    ) THEN
        ALTER TABLE resources ADD COLUMN service_name VARCHAR(255);
    END IF;

    -- ===========================================================================================
    -- Full current secondary-index set -- every index in the current schema, not just the ones
    -- new in this version range, since a table this script's CREATE TABLE IF NOT EXISTS just
    -- skipped (already existed, at any version from 2.11.0 up) still needs each of these.
    -- ===========================================================================================

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'api_keys' AND index_name = 'idx_api_keys_tenant_id') THEN
        ALTER TABLE api_keys ADD INDEX idx_api_keys_tenant_id (tenant_id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'resources' AND index_name = 'idx_resources_tenant_id') THEN
        ALTER TABLE resources ADD INDEX idx_resources_tenant_id (tenant_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'resources' AND index_name = 'idx_created_at') THEN
        ALTER TABLE resources ADD INDEX idx_created_at (created_at);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'resources' AND index_name = 'idx_resources_service_name') THEN
        ALTER TABLE resources ADD INDEX idx_resources_service_name (service_name);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'instrumentation_scopes' AND index_name = 'idx_name_version') THEN
        ALTER TABLE instrumentation_scopes ADD INDEX idx_name_version (name, version);
    END IF;

    -- idx_trace_id, idx_start_time, idx_kind and idx_status were dropped in 2.8.0 (before this
    -- version range) and are deliberately NOT re-added here: idx_trace_id was a left prefix of
    -- uk_trace_span (trace_id, span_id); idx_start_time was a left prefix of idx_duration
    -- (start_time_unix_nano, end_time_unix_nano); idx_kind (6 distinct values) and idx_status
    -- (3 distinct values) were too low-cardinality for the planner to ever choose. All three
    -- carried real write cost for zero read benefit -- see CLAUDE.md's "spans index set" notes.
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_span_id') THEN
        ALTER TABLE spans ADD INDEX idx_span_id (span_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_parent_span') THEN
        ALTER TABLE spans ADD INDEX idx_parent_span (parent_span_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_trace_parent') THEN
        ALTER TABLE spans ADD INDEX idx_spans_trace_parent (trace_id, parent_span_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_end_time') THEN
        ALTER TABLE spans ADD INDEX idx_end_time (end_time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_duration') THEN
        ALTER TABLE spans ADD INDEX idx_duration (start_time_unix_nano, end_time_unix_nano);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_name') THEN
        ALTER TABLE spans ADD INDEX idx_spans_name (name);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_resource_time') THEN
        ALTER TABLE spans ADD INDEX idx_spans_resource_time (resource_id, start_time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_error') THEN
        ALTER TABLE spans ADD INDEX idx_spans_error (status_code, start_time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'spans' AND index_name = 'idx_spans_root_time') THEN
        ALTER TABLE spans ADD INDEX idx_spans_root_time (is_root, start_time_unix_nano DESC, end_time_unix_nano);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'metrics' AND index_name = 'idx_metrics_name') THEN
        ALTER TABLE metrics ADD INDEX idx_metrics_name (name);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'metrics' AND index_name = 'idx_type') THEN
        ALTER TABLE metrics ADD INDEX idx_type (type);
    END IF;
    -- idx_resource_name (resource_id, name) dropped in 2.7.0 (before this version range): now a
    -- left prefix of uk_metric_identity.

    -- Standalone time index added in 2.7.0: metric retention deletes from the data-point tables
    -- by time_unix_nano alone, which without this would full-scan.
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'gauge_data_points' AND index_name = 'idx_gauge_metric_time') THEN
        ALTER TABLE gauge_data_points ADD INDEX idx_gauge_metric_time (metric_id, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'gauge_data_points' AND index_name = 'idx_gauge_time') THEN
        ALTER TABLE gauge_data_points ADD INDEX idx_gauge_time (time_unix_nano DESC);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'sum_data_points' AND index_name = 'idx_sum_metric_time') THEN
        ALTER TABLE sum_data_points ADD INDEX idx_sum_metric_time (metric_id, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'sum_data_points' AND index_name = 'idx_sum_time') THEN
        ALTER TABLE sum_data_points ADD INDEX idx_sum_time (time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'sum_data_points' AND index_name = 'idx_temporality') THEN
        ALTER TABLE sum_data_points ADD INDEX idx_temporality (aggregation_temporality);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'histogram_data_points' AND index_name = 'idx_histogram_metric_time') THEN
        ALTER TABLE histogram_data_points ADD INDEX idx_histogram_metric_time (metric_id, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'histogram_data_points' AND index_name = 'idx_histogram_time') THEN
        ALTER TABLE histogram_data_points ADD INDEX idx_histogram_time (time_unix_nano DESC);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'exponential_histogram_data_points' AND index_name = 'idx_exp_histogram_metric_time') THEN
        ALTER TABLE exponential_histogram_data_points ADD INDEX idx_exp_histogram_metric_time (metric_id, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'exponential_histogram_data_points' AND index_name = 'idx_exp_histogram_time') THEN
        ALTER TABLE exponential_histogram_data_points ADD INDEX idx_exp_histogram_time (time_unix_nano DESC);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'summary_data_points' AND index_name = 'idx_summary_metric_time') THEN
        ALTER TABLE summary_data_points ADD INDEX idx_summary_metric_time (metric_id, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'summary_data_points' AND index_name = 'idx_summary_time') THEN
        ALTER TABLE summary_data_points ADD INDEX idx_summary_time (time_unix_nano DESC);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'metric_last_seen' AND index_name = 'idx_metric_last_seen_last_seen') THEN
        ALTER TABLE metric_last_seen ADD INDEX idx_metric_last_seen_last_seen (last_seen_unix_nano);
    END IF;

    -- idx_log_time (dropped above) was a pure left prefix of idx_log_time_id.
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_time_id') THEN
        ALTER TABLE log_records ADD INDEX idx_log_time_id (time_unix_nano DESC, id DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_observed_time') THEN
        ALTER TABLE log_records ADD INDEX idx_observed_time (observed_time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_severity') THEN
        ALTER TABLE log_records ADD INDEX idx_severity (severity_number);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_severity_time') THEN
        ALTER TABLE log_records ADD INDEX idx_log_severity_time (severity_number, time_unix_nano DESC);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_trace_span') THEN
        ALTER TABLE log_records ADD INDEX idx_log_trace_span (trace_id, span_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_records' AND index_name = 'idx_log_resource_time') THEN
        ALTER TABLE log_records ADD INDEX idx_log_resource_time (resource_id, time_unix_nano DESC);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_rollup_minute' AND index_name = 'idx_log_rollup_minute_bucket') THEN
        ALTER TABLE log_rollup_minute ADD INDEX idx_log_rollup_minute_bucket (bucket_unix_nano);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'log_rollup_hour' AND index_name = 'idx_log_rollup_hour_bucket') THEN
        ALTER TABLE log_rollup_hour ADD INDEX idx_log_rollup_hour_bucket (bucket_unix_nano);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'orphan_roots' AND index_name = 'idx_orphan_roots_start') THEN
        ALTER TABLE orphan_roots ADD INDEX idx_orphan_roots_start (start_time_unix_nano DESC, trace_id);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'trace_rollup_minute' AND index_name = 'idx_trace_rollup_minute_bucket') THEN
        ALTER TABLE trace_rollup_minute ADD INDEX idx_trace_rollup_minute_bucket (bucket_unix_nano);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'trace_rollup_hour' AND index_name = 'idx_trace_rollup_hour_bucket') THEN
        ALTER TABLE trace_rollup_hour ADD INDEX idx_trace_rollup_hour_bucket (bucket_unix_nano);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'alert_rules' AND index_name = 'idx_alert_rules_tenant_id') THEN
        ALTER TABLE alert_rules ADD INDEX idx_alert_rules_tenant_id (tenant_id);
    END IF;
    -- MySQL has no filtered indexes; a plain composite index covers the enabled-rules lookup.
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'alert_rules' AND index_name = 'idx_alert_rules_tenant_enabled') THEN
        ALTER TABLE alert_rules ADD INDEX idx_alert_rules_tenant_enabled (tenant_id, enabled);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'alert_events' AND index_name = 'idx_alert_events_rule_id') THEN
        ALTER TABLE alert_events ADD INDEX idx_alert_events_rule_id (rule_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'alert_events' AND index_name = 'idx_alert_events_fired_at') THEN
        ALTER TABLE alert_events ADD INDEX idx_alert_events_fired_at (fired_at DESC);
    END IF;
END //
DELIMITER ;

CALL _mysql_migrate_alters();

DROP PROCEDURE _mysql_migrate_alters;
-- =============================================================================
-- 4. SEED DATA -- idempotent, and never clobbers operator-configured values
-- =============================================================================

-- Seed the four rows this schema needs (logs/traces x minute/hour). ON DUPLICATE KEY UPDATE is a
-- true no-op here (assigns a column to itself), so this is safe to re-run unconditionally.
INSERT INTO rollup_state (signal_name, granularity)
VALUES ('logs', 'minute'), ('logs', 'hour'), ('traces', 'minute'), ('traces', 'hour')
ON DUPLICATE KEY UPDATE signal_name = signal_name;

-- Seeded with today's implicit defaults (traces 90d, logs 90d, metrics 180d) ONLY the first time
-- this row is created -- INSERT IGNORE means a database where the operator has since edited these
-- values via the settings API (PUT /api/settings/retention) is left untouched on every re-run.
INSERT IGNORE INTO retention_settings (id, trace_retention_days, log_retention_days, metric_retention_days)
VALUES (1, 90, 90, 180);

-- =============================================================================
-- 5. DATA BACKFILLS -- one-off stored procedure, CALLed once, DROPped immediately after
-- =============================================================================
--
-- IF/THEN control flow is only legal inside a stored routine, never at the top level of a plain
-- `mysql < file.sql` script -- see the design note at the top of this file. Both backfills below
-- are gated on whether the version that introduced them has already been recorded in
-- schema_version (i.e. whether this migration -- or, on an old database, the original per-version
-- file it replaces -- has already run), so a re-run of this script never rescans data it already
-- backfilled.

DROP PROCEDURE IF EXISTS _mysql_migrate_backfills;

DELIMITER //
CREATE PROCEDURE _mysql_migrate_backfills()
BEGIN
    -- -------------------------------------------------------------------------------------------
    -- 2.13.2 -- backfill metric_last_seen from the five data-point tables (see
    -- plans/list-pages-server-side.md Phase 5, decision 27).
    --
    -- DEVIATION from the plan's "batched" wording, carried over from the original
    -- MySQL-2.13.1-to-2.13.2.sql and documented there and here rather than silently: unlike the
    -- PostgreSQL/Timescale (PL/pgSQL DO loop) and SQL Server (T-SQL WHILE loop) migrations, this
    -- backfills each data-point table in a single INSERT ... SELECT ... GROUP BY pass rather than
    -- chunking by metric_id range. A plain `mysql < file` script has no portable loop construct
    -- without wrapping the whole migration in a stored procedure, which was judged not worth the
    -- added complexity for a one-time upgrade step (this file now DOES wrap things in a
    -- procedure for the version-gating IF below, but that does not by itself make a chunked loop
    -- free -- it would still need its own state and looping logic). On a large installation this
    -- single pass can run for minutes to hours and takes InnoDB's ordinary read locks on the
    -- source tables for its duration (each is covered by the existing
    -- (metric_id, time_unix_nano DESC) index, so it is a sequential per-metric index scan, not a
    -- full table scan) -- run this during a maintenance window on a very large database. If this
    -- needs chunking in practice, extend this procedure with a metric_id range loop, mirroring
    -- the SQL Server migration's shape.
    -- -------------------------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.2') THEN
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
    END IF;

    -- -------------------------------------------------------------------------------------------
    -- 2.13.3 -- backfill resources.service_name from attributes_json (see
    -- plans/list-pages-server-side.md Phase 7, decisions 8/39).
    -- -------------------------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM schema_version WHERE version = '2.13.3') THEN
        UPDATE resources
        SET service_name = attributes_json ->> '$."service.name"'
        WHERE service_name IS NULL AND attributes_json IS NOT NULL;
    END IF;
END //
DELIMITER ;

CALL _mysql_migrate_backfills();

DROP PROCEDURE _mysql_migrate_backfills;

-- =============================================================================
-- 6. VIEWS -- CREATE OR REPLACE, unchanged shape across this whole version range
-- =============================================================================

-- Trace summary: aggregated span counts and timing per trace.
CREATE OR REPLACE VIEW trace_summary AS
SELECT
    s.trace_id                                                AS trace_id_hex,
    s.trace_id,
    COUNT(*)                                                  AS span_count,
    MIN(s.start_time_unix_nano)                               AS trace_start_time,
    MAX(s.end_time_unix_nano)                                 AS trace_end_time,
    MAX(s.end_time_unix_nano) - MIN(s.start_time_unix_nano)   AS trace_duration_ns,
    r.id                                                      AS resource_id
FROM spans s
JOIN resources r ON s.resource_id = r.id
GROUP BY s.trace_id, r.id;

-- Service map: service-to-service call relationships extracted from span parent-child pairs.
-- The ->> operator (JSON_UNQUOTE(JSON_EXTRACT(...))) replaces SQL Server's JSON_VALUE.
-- The attribute key "service.name" contains a dot, so the JSON path quotes it: $."service.name".
CREATE OR REPLACE VIEW service_map AS
SELECT
    parent_res.service_name AS parent_service,
    child_res.service_name AS child_service,
    child.kind                                         AS span_kind,
    COUNT(*)                                           AS call_count
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    parent_res.service_name IS NOT NULL
    AND child_res.service_name IS NOT NULL
    AND parent_res.service_name <>
        child_res.service_name
GROUP BY
    parent_res.service_name,
    child_res.service_name,
    child.kind;

-- Service map with performance metrics.
CREATE OR REPLACE VIEW service_map_detailed AS
SELECT
    parent_res.service_name                              AS parent_service,
    child_res.service_name                              AS child_service,
    child.kind                                                                     AS span_kind,
    COUNT(*)                                                                       AS call_count,
    AVG(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS avg_duration_ms,
    MIN(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS min_duration_ms,
    MAX(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000           AS max_duration_ms,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END)                   AS error_count,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END) / COUNT(*) * 100  AS error_rate
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    parent_res.service_name IS NOT NULL
    AND child_res.service_name IS NOT NULL
    AND parent_res.service_name <>
        child_res.service_name
GROUP BY
    parent_res.service_name,
    child_res.service_name,
    child.kind;

-- Log severity distribution by day.
-- SQL Server used a regular view; MySQL does the same (computed on demand).
-- The day bucket integer-divides nanoseconds down to whole days since epoch (DIV, since MySQL '/'
-- is floating-point), then converts back to a DATE via DATE_ADD.
CREATE OR REPLACE VIEW log_severity_stats AS
WITH bucketed AS (
    SELECT
        severity_text,
        severity_number,
        (time_unix_nano DIV 1000000000 DIV 86400) AS day_bucket
    FROM log_records
    WHERE time_unix_nano > 0
)
SELECT
    severity_text,
    severity_number,
    COUNT(*)                                       AS count,
    DATE_ADD('1970-01-01', INTERVAL day_bucket DAY) AS log_date
FROM bucketed
GROUP BY severity_text, severity_number, day_bucket;

-- =============================================================================
-- 7. SCHEMA VERSION -- recorded LAST, one row per historical milestone this script passes through
-- =============================================================================
-- Recorded after every structural change and backfill above has succeeded, so a partial/failed
-- run never records a version the apply-schema.sh gate -- or this script's own backfill guards in
-- section 5 -- would wrongly treat as "already applied". ON DUPLICATE KEY UPDATE means a re-run
-- just refreshes applied_at for versions already recorded; it never re-triggers section 5's
-- backfills, since those are gated on the value having existed BEFORE this section runs.

INSERT INTO schema_version (version, applied_at) VALUES ('2.12.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

INSERT INTO schema_version (version, applied_at) VALUES ('2.13.0', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

INSERT INTO schema_version (version, applied_at) VALUES ('2.13.1', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

INSERT INTO schema_version (version, applied_at) VALUES ('2.13.2', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

INSERT INTO schema_version (version, applied_at) VALUES ('2.13.3', CURRENT_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE applied_at = CURRENT_TIMESTAMP(6);

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all base tables (expect 19)
--    SELECT table_name FROM information_schema.tables
--    WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
--
-- 2) List all views (expect 4)
--    SELECT table_name FROM information_schema.views
--    WHERE table_schema = DATABASE() ORDER BY table_name;
--
-- 3) Verify schema version
--    SELECT * FROM schema_version ORDER BY version;
--
-- 4) Confirm no leftover helper procedures (expect zero rows -- both
--    _mysql_migrate_alters and _mysql_migrate_backfills are DROPped at the end of their
--    respective sections above)
--    SELECT routine_name FROM information_schema.routines
--    WHERE routine_schema = DATABASE() AND routine_type = 'PROCEDURE';
