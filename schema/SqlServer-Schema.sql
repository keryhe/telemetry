-- OpenTelemetry SQL Server Database Schema - PascalCase Column Names
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Designed for high performance and scalability with proper indexing

-- Drop and create database
USE master;
GO

IF EXISTS (SELECT name FROM sys.databases WHERE name = 'Telemetry')
BEGIN
    ALTER DATABASE Telemetry SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE Telemetry;
END
GO

CREATE DATABASE Telemetry
COLLATE Latin1_General_100_CI_AS_SC_UTF8;
GO

USE Telemetry;
GO

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

-- Resource represents the entity producing telemetry
CREATE TABLE resources (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ResourceHash CHAR(64) NOT NULL, -- SHA256 hash for deduplication
    SchemaUrl NVARCHAR(2048),
    CreatedAt DATETIME2 DEFAULT SYSDATETIME(),
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT UK_resource_hash UNIQUE (ResourceHash)
);
CREATE INDEX idx_created_at ON resources(CreatedAt);
GO

-- Instrumentation scope (library)
CREATE TABLE instrumentation_scopes (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(255) NOT NULL,
    Version NVARCHAR(255),
    SchemaUrl NVARCHAR(2048),
    ScopeHash CHAR(64) NOT NULL, -- for deduplication
    CreatedAt DATETIME2 DEFAULT SYSDATETIME(),
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT UK_scope_hash UNIQUE (ScopeHash)
);
CREATE INDEX idx_name_version ON instrumentation_scopes(Name, Version);
GO

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans
CREATE TABLE spans (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    TraceId CHAR(32) NOT NULL, -- 128-bit trace ID
    SpanId CHAR(16) NOT NULL,   -- 64-bit span ID
    ParentSpanId CHAR(16),      -- 64-bit parent span ID
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    Name NVARCHAR(255) NOT NULL,
    Kind NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (Kind IN ('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER')),
    StartTimeUnixNano BIGINT NOT NULL, -- nanoseconds since Unix epoch
    EndTimeUnixNano BIGINT NOT NULL,
    DroppedAttributesCount INT DEFAULT 0,
    DroppedEventsCount INT DEFAULT 0,
    DroppedLinksCount INT DEFAULT 0,
    TraceState NVARCHAR(MAX), -- W3C trace state
    StatusCode NVARCHAR(20) NOT NULL DEFAULT 'UNSET'
        CHECK (StatusCode IN ('UNSET', 'OK', 'ERROR')),
    StatusMessage NVARCHAR(MAX),
    CreatedAt DATETIME2 DEFAULT SYSDATETIME(),
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_spans_resources FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    CONSTRAINT FK_spans_scopes FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id),
    CONSTRAINT UK_trace_span UNIQUE (TraceId, SpanId)
);
CREATE INDEX idx_trace_id ON spans(TraceId);
CREATE INDEX idx_span_id ON spans(SpanId);
CREATE INDEX idx_parent_span ON spans(ParentSpanId);
CREATE INDEX idx_start_time ON spans(StartTimeUnixNano);
CREATE INDEX idx_end_time ON spans(EndTimeUnixNano);
CREATE INDEX idx_duration ON spans(StartTimeUnixNano, EndTimeUnixNano);
CREATE INDEX idx_name ON spans(Name);
CREATE INDEX idx_kind ON spans(Kind);
CREATE INDEX idx_status ON spans(StatusCode);
CREATE INDEX idx_resource_time ON spans(ResourceId, StartTimeUnixNano);
GO

-- Span events
CREATE TABLE span_events (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    SpanId BIGINT NOT NULL,
    Name NVARCHAR(255) NOT NULL,
    TimeUnixNano BIGINT NOT NULL,
    DroppedAttributesCount INT DEFAULT 0,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_span_events_spans FOREIGN KEY (SpanId) REFERENCES spans(Id) ON DELETE CASCADE
);
CREATE INDEX idx_span_time ON span_events(SpanId, TimeUnixNano);
GO

-- Span links
CREATE TABLE span_links (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    SpanId BIGINT NOT NULL,
    LinkedTraceId CHAR(32) NOT NULL,
    LinkedSpanId CHAR(16) NOT NULL,
    TraceState NVARCHAR(MAX),
    DroppedAttributesCount INT DEFAULT 0,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_span_links_spans FOREIGN KEY (SpanId) REFERENCES spans(Id) ON DELETE CASCADE
);
CREATE INDEX idx_span_link ON span_links(SpanId, LinkedTraceId, LinkedSpanId);
GO

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table
CREATE TABLE metrics (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    Name NVARCHAR(255) NOT NULL,
    Description NVARCHAR(MAX),
    Unit NVARCHAR(63),
    Type NVARCHAR(30) NOT NULL
        CHECK (Type IN ('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY')),
    CreatedAt DATETIME2 DEFAULT SYSDATETIME(),
    CONSTRAINT FK_metrics_resources FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    CONSTRAINT FK_metrics_scopes FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id)
);
CREATE INDEX idx_name ON metrics(Name);
CREATE INDEX idx_type ON metrics(Type);
CREATE INDEX idx_resource_name ON metrics(ResourceId, Name);
GO

-- Gauge data points
CREATE TABLE gauge_data_points (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble FLOAT,
    ValueInt BIGINT,
    Flags INT DEFAULT 0,
    ExemplarId BIGINT,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_gauge_data_points_metrics FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE
);
CREATE INDEX idx_metric_time ON gauge_data_points(MetricId, TimeUnixNano);
CREATE INDEX idx_time ON gauge_data_points(TimeUnixNano);
GO

-- Sum data points
CREATE TABLE sum_data_points (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble FLOAT,
    ValueInt BIGINT,
    AggregationTemporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (AggregationTemporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    IsMonotonic BIT DEFAULT 0,
    Flags INT DEFAULT 0,
    ExemplarId BIGINT,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_sum_data_points_metrics FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE
);
CREATE INDEX idx_metric_time ON sum_data_points(MetricId, TimeUnixNano);
CREATE INDEX idx_temporality ON sum_data_points(AggregationTemporality);
GO

-- Histogram data points
CREATE TABLE histogram_data_points (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue FLOAT,
    BucketCounts NVARCHAR(MAX) CHECK (ISJSON(BucketCounts) = 1), -- Array of bucket counts
    ExplicitBounds NVARCHAR(MAX) CHECK (ISJSON(ExplicitBounds) = 1), -- Array of explicit bucket bounds
    AggregationTemporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (AggregationTemporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    Flags INT DEFAULT 0,
    Min_Value FLOAT,
    Max_Value FLOAT,
    ExemplarId BIGINT,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_histogram_data_points_metrics FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE
);
CREATE INDEX idx_metric_time ON histogram_data_points(MetricId, TimeUnixNano);
GO

-- Exponential histogram data points
CREATE TABLE exponential_histogram_data_points (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue FLOAT,
    Scale INT NOT NULL,
    ZeroCount BIGINT NOT NULL,
    PositiveOffset INT,
    PositiveBucketCounts NVARCHAR(MAX) CHECK (ISJSON(PositiveBucketCounts) = 1),
    NegativeOffset INT,
    NegativeBucketCounts NVARCHAR(MAX) CHECK (ISJSON(NegativeBucketCounts) = 1),
    AggregationTemporality NVARCHAR(20) NOT NULL DEFAULT 'UNSPECIFIED'
        CHECK (AggregationTemporality IN ('UNSPECIFIED', 'DELTA', 'CUMULATIVE')),
    Flags INT DEFAULT 0,
    Min_Value FLOAT,
    Max_Value FLOAT,
    ExemplarId BIGINT,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_exponential_histogram_data_points_metrics FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE
);
CREATE INDEX idx_metric_time ON exponential_histogram_data_points(MetricId, TimeUnixNano);
GO

-- Summary data points
CREATE TABLE summary_data_points (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue FLOAT NOT NULL,
    QuantileValues NVARCHAR(MAX) CHECK (ISJSON(QuantileValues) = 1), -- Array of {quantile, value} objects
    Flags INT DEFAULT 0,
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_summary_data_points_metrics FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE
);
CREATE INDEX idx_metric_time ON summary_data_points(MetricId, TimeUnixNano);
GO

-- Exemplars (for metrics)
CREATE TABLE exemplars (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    FilteredAttributes NVARCHAR(MAX) CHECK (ISJSON(FilteredAttributes) = 1), -- Key-value pairs as JSON
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble FLOAT,
    ValueInt BIGINT,
    SpanId CHAR(16),
    TraceId CHAR(32)
);
CREATE INDEX idx_time ON exemplars(TimeUnixNano);
CREATE INDEX idx_trace_span ON exemplars(TraceId, SpanId);
GO

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records
CREATE TABLE log_records (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    TimeUnixNano BIGINT, -- when the event occurred
    ObservedTimeUnixNano BIGINT, -- when the event was observed
    SeverityNumber INT, -- 1-24 based on standard levels
    SeverityText NVARCHAR(32), -- DEBUG, INFO, WARN, ERROR, etc.
    BodyType NVARCHAR(20) DEFAULT 'STRING'
        CHECK (BodyType IN ('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST')),
    BodyValue NVARCHAR(MAX), -- main log content
    DroppedAttributesCount INT DEFAULT 0,
    Flags INT DEFAULT 0,
    TraceId CHAR(32), -- trace context
    SpanId CHAR(16),   -- span context
    CreatedAt DATETIME2 DEFAULT SYSDATETIME(),
    AttributesJson NVARCHAR(MAX) CHECK (ISJSON(AttributesJson) = 1),
    CONSTRAINT FK_log_records_resources FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    CONSTRAINT FK_log_records_scopes FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id)
);
CREATE INDEX idx_time ON log_records(TimeUnixNano);
CREATE INDEX idx_observed_time ON log_records(ObservedTimeUnixNano);
CREATE INDEX idx_severity ON log_records(SeverityNumber);
CREATE INDEX idx_trace_span ON log_records(TraceId, SpanId);
CREATE INDEX idx_resource_time ON log_records(ResourceId, TimeUnixNano);
GO

-- Full-text index for log body (requires full-text search to be enabled)
-- Uncomment if full-text search is needed
/*
CREATE FULLTEXT CATALOG ftCatalog AS DEFAULT;
CREATE FULLTEXT INDEX ON log_records(BodyValue)
    KEY INDEX PK__log_reco__3214EC0701234567
    WITH STOPLIST = SYSTEM;
GO
*/

-- =============================================================================
-- UTILITY TABLES
-- =============================================================================

-- Schema version for migrations
CREATE TABLE schema_version (
    Version NVARCHAR(20) PRIMARY KEY,
    AppliedAt DATETIME2 DEFAULT SYSDATETIME()
);
GO

INSERT INTO schema_version (Version) VALUES ('1.0.0');
GO

-- =============================================================================
-- PERFORMANCE OPTIMIZATIONS
-- =============================================================================

-- Partitioning suggestions for large deployments
-- SQL Server uses partition functions and schemes
/*
-- Example: Partition spans by month
-- First, create a partition function
CREATE PARTITION FUNCTION pfSpansByMonth (BIGINT)
AS RANGE RIGHT FOR VALUES (
    1704067200000000000, -- 2024-01-01 in nanoseconds
    1706745600000000000, -- 2024-02-01
    1709251200000000000  -- 2024-03-01
    -- Add more boundaries as needed
);
GO

-- Create a partition scheme
CREATE PARTITION SCHEME psSpansByMonth
AS PARTITION pfSpansByMonth
ALL TO ([PRIMARY]);
GO

-- Apply to table (must be done during table creation or rebuild)
-- DROP TABLE spans;
-- CREATE TABLE spans (...) ON psSpansByMonth(StartTimeUnixNano);
*/

-- =============================================================================
-- USEFUL VIEWS
-- =============================================================================

-- Traces with resource information
GO
CREATE VIEW trace_summary AS
SELECT 
    CONVERT(NVARCHAR(32), s.TraceId) as TraceIdHex,
    s.TraceId,
    COUNT(*) as SpanCount,
    MIN(s.StartTimeUnixNano) as TraceStartTime,
    MAX(s.EndTimeUnixNano) as TraceEndTime,
    MAX(s.EndTimeUnixNano) - MIN(s.StartTimeUnixNano) as TraceDurationNs,
    r.Id as ResourceId
FROM spans s
JOIN resources r ON s.ResourceId = r.Id
GROUP BY s.TraceId, r.Id;
GO

-- Service Map View
-- Shows service-to-service relationships based on span parent-child relationships
-- Extracts service.name from Resource AttributesJson
CREATE VIEW service_map AS
SELECT DISTINCT
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"') AS ParentService,
    JSON_VALUE(child_res.AttributesJson, '$."service.name"') AS ChildService,
    child.Kind AS SpanKind,
    COUNT(*) AS CallCount
FROM spans child
INNER JOIN spans parent 
    ON child.ParentSpanId = parent.SpanId 
    AND child.TraceId = parent.TraceId
INNER JOIN resources parent_res 
    ON parent.ResourceId = parent_res.Id
INNER JOIN resources child_res 
    ON child.ResourceId = child_res.Id
WHERE 
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.AttributesJson, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.AttributesJson, '$."service.name"') != 
        JSON_VALUE(child_res.AttributesJson, '$."service.name"')
GROUP BY 
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"'),
    JSON_VALUE(child_res.AttributesJson, '$."service.name"'),
    child.Kind;
GO

-- Service Map with Additional Metrics
CREATE VIEW service_map_detailed AS
SELECT DISTINCT
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"') AS ParentService,
    JSON_VALUE(child_res.AttributesJson, '$."service.name"') AS ChildService,
    child.Kind AS SpanKind,
    COUNT(*) AS CallCount,
    AVG(CAST(child.EndTimeUnixNano - child.StartTimeUnixNano AS FLOAT)) / 1000000 AS AvgDurationMs,
    MIN(child.EndTimeUnixNano - child.StartTimeUnixNano) / 1000000 AS MinDurationMs,
    MAX(child.EndTimeUnixNano - child.StartTimeUnixNano) / 1000000 AS MaxDurationMs,
    SUM(CASE WHEN child.StatusCode = 'ERROR' THEN 1 ELSE 0 END) AS ErrorCount,
    (CAST(SUM(CASE WHEN child.StatusCode = 'ERROR' THEN 1 ELSE 0 END) AS FLOAT) / COUNT(*)) * 100 AS ErrorRate
FROM spans child
INNER JOIN spans parent 
    ON child.ParentSpanId = parent.SpanId 
    AND child.TraceId = parent.TraceId
INNER JOIN resources parent_res 
    ON parent.ResourceId = parent_res.Id
INNER JOIN resources child_res 
    ON child.ResourceId = child_res.Id
WHERE 
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(child_res.AttributesJson, '$."service.name"') IS NOT NULL
    AND JSON_VALUE(parent_res.AttributesJson, '$."service.name"') != 
        JSON_VALUE(child_res.AttributesJson, '$."service.name"')
GROUP BY 
    JSON_VALUE(parent_res.AttributesJson, '$."service.name"'),
    JSON_VALUE(child_res.AttributesJson, '$."service.name"'),
    child.Kind;
GO

-- Log severity distribution
CREATE VIEW log_severity_stats AS
SELECT 
    SeverityText,
    SeverityNumber,
    COUNT(*) as Count,
    CAST(DATEADD(SECOND, TimeUnixNano/1000000000, '1970-01-01') AS DATE) as LogDate
FROM log_records 
WHERE TimeUnixNano IS NOT NULL
GROUP BY SeverityText, SeverityNumber, CAST(DATEADD(SECOND, TimeUnixNano/1000000000, '1970-01-01') AS DATE);
GO

-- =============================================================================
-- NOTES
-- =============================================================================
/*
Key Conversion Notes from MySQL to SQL Server:

1. AUTO_INCREMENT → IDENTITY(1,1)
2. ENUM → NVARCHAR with CHECK constraints
3. TEXT → NVARCHAR(MAX)
4. TIMESTAMP → DATETIME2
5. CURRENT_TIMESTAMP → SYSDATETIME()
6. DOUBLE → FLOAT
7. BOOLEAN → BIT
8. JSON type → NVARCHAR(MAX) with JSON validation
9. VARCHAR → NVARCHAR for Unicode support
10. Index naming conventions adapted to SQL Server
11. Foreign key ON DELETE CASCADE syntax adjusted
12. JSON path syntax: ->>'$."key"' → JSON_VALUE(column, '$."key"')
13. UNIX_TIMESTAMP/FROM_UNIXTIME → DATEADD calculations
14. FULLTEXT index syntax differs significantly
15. GO batch separator added between statements
16. Partitioning uses partition functions and schemes

Key Design Decisions:
1. Normalized schema to reduce storage overhead and maintain referential integrity
2. Binary storage for trace/span IDs for space efficiency
3. Separate tables for each metric type to optimize storage and queries
4. JSON columns (as NVARCHAR(MAX)) for arrays/complex structures
5. Comprehensive indexing strategy for common query patterns
6. Support for all OTLP data types and structures
7. Partitioning ready for high-volume deployments
8. Views for common analytical queries
9. Column names in PascalCase for consistency
10. UTF-8 collation support via Latin1_General_100_CI_AS_SC_UTF8

Scaling Considerations:
1. Consider partitioning large tables by time using partition functions
2. Implement data retention policies with automated jobs
3. Use read-only replicas or Always On for analytical queries
4. Consider columnstore indexes for analytical workloads
5. Monitor and optimize index usage based on actual query patterns
6. Use page compression for large tables
7. Consider In-Memory OLTP for high-throughput scenarios

Data Types Supported:
- All OpenTelemetry attribute value types
- All metric types (Gauge, Sum, Histogram, ExponentialHistogram, Summary)
- Complete trace context and relationships
- Full log record structure with correlation
- Resource and instrumentation scope metadata
*/