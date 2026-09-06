-- OpenTelemetry SQL Server Schema
-- Translated from PostgreSQL-Schema.sql (TimescaleDB features removed)
--
-- Differences from PostgreSQL schema:
--   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY  ->  BIGINT IDENTITY(1,1) PRIMARY KEY
--   VARCHAR(n)                                       ->  NVARCHAR(n)
--   TEXT                                             ->  NVARCHAR(MAX)
--   DOUBLE PRECISION                                 ->  FLOAT
--   BOOLEAN                                          ->  BIT
--   TIMESTAMPTZ                                      ->  DATETIME2
--   NOW() / CURRENT_TIMESTAMP                        ->  SYSDATETIME()
--   JSONB                                            ->  NVARCHAR(MAX)
--   CHAR(n)                                          ->  CHAR(n)  (unchanged)
--   INTEGER                                          ->  INT
--   ON CONFLICT DO NOTHING                           ->  MERGE ... WHEN NOT MATCHED
--   RETURNING                                        ->  OUTPUT INSERTED.*
--   col ->> 'key'                                    ->  JSON_VALUE(col, '$.key')
--   GIN index on JSONB                               ->  omitted (no equivalent)
--   TimescaleDB hypertables / compression / retention ->  omitted entirely
--
-- Hypertable tables (gauge_data_points, sum_data_points, histogram_data_points,
-- exponential_histogram_data_points, summary_data_points, log_records) had no
-- PRIMARY KEY in PostgreSQL because TimescaleDB disallows unique constraints
-- that exclude the partition column.  In SQL Server they get a normal
-- BIGINT IDENTITY(1,1) PRIMARY KEY.
--
-- Usage:
--   sqlcmd -S <server> -d telemetry -i schema/SqlServer-Schema.sql

-- sqlcmd connects with SET QUOTED_IDENTIFIER OFF by default, but the filtered index
-- (idx_alert_rules_tenant_enabled) requires it ON — otherwise CREATE INDEX fails with
-- Msg 1934 and, because schema_version is already committed above the failure, the
-- apply-schema.sh version gate would wrongly report the schema as applied. Set it (and
-- ANSI_NULLS) ON for the whole session so the script applies cleanly regardless of
-- client. (SSMS / ADO.NET already default these ON.)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

CREATE TABLE tenants (
    id         BIGINT IDENTITY(1,1) PRIMARY KEY,
    name       NVARCHAR(255) NOT NULL,
    created_at DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT uk_tenant_name UNIQUE (name)
);

CREATE TABLE api_keys (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id    BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    key_hash     CHAR(64)      NOT NULL,
    name         NVARCHAR(255) NOT NULL,
    is_active    BIT           NOT NULL DEFAULT 1,
    created_at   DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_used_at DATETIME2,
    CONSTRAINT uk_api_key_hash UNIQUE (key_hash)
);
CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);
GO

-- Resource represents the entity producing telemetry.
CREATE TABLE resources (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    tenant_id       BIGINT        NOT NULL DEFAULT 1 REFERENCES tenants(id),
    resource_hash   CHAR(64)      NOT NULL,
    schema_url      NVARCHAR(2048),
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    CONSTRAINT uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash)
);
CREATE INDEX idx_resources_tenant_id ON resources (tenant_id);
CREATE INDEX idx_created_at ON resources (created_at);

-- PostgreSQL had a functional index on (attributes_json ->> 'service.name').
-- SQL Server equivalent requires a persisted computed column; omitted here.
-- Add one if filtering by service name at scale becomes a bottleneck:
--   ALTER TABLE resources ADD service_name AS JSON_VALUE(attributes_json, '$."service.name"') PERSISTED;
--   CREATE INDEX idx_resources_service_name ON resources (service_name);
GO

-- Instrumentation scope (library).
CREATE TABLE instrumentation_scopes (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    name            NVARCHAR(255) NOT NULL,
    version         NVARCHAR(255),
    schema_url      NVARCHAR(2048),
    scope_hash      CHAR(64)      NOT NULL,
    created_at      DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json NVARCHAR(MAX),
    CONSTRAINT uk_scope_hash UNIQUE (scope_hash)
);
CREATE INDEX idx_name_version ON instrumentation_scopes (name, version);
GO

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

CREATE TABLE spans (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    trace_id                 CHAR(32)      NOT NULL,
    span_id                  CHAR(16)      NOT NULL,
    parent_span_id           CHAR(16),
    resource_id              BIGINT        NOT NULL,
    scope_id                 BIGINT        NOT NULL,
    name                     NVARCHAR(255) NOT NULL,
    kind                     NVARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    start_time_unix_nano     BIGINT        NOT NULL,
    end_time_unix_nano       BIGINT        NOT NULL,
    dropped_attributes_count INT           DEFAULT 0,
    dropped_events_count     INT           DEFAULT 0,
    dropped_links_count      INT           DEFAULT 0,
    trace_state              NVARCHAR(MAX),
    flags                    INT           DEFAULT 0,
    status_code              NVARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK (status_code IN ('UNSET', 'OK', 'ERROR')),
    status_message           NVARCHAR(MAX),
    created_at               DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX),
    CONSTRAINT fk_spans_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    CONSTRAINT uk_trace_span      UNIQUE (trace_id, span_id)
);
CREATE INDEX idx_trace_id            ON spans (trace_id);
CREATE INDEX idx_span_id             ON spans (span_id);
CREATE INDEX idx_parent_span         ON spans (parent_span_id);
CREATE INDEX idx_spans_trace_parent  ON spans (trace_id, parent_span_id);
CREATE INDEX idx_start_time          ON spans (start_time_unix_nano DESC);
CREATE INDEX idx_end_time            ON spans (end_time_unix_nano DESC);
CREATE INDEX idx_duration            ON spans (start_time_unix_nano, end_time_unix_nano);
CREATE INDEX idx_spans_name          ON spans (name);
CREATE INDEX idx_kind                ON spans (kind);
CREATE INDEX idx_status              ON spans (status_code);
CREATE INDEX idx_spans_resource_time ON spans (resource_id, start_time_unix_nano DESC);
-- GIN index on attributes_json omitted: no SQL Server equivalent.
GO

-- Span events.
CREATE TABLE span_events (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    span_id                  BIGINT        NOT NULL,
    name                     NVARCHAR(255) NOT NULL,
    time_unix_nano           BIGINT        NOT NULL,
    dropped_attributes_count INT           DEFAULT 0,
    attributes_json          NVARCHAR(MAX),
    CONSTRAINT fk_span_events_spans FOREIGN KEY (span_id) REFERENCES spans (id) ON DELETE CASCADE
);
CREATE INDEX idx_span_time ON span_events (span_id, time_unix_nano);
GO

-- Span links.
CREATE TABLE span_links (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    span_id                  BIGINT   NOT NULL,
    linked_trace_id          CHAR(32) NOT NULL,
    linked_span_id           CHAR(16) NOT NULL,
    trace_state              NVARCHAR(MAX),
    flags                    INT      DEFAULT 0,
    dropped_attributes_count INT      DEFAULT 0,
    attributes_json          NVARCHAR(MAX),
    CONSTRAINT fk_span_links_spans FOREIGN KEY (span_id) REFERENCES spans (id) ON DELETE CASCADE
);
CREATE INDEX idx_span_link ON span_links (span_id, linked_trace_id, linked_span_id);
GO

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

CREATE TABLE metrics (
    id          BIGINT IDENTITY(1,1) PRIMARY KEY,
    resource_id BIGINT        NOT NULL,
    scope_id    BIGINT        NOT NULL,
    name        NVARCHAR(255) NOT NULL,
    description NVARCHAR(MAX),
    unit        NVARCHAR(63),
    -- [type] is bracketed because TYPE is a reserved word in T-SQL.
    [type]      NVARCHAR(30)  NOT NULL
        CHECK ([type] IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    created_at  DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id),
    -- Metric identity: one row per (resource, scope, name, type), not one row per OTLP
    -- export cycle. Resources and instrumentation_scopes dedup on an unbounded attribute map
    -- and therefore need a SHA-256 hash column; a metric is identified by bounded scalar
    -- columns that already exist here, so a plain composite UNIQUE is enough.
    --
    -- type is part of the key, not merely updated on conflict. The write path chooses which
    -- data-point table to insert into from the INCOMING type while the read path chooses which
    -- to read from the STORED type, so a metric that changes type mid-stream and matched an
    -- existing row would write points the reader would never look for. Keying on type makes
    -- such a change a new row instead: old points stay readable, new points are found.
    --
    -- Column order is deliberate. Leading with (resource_id, name, ...) makes the former
    -- idx_resource_name an exact redundant left prefix, so it is dropped below.
    -- Key bytes: 8 + 510 + 60 + 8 = 586, well inside the 1700-byte nonclustered limit.
    -- NOTE: under a case-insensitive database collation (the LocalDB default) this treats
    -- metric names differing only by case as the same metric, which PostgreSQL and
    -- ClickHouse do not. OTLP names are case-sensitive; no real instrumentation emits
    -- case-variant duplicates, so this is accepted rather than forced with a BIN2 collation.
    CONSTRAINT uk_metric_identity UNIQUE (resource_id, name, [type], scope_id)
);
CREATE INDEX idx_metrics_name  ON metrics (name);
CREATE INDEX idx_type          ON metrics ([type]);
-- idx_resource_name (resource_id, name) dropped in 2.7.0: now a left prefix of uk_metric_identity.
GO

-- Gauge data points.
-- PostgreSQL used a TimescaleDB hypertable (no PRIMARY KEY); SQL Server uses a normal table.
CREATE TABLE gauge_data_points (
    id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    value_double         FLOAT,
    value_int            BIGINT,
    flags                INT    DEFAULT 0,
    exemplar_id          BIGINT,
    attributes_json      NVARCHAR(MAX),
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_gauge_metric_time ON gauge_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points (time_unix_nano DESC);
GO

-- Sum data points.
CREATE TABLE sum_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    value_double            FLOAT,
    value_int               BIGINT,
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    is_monotonic            BIT          DEFAULT 0,
    flags                   INT          DEFAULT 0,
    exemplar_id             BIGINT,
    attributes_json         NVARCHAR(MAX),
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
-- Standalone time index added in 2.7.0: metric retention now deletes from the data-point
-- tables by time_unix_nano alone, which without this would full-scan.
CREATE INDEX idx_sum_metric_time ON sum_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_sum_time        ON sum_data_points (time_unix_nano DESC);
CREATE INDEX idx_temporality     ON sum_data_points (aggregation_temporality);
GO

-- Histogram data points.
CREATE TABLE histogram_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    bucket_counts           NVARCHAR(MAX),
    explicit_bounds         NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    exemplar_id             BIGINT,
    attributes_json         NVARCHAR(MAX),
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_histogram_metric_time ON histogram_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_histogram_time        ON histogram_data_points (time_unix_nano DESC);
GO

-- Exponential histogram data points.
CREATE TABLE exponential_histogram_data_points (
    id                      BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id               BIGINT       NOT NULL,
    start_time_unix_nano    BIGINT,
    time_unix_nano          BIGINT       NOT NULL,
    count                   BIGINT       NOT NULL,
    sum_value               FLOAT,
    scale                   INT          NOT NULL,
    zero_count              BIGINT       NOT NULL,
    positive_offset         INT,
    positive_bucket_counts  NVARCHAR(MAX),
    negative_offset         INT,
    negative_bucket_counts  NVARCHAR(MAX),
    aggregation_temporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (aggregation_temporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    flags                   INT          DEFAULT 0,
    min_value               FLOAT,
    max_value               FLOAT,
    exemplar_id             BIGINT,
    attributes_json         NVARCHAR(MAX),
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_exp_histogram_time        ON exponential_histogram_data_points (time_unix_nano DESC);
GO

-- Summary data points.
CREATE TABLE summary_data_points (
    id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
    metric_id            BIGINT NOT NULL,
    start_time_unix_nano BIGINT,
    time_unix_nano       BIGINT NOT NULL,
    count                BIGINT NOT NULL,
    sum_value            FLOAT  NOT NULL,
    quantile_values      NVARCHAR(MAX),
    flags                INT    DEFAULT 0,
    attributes_json      NVARCHAR(MAX),
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY (metric_id) REFERENCES metrics (id) ON DELETE CASCADE
);
CREATE INDEX idx_summary_metric_time ON summary_data_points (metric_id, time_unix_nano DESC);
CREATE INDEX idx_summary_time        ON summary_data_points (time_unix_nano DESC);
GO

-- Exemplars (regular table, soft-referenced by data point tables via exemplar_id).
CREATE TABLE exemplars (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    filtered_attributes NVARCHAR(MAX),
    time_unix_nano      BIGINT NOT NULL,
    value_double        FLOAT,
    value_int           BIGINT,
    span_id             CHAR(16),
    trace_id            CHAR(32)
);
CREATE INDEX idx_exemplar_time       ON exemplars (time_unix_nano);
CREATE INDEX idx_exemplar_trace_span ON exemplars (trace_id, span_id);
GO

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records.
-- PostgreSQL used a TimescaleDB hypertable on time_unix_nano; SQL Server uses a normal table.
-- Default 0 for time_unix_nano handles OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    id                       BIGINT IDENTITY(1,1) PRIMARY KEY,
    resource_id              BIGINT       NOT NULL,
    scope_id                 BIGINT       NOT NULL,
    time_unix_nano           BIGINT       NOT NULL DEFAULT 0,
    observed_time_unix_nano  BIGINT,
    severity_number          INT,
    severity_text            NVARCHAR(MAX),
    event_name               NVARCHAR(256),
    body_type                NVARCHAR(20) DEFAULT 'STRING'
        CHECK (body_type IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    body_value               NVARCHAR(MAX),
    dropped_attributes_count INT          DEFAULT 0,
    flags                    INT          DEFAULT 0,
    trace_id                 CHAR(32),
    span_id                  CHAR(16),
    created_at               DATETIME2    NOT NULL DEFAULT SYSDATETIME(),
    attributes_json          NVARCHAR(MAX),
    CONSTRAINT fk_log_records_resources FOREIGN KEY (resource_id) REFERENCES resources (id),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY (scope_id)    REFERENCES instrumentation_scopes (id)
);
CREATE INDEX idx_log_time          ON log_records (time_unix_nano          DESC);
CREATE INDEX idx_observed_time     ON log_records (observed_time_unix_nano  DESC);
CREATE INDEX idx_severity          ON log_records (severity_number);
CREATE INDEX idx_log_severity_time ON log_records (severity_number, time_unix_nano DESC);
CREATE INDEX idx_log_trace_span    ON log_records (trace_id, span_id);
CREATE INDEX idx_log_resource_time ON log_records (resource_id, time_unix_nano DESC);
-- GIN index on attributes_json omitted: no SQL Server equivalent.
GO

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    version    NVARCHAR(20) PRIMARY KEY,
    applied_at DATETIME2    NOT NULL DEFAULT SYSDATETIME()
);
GO
-- NOTE: the schema_version row is seeded at the very END of this script (after all
-- tables and views), so a partial/failed apply never records a version that the
-- apply-schema.sh version gate would wrongly treat as "already applied".

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    id               INT           IDENTITY(1,1) PRIMARY KEY,
    tenant_id        BIGINT        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    name             NVARCHAR(MAX) NOT NULL,
    -- [type] bracketed because TYPE is a reserved word in T-SQL.
    [type]           NVARCHAR(50)  NOT NULL,
    service_name     NVARCHAR(255),
    condition_json   NVARCHAR(MAX) NOT NULL,
    webhook_url      NVARCHAR(MAX) NOT NULL,
    cooldown_minutes INT           NOT NULL DEFAULT 60,
    enabled          BIT           NOT NULL DEFAULT 1,
    created_at       DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    last_fired_at    DATETIME2
);
CREATE INDEX idx_alert_rules_tenant_id      ON alert_rules (tenant_id);
-- SQL Server filtered index: WHERE enabled = 1  (BIT 1 = TRUE)
CREATE INDEX idx_alert_rules_tenant_enabled ON alert_rules (tenant_id, enabled) WHERE enabled = 1;
GO

CREATE TABLE alert_events (
    id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    rule_id      INT           NOT NULL,
    fired_at     DATETIME2     NOT NULL DEFAULT SYSDATETIME(),
    details_json NVARCHAR(MAX) NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY (rule_id) REFERENCES alert_rules (id) ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events (rule_id);
CREATE INDEX idx_alert_events_fired_at ON alert_events (fired_at DESC);
GO

-- =============================================================================
-- VIEWS
-- =============================================================================

DROP VIEW IF EXISTS log_severity_stats;
DROP VIEW IF EXISTS service_map_detailed;
DROP VIEW IF EXISTS service_map;
DROP VIEW IF EXISTS trace_summary;
GO

-- Trace summary: aggregated span counts and timing per trace.
CREATE VIEW trace_summary AS
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
GO

-- Service map: service-to-service call relationships extracted from span parent-child pairs.
-- JSON_VALUE replaces PostgreSQL's ->> operator.
CREATE VIEW service_map AS
SELECT
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') AS parent_service,
    JSON_VALUE(child_res.attributes_json,  '$."service.name"') AS child_service,
    child.kind                                               AS span_kind,
    COUNT(*)                                                 AS call_count
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.attributes_json,  '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.attributes_json, '$."service.name"') <>
        JSON_VALUE(child_res.attributes_json,  '$."service.name"')
GROUP BY
    JSON_VALUE(parent_res.attributes_json, '$."service.name"'),
    JSON_VALUE(child_res.attributes_json,  '$."service.name"'),
    child.kind;
GO

-- Service map with performance metrics.
CREATE VIEW service_map_detailed AS
SELECT
    JSON_VALUE(parent_res.attributes_json, '$."service.name"')                               AS parent_service,
    JSON_VALUE(child_res.attributes_json,  '$."service.name"')                               AS child_service,
    child.kind                                                                             AS span_kind,
    COUNT(*)                                                                               AS call_count,
    AVG(CAST(child.end_time_unix_nano - child.start_time_unix_nano AS FLOAT)) / 1000000   AS avg_duration_ms,
    MIN(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000                  AS min_duration_ms,
    MAX(child.end_time_unix_nano - child.start_time_unix_nano) / 1000000                  AS max_duration_ms,
    SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END)                          AS error_count,
    CAST(SUM(CASE WHEN child.status_code = 'ERROR' THEN 1 ELSE 0 END) AS FLOAT)
        / COUNT(*) * 100                                                                   AS error_rate
FROM spans child
INNER JOIN spans parent
    ON child.parent_span_id = parent.span_id
   AND child.trace_id       = parent.trace_id
INNER JOIN resources parent_res ON parent.resource_id = parent_res.id
INNER JOIN resources child_res  ON child.resource_id  = child_res.id
WHERE
    JSON_VALUE(parent_res.attributes_json, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.attributes_json,  '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.attributes_json, '$."service.name"') <>
        JSON_VALUE(child_res.attributes_json,  '$."service.name"')
GROUP BY
    JSON_VALUE(parent_res.attributes_json, '$."service.name"'),
    JSON_VALUE(child_res.attributes_json,  '$."service.name"'),
    child.kind;
GO

-- Log severity distribution by day.
-- PostgreSQL had a TimescaleDB continuous aggregate (pre-computed, refreshed every 5 minutes).
-- SQL Server uses a regular view (computed on demand).
-- The day bucket is computed by integer-dividing nanoseconds down to whole days since epoch,
-- then converting back to a DATE via DATEADD(DAY, ..., '1970-01-01').
CREATE VIEW log_severity_stats AS
WITH bucketed AS (
    SELECT
        severity_text,
        severity_number,
        CAST(time_unix_nano / 1000000000 / 86400 AS INT) AS day_bucket
    FROM log_records
    WHERE time_unix_nano > 0
)
SELECT
    severity_text,
    severity_number,
    COUNT(*)                                                       AS [count],
    CAST(DATEADD(DAY, day_bucket, '1970-01-01') AS DATE)          AS log_date
FROM bucketed
GROUP BY severity_text, severity_number, day_bucket;
GO

-- =============================================================================
-- SCHEMA VERSION (recorded LAST)
-- =============================================================================
-- Only reached when every statement above succeeded, so a partial apply cannot
-- leave a false version marker for the apply-schema.sh gate.
MERGE schema_version AS target
USING (VALUES (N'2.7.0')) AS src (version)
ON target.version = src.version
WHEN MATCHED     THEN UPDATE SET applied_at = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (version, applied_at) VALUES (src.version, SYSDATETIME());
GO

-- =============================================================================
-- POST-APPLY VERIFICATION (MANUAL SQL CHECKS)
-- =============================================================================
-- 1) List all user tables created
--    SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;
--
-- 2) List all indexes
--    SELECT i.name AS index_name, t.name AS table_name
--    FROM sys.indexes i
--    JOIN sys.tables t ON i.object_id = t.object_id
--    WHERE i.name IS NOT NULL
--    ORDER BY t.name, i.name;
--
-- 3) Verify schema version
--    SELECT * FROM schema_version;
--
-- 4) Verify default tenant
--    SELECT * FROM tenants;
