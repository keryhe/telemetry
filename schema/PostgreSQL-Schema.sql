-- OpenTelemetry PostgreSQL + TimescaleDB Schema
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Requires TimescaleDB extension (https://docs.timescale.com/install/latest/)
--
-- Column names use PascalCase (double-quoted) to match EF Core / Npgsql identifier generation.
-- Table names use snake_case as configured via ToTable() in OpenTelemetryDbContext.
--
-- Usage:
--   psql -U postgres -c "CREATE DATABASE telemetry;"
--   psql -U postgres -d telemetry -f PostgreSQL-Schema.sql

-- =============================================================================
-- EXTENSIONS
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

-- Resource represents the entity producing telemetry
CREATE TABLE resources (
    "Id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "ResourceHash"   CHAR(64)     NOT NULL,
    "SchemaUrl"      VARCHAR(2048),
    "CreatedAt"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "AttributesJson" JSONB,
    CONSTRAINT uk_resource_hash UNIQUE ("ResourceHash")
);
CREATE INDEX idx_created_at ON resources ("CreatedAt");

-- Instrumentation scope (library)
CREATE TABLE instrumentation_scopes (
    "Id"             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Name"           VARCHAR(255) NOT NULL,
    "Version"        VARCHAR(255),
    "SchemaUrl"      VARCHAR(2048),
    "ScopeHash"      CHAR(64)     NOT NULL,
    "CreatedAt"      TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "AttributesJson" JSONB,
    CONSTRAINT uk_scope_hash UNIQUE ("ScopeHash")
);
CREATE INDEX idx_name_version ON instrumentation_scopes ("Name", "Version");

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans: regular PostgreSQL table (not a hypertable).
-- span_events and span_links hold FK references to spans("Id"), which requires
-- a simple primary key. Use idx_start_time for time-range queries instead.
CREATE TABLE spans (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "TraceId"                CHAR(32)     NOT NULL,
    "SpanId"                 CHAR(16)     NOT NULL,
    "ParentSpanId"           CHAR(16),
    "ResourceId"             BIGINT       NOT NULL,
    "ScopeId"                BIGINT       NOT NULL,
    "Name"                   VARCHAR(255) NOT NULL,
    "Kind"                   VARCHAR(20)  NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("Kind" IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    "StartTimeUnixNano"      BIGINT       NOT NULL,
    "EndTimeUnixNano"        BIGINT       NOT NULL,
    "DroppedAttributesCount" INTEGER      DEFAULT 0,
    "DroppedEventsCount"     INTEGER      DEFAULT 0,
    "DroppedLinksCount"      INTEGER      DEFAULT 0,
    "TraceState"             TEXT,
    "StatusCode"             VARCHAR(20)  NOT NULL DEFAULT 'UNSET'
        CHECK ("StatusCode" IN ('UNSET', 'OK', 'ERROR')),
    "StatusMessage"          TEXT,
    "CreatedAt"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "AttributesJson"         JSONB,
    CONSTRAINT fk_spans_resources FOREIGN KEY ("ResourceId") REFERENCES resources ("Id"),
    CONSTRAINT fk_spans_scopes    FOREIGN KEY ("ScopeId")    REFERENCES instrumentation_scopes ("Id"),
    CONSTRAINT uk_trace_span      UNIQUE ("TraceId", "SpanId")
);
CREATE INDEX idx_trace_id           ON spans ("TraceId");
CREATE INDEX idx_span_id            ON spans ("SpanId");
CREATE INDEX idx_parent_span        ON spans ("ParentSpanId");
CREATE INDEX idx_start_time         ON spans ("StartTimeUnixNano" DESC);
CREATE INDEX idx_end_time           ON spans ("EndTimeUnixNano"   DESC);
CREATE INDEX idx_duration           ON spans ("StartTimeUnixNano", "EndTimeUnixNano");
CREATE INDEX idx_spans_name         ON spans ("Name");
CREATE INDEX idx_kind               ON spans ("Kind");
CREATE INDEX idx_status             ON spans ("StatusCode");
CREATE INDEX idx_spans_resource_time ON spans ("ResourceId", "StartTimeUnixNano" DESC);

-- Span events
CREATE TABLE span_events (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "SpanId"                 BIGINT       NOT NULL,
    "Name"                   VARCHAR(255) NOT NULL,
    "TimeUnixNano"           BIGINT       NOT NULL,
    "DroppedAttributesCount" INTEGER      DEFAULT 0,
    "AttributesJson"         JSONB,
    CONSTRAINT fk_span_events_spans FOREIGN KEY ("SpanId") REFERENCES spans ("Id") ON DELETE CASCADE
);
CREATE INDEX idx_span_time ON span_events ("SpanId", "TimeUnixNano");

-- Span links
CREATE TABLE span_links (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "SpanId"                 BIGINT    NOT NULL,
    "LinkedTraceId"          CHAR(32)  NOT NULL,
    "LinkedSpanId"           CHAR(16)  NOT NULL,
    "TraceState"             TEXT,
    "DroppedAttributesCount" INTEGER   DEFAULT 0,
    "AttributesJson"         JSONB,
    CONSTRAINT fk_span_links_spans FOREIGN KEY ("SpanId") REFERENCES spans ("Id") ON DELETE CASCADE
);
CREATE INDEX idx_span_link ON span_links ("SpanId", "LinkedTraceId", "LinkedSpanId");

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table (regular table  referenced by FK from data point tables)
CREATE TABLE metrics (
    "Id"          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "ResourceId"  BIGINT       NOT NULL,
    "ScopeId"     BIGINT       NOT NULL,
    "Name"        VARCHAR(255) NOT NULL,
    "Description" TEXT,
    "Unit"        VARCHAR(63),
    "Type"        VARCHAR(30)  NOT NULL
        CHECK ("Type" IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    "CreatedAt"   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_metrics_resources FOREIGN KEY ("ResourceId") REFERENCES resources ("Id"),
    CONSTRAINT fk_metrics_scopes    FOREIGN KEY ("ScopeId")    REFERENCES instrumentation_scopes ("Id")
);
CREATE INDEX idx_metrics_name  ON metrics ("Name");
CREATE INDEX idx_type          ON metrics ("Type");
CREATE INDEX idx_resource_name ON metrics ("ResourceId", "Name");

-- Gauge data points (TimescaleDB hypertable on TimeUnixNano)
-- No PRIMARY KEY: TimescaleDB requires unique constraints to include the partition
-- column; since nothing FK-references this table's Id, a DB-level PK is not needed.
CREATE TABLE gauge_data_points (
    "Id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "MetricId"          BIGINT           NOT NULL,
    "StartTimeUnixNano" BIGINT,
    "TimeUnixNano"      BIGINT           NOT NULL,
    "ValueDouble"       DOUBLE PRECISION,
    "ValueInt"          BIGINT,
    "Flags"             INTEGER          DEFAULT 0,
    "ExemplarId"        BIGINT,
    "AttributesJson"    JSONB,
    CONSTRAINT fk_gauge_data_points_metrics FOREIGN KEY ("MetricId") REFERENCES metrics ("Id") ON DELETE CASCADE
);
SELECT create_hypertable('gauge_data_points', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_gauge_metric_time ON gauge_data_points ("MetricId", "TimeUnixNano" DESC);
CREATE INDEX idx_gauge_time        ON gauge_data_points ("TimeUnixNano" DESC);

-- Sum data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE sum_data_points (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "MetricId"               BIGINT           NOT NULL,
    "StartTimeUnixNano"      BIGINT,
    "TimeUnixNano"           BIGINT           NOT NULL,
    "ValueDouble"            DOUBLE PRECISION,
    "ValueInt"               BIGINT,
    "AggregationTemporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("AggregationTemporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "IsMonotonic"            BOOLEAN          DEFAULT FALSE,
    "Flags"                  INTEGER          DEFAULT 0,
    "ExemplarId"             BIGINT,
    "AttributesJson"         JSONB,
    CONSTRAINT fk_sum_data_points_metrics FOREIGN KEY ("MetricId") REFERENCES metrics ("Id") ON DELETE CASCADE
);
SELECT create_hypertable('sum_data_points', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_sum_metric_time ON sum_data_points ("MetricId", "TimeUnixNano" DESC);
CREATE INDEX idx_temporality     ON sum_data_points ("AggregationTemporality");

-- Histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE histogram_data_points (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "MetricId"               BIGINT           NOT NULL,
    "StartTimeUnixNano"      BIGINT,
    "TimeUnixNano"           BIGINT           NOT NULL,
    "Count"                  BIGINT           NOT NULL,
    "SumValue"               DOUBLE PRECISION,
    "BucketCounts"           JSONB,
    "ExplicitBounds"         JSONB,
    "AggregationTemporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("AggregationTemporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "Flags"                  INTEGER          DEFAULT 0,
    "Min_Value"              DOUBLE PRECISION,
    "Max_Value"              DOUBLE PRECISION,
    "ExemplarId"             BIGINT,
    "AttributesJson"         JSONB,
    CONSTRAINT fk_histogram_data_points_metrics FOREIGN KEY ("MetricId") REFERENCES metrics ("Id") ON DELETE CASCADE
);
SELECT create_hypertable('histogram_data_points', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_histogram_metric_time ON histogram_data_points ("MetricId", "TimeUnixNano" DESC);

-- Exponential histogram data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE exponential_histogram_data_points (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "MetricId"               BIGINT           NOT NULL,
    "StartTimeUnixNano"      BIGINT,
    "TimeUnixNano"           BIGINT           NOT NULL,
    "Count"                  BIGINT           NOT NULL,
    "SumValue"               DOUBLE PRECISION,
    "Scale"                  INTEGER          NOT NULL,
    "ZeroCount"              BIGINT           NOT NULL,
    "PositiveOffset"         INTEGER,
    "PositiveBucketCounts"   JSONB,
    "NegativeOffset"         INTEGER,
    "NegativeBucketCounts"   JSONB,
    "AggregationTemporality" TEXT         NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK ("AggregationTemporality" IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    "Flags"                  INTEGER          DEFAULT 0,
    "Min_Value"              DOUBLE PRECISION,
    "Max_Value"              DOUBLE PRECISION,
    "ExemplarId"             BIGINT,
    "AttributesJson"         JSONB,
    CONSTRAINT fk_exponential_histogram_data_points_metrics FOREIGN KEY ("MetricId") REFERENCES metrics ("Id") ON DELETE CASCADE
);
SELECT create_hypertable('exponential_histogram_data_points', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_exp_histogram_metric_time ON exponential_histogram_data_points ("MetricId", "TimeUnixNano" DESC);

-- Summary data points (TimescaleDB hypertable on TimeUnixNano)
CREATE TABLE summary_data_points (
    "Id"                BIGINT GENERATED ALWAYS AS IDENTITY,
    "MetricId"          BIGINT           NOT NULL,
    "StartTimeUnixNano" BIGINT,
    "TimeUnixNano"      BIGINT           NOT NULL,
    "Count"             BIGINT           NOT NULL,
    "SumValue"          DOUBLE PRECISION NOT NULL,
    "QuantileValues"    JSONB,
    "Flags"             INTEGER          DEFAULT 0,
    "AttributesJson"    JSONB,
    CONSTRAINT fk_summary_data_points_metrics FOREIGN KEY ("MetricId") REFERENCES metrics ("Id") ON DELETE CASCADE
);
SELECT create_hypertable('summary_data_points', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_summary_metric_time ON summary_data_points ("MetricId", "TimeUnixNano" DESC);

-- Exemplars (for metrics  regular table, referenced by FK from data point tables)
CREATE TABLE exemplars (
    "Id"                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "FilteredAttributes" JSONB,
    "TimeUnixNano"       BIGINT NOT NULL,
    "ValueDouble"        DOUBLE PRECISION,
    "ValueInt"           BIGINT,
    "SpanId"             CHAR(16),
    "TraceId"            CHAR(32)
);
CREATE INDEX idx_exemplar_time       ON exemplars ("TimeUnixNano");
CREATE INDEX idx_exemplar_trace_span ON exemplars ("TraceId", "SpanId");

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records (TimescaleDB hypertable on TimeUnixNano)
-- TimeUnixNano is NOT NULL (required for hypertable partition column).
-- Default 0 handles any edge-case OTLP records where TimeUnixNano is absent.
CREATE TABLE log_records (
    "Id"                     BIGINT GENERATED ALWAYS AS IDENTITY,
    "ResourceId"             BIGINT       NOT NULL,
    "ScopeId"                BIGINT       NOT NULL,
    "TimeUnixNano"           BIGINT       NOT NULL DEFAULT 0,
    "ObservedTimeUnixNano"   BIGINT,
    "SeverityNumber"         INTEGER,
    "SeverityText"           TEXT,
    "BodyType"               TEXT         DEFAULT 'STRING'
        CHECK ("BodyType" IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    "BodyValue"              TEXT,
    "DroppedAttributesCount" INTEGER      DEFAULT 0,
    "Flags"                  INTEGER      DEFAULT 0,
    "TraceId"                CHAR(32),
    "SpanId"                 CHAR(16),
    "CreatedAt"              TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "AttributesJson"         JSONB,
    CONSTRAINT fk_log_records_resources FOREIGN KEY ("ResourceId") REFERENCES resources ("Id"),
    CONSTRAINT fk_log_records_scopes    FOREIGN KEY ("ScopeId")    REFERENCES instrumentation_scopes ("Id")
);
SELECT create_hypertable('log_records', 'TimeUnixNano',
    chunk_time_interval => 86400000000000
);
CREATE INDEX idx_log_time          ON log_records ("TimeUnixNano"         DESC);
CREATE INDEX idx_observed_time     ON log_records ("ObservedTimeUnixNano" DESC);
CREATE INDEX idx_severity          ON log_records ("SeverityNumber");
CREATE INDEX idx_log_trace_span    ON log_records ("TraceId", "SpanId");
CREATE INDEX idx_log_resource_time ON log_records ("ResourceId", "TimeUnixNano" DESC);

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

CREATE TABLE schema_version (
    "Version"   VARCHAR(20) PRIMARY KEY,
    "AppliedAt" TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO schema_version ("Version") VALUES ('2.0.0');

-- =============================================================================
-- ALERTING TABLES
-- =============================================================================

CREATE TABLE alert_rules (
    "Id"              INTEGER      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "Name"            TEXT         NOT NULL,
    "Type"            VARCHAR(50)  NOT NULL,
    "ServiceName"     VARCHAR(255),
    "ConditionJson"   JSONB        NOT NULL,
    "WebhookUrl"      TEXT         NOT NULL,
    "CooldownMinutes" INTEGER      NOT NULL DEFAULT 60,
    "Enabled"         BOOLEAN      NOT NULL DEFAULT TRUE,
    "CreatedAt"       TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "LastFiredAt"     TIMESTAMPTZ
);
CREATE INDEX idx_alert_rules_enabled ON alert_rules ("Enabled") WHERE "Enabled" = TRUE;

CREATE TABLE alert_events (
    "Id"          BIGINT       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    "RuleId"      INTEGER      NOT NULL,
    "FiredAt"     TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    "DetailsJson" JSONB        NOT NULL,
    CONSTRAINT fk_alert_events_alert_rules FOREIGN KEY ("RuleId") REFERENCES alert_rules ("Id") ON DELETE CASCADE
);
CREATE INDEX idx_alert_events_rule_id  ON alert_events ("RuleId");
CREATE INDEX idx_alert_events_fired_at ON alert_events ("FiredAt" DESC);

-- =============================================================================
-- VIEWS
-- =============================================================================

-- Trace summary: aggregated span counts and timing per trace
CREATE VIEW trace_summary AS
SELECT
    s."TraceId"::TEXT                                       AS "TraceIdHex",
    s."TraceId",
    COUNT(*)                                                AS "SpanCount",
    MIN(s."StartTimeUnixNano")                              AS "TraceStartTime",
    MAX(s."EndTimeUnixNano")                                AS "TraceEndTime",
    MAX(s."EndTimeUnixNano") - MIN(s."StartTimeUnixNano")   AS "TraceDurationNs",
    r."Id"                                                  AS "ResourceId"
FROM spans s
JOIN resources r ON s."ResourceId" = r."Id"
GROUP BY s."TraceId", r."Id";

-- Service map: service-to-service call relationships extracted from span parent-child pairs
CREATE VIEW service_map AS
SELECT DISTINCT
    parent_res."AttributesJson" ->> 'service.name'   AS "ParentService",
    child_res."AttributesJson"  ->> 'service.name'   AS "ChildService",
    child."Kind"                                     AS "SpanKind",
    COUNT(*)                                         AS "CallCount"
FROM spans child
INNER JOIN spans parent
    ON child."ParentSpanId" = parent."SpanId"
    AND child."TraceId"     = parent."TraceId"
INNER JOIN resources parent_res ON parent."ResourceId" = parent_res."Id"
INNER JOIN resources child_res  ON child."ResourceId"  = child_res."Id"
WHERE
    parent_res."AttributesJson" ->> 'service.name' IS NOT NULL
    AND child_res."AttributesJson"  ->> 'service.name' IS NOT NULL
    AND parent_res."AttributesJson" ->> 'service.name' <>
        child_res."AttributesJson"  ->> 'service.name'
GROUP BY
    parent_res."AttributesJson" ->> 'service.name',
    child_res."AttributesJson"  ->> 'service.name',
    child."Kind";

-- Service map with performance metrics
CREATE VIEW service_map_detailed AS
SELECT DISTINCT
    parent_res."AttributesJson" ->> 'service.name'   AS "ParentService",
    child_res."AttributesJson"  ->> 'service.name'   AS "ChildService",
    child."Kind"                                     AS "SpanKind",
    COUNT(*)                                         AS "CallCount",
    AVG(CAST(child."EndTimeUnixNano" - child."StartTimeUnixNano" AS DOUBLE PRECISION)) / 1000000 AS "AvgDurationMs",
    MIN(child."EndTimeUnixNano" - child."StartTimeUnixNano") / 1000000                            AS "MinDurationMs",
    MAX(child."EndTimeUnixNano" - child."StartTimeUnixNano") / 1000000                            AS "MaxDurationMs",
    SUM(CASE WHEN child."StatusCode" = 'ERROR' THEN 1 ELSE 0 END)                                AS "ErrorCount",
    (CAST(SUM(CASE WHEN child."StatusCode" = 'ERROR' THEN 1 ELSE 0 END) AS DOUBLE PRECISION)
        / COUNT(*)) * 100                                                                          AS "ErrorRate"
FROM spans child
INNER JOIN spans parent
    ON child."ParentSpanId" = parent."SpanId"
    AND child."TraceId"     = parent."TraceId"
INNER JOIN resources parent_res ON parent."ResourceId" = parent_res."Id"
INNER JOIN resources child_res  ON child."ResourceId"  = child_res."Id"
WHERE
    parent_res."AttributesJson" ->> 'service.name' IS NOT NULL
    AND child_res."AttributesJson"  ->> 'service.name' IS NOT NULL
    AND parent_res."AttributesJson" ->> 'service.name' <>
        child_res."AttributesJson"  ->> 'service.name'
GROUP BY
    parent_res."AttributesJson" ->> 'service.name',
    child_res."AttributesJson"  ->> 'service.name',
    child."Kind";

-- Log severity distribution by day
CREATE VIEW log_severity_stats AS
SELECT
    "SeverityText",
    "SeverityNumber",
    COUNT(*)                                              AS "Count",
    CAST(to_timestamp("TimeUnixNano" / 1000000000.0) AS DATE) AS "LogDate"
FROM log_records
WHERE "TimeUnixNano" > 0
GROUP BY
    "SeverityText",
    "SeverityNumber",
    CAST(to_timestamp("TimeUnixNano" / 1000000000.0) AS DATE);

-- =============================================================================
-- NOTES
-- =============================================================================
--
-- Key conversion notes from SQL Server to PostgreSQL + TimescaleDB:
--
-- 1.  BIGINT IDENTITY(1,1)   BIGINT GENERATED ALWAYS AS IDENTITY
-- 2.  NVARCHAR(n)            VARCHAR(n)  (PostgreSQL is Unicode by default)
-- 3.  NVARCHAR(MAX)          TEXT
-- 4.  FLOAT                  DOUBLE PRECISION
-- 5.  BIT                    BOOLEAN
-- 6.  DATETIME2              TIMESTAMPTZ
-- 7.  SYSDATETIME()          NOW()
-- 8.  ISJSON(col) = 1        Removed; JSONB type enforces valid JSON natively
-- 9.  JSON columns           JSONB for efficient operator-based querying
-- 10. JSON_VALUE(col, '$."key"')  col ->> 'key'
-- 11. DATEADD(SECOND, ns/1e9, '1970-01-01')  to_timestamp(ns / 1000000000.0)
-- 12. CONVERT(NVARCHAR, col)      col::TEXT
-- 13. CAST(x AS FLOAT)     CAST(x AS DOUBLE PRECISION)
-- 14. GO batch separator    Removed (not used in PostgreSQL)
-- 15. uk_trace_span         (TraceId, SpanId)  spans is a regular table;
--                            hypertable requirement was removed for spans
-- 16. Index names are globally unique (prefixed by table abbreviation where needed)
--
-- TimescaleDB hypertables (partitioned by TimeUnixNano, 1-day chunks):
--   log_records, gauge_data_points, sum_data_points, histogram_data_points,
--   exponential_histogram_data_points, summary_data_points
--
-- Why spans is NOT a hypertable:
--   span_events and span_links hold FK references to spans("Id"). TimescaleDB
--   requires all unique/PK constraints to include the partition column, which
--   would break these normalized FK relationships. spans uses idx_start_time
--   for time-range query performance instead.
--
-- Hypertable leaf tables have no PRIMARY KEY constraint (only GENERATED ALWAYS
-- AS IDENTITY). TimescaleDB disallows unique constraints that exclude the
-- partition column. Since nothing FK-references these tables by Id, a DB-level
-- PK is not needed. EF Core uses Id as the logical primary key and reads the
-- generated value via RETURNING on INSERT.
--
-- Scaling considerations:
-- 1. Adjust chunk_time_interval based on ingestion volume
--    (e.g., 1 hour = 3600000000000 ns for high-volume environments)
-- 2. Enable TimescaleDB compression after a retention window:
--    SELECT add_compression_policy('log_records', INTERVAL '7 days');
-- 3. Add data retention policies as needed:
--    SELECT add_retention_policy('log_records', INTERVAL '90 days');
-- 4. Consider continuous aggregates for pre-computed service map metrics

