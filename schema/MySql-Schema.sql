-- OpenTelemetry MySQL Database Schema - PascalCase Column Names
-- Supports OTLP logs, metrics, and traces as defined in opentelemetry-proto
-- Designed for high performance and scalability with proper indexing

SET foreign_key_checks = 0;
DROP DATABASE IF EXISTS opentelemetry;
CREATE DATABASE opentelemetry CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE opentelemetry;

-- =============================================================================
-- COMMON TABLES (shared across signals)
-- =============================================================================

-- Resource represents the entity producing telemetry
CREATE TABLE resources (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    ResourceHash CHAR(64) NOT NULL, -- SHA256 hash for deduplication
    SchemaUrl VARCHAR(2048),
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    AttributesJson JSON,
    UNIQUE KEY uk_resource_hash (ResourceHash),
    INDEX idx_created_at (CreatedAt)
) ENGINE=InnoDB;

-- Instrumentation scope (library)
CREATE TABLE instrumentation_scopes (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    Name VARCHAR(255) NOT NULL,
    Version VARCHAR(255),
    SchemaUrl VARCHAR(2048),
    ScopeHash CHAR(64) NOT NULL, -- for deduplication
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    AttributesJson JSON,
    UNIQUE KEY uk_scope_hash (ScopeHash),
    INDEX idx_name_version (Name, Version)
) ENGINE=InnoDB;

-- =============================================================================
-- TRACES TABLES
-- =============================================================================

-- Trace spans
CREATE TABLE spans (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    TraceId char(32) NOT NULL, -- 128-bit trace ID
    SpanId char(16) NOT NULL,   -- 64-bit span ID
    ParentSpanId char(16),      -- 64-bit parent span ID
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    Name VARCHAR(255) NOT NULL,
    Kind ENUM('UNSPECIFIED', 'INTERNAL', 'SERVER', 'CLIENT', 'PRODUCER', 'CONSUMER') NOT NULL DEFAULT 'UNSPECIFIED',
    StartTimeUnixNano BIGINT NOT NULL, -- nanoseconds since Unix epoch
    EndTimeUnixNano BIGINT NOT NULL,
    DroppedAttributesCount INT DEFAULT 0,
    DroppedEventsCount INT DEFAULT 0,
    DroppedLinksCount INT DEFAULT 0,
    TraceState TEXT, -- W3C trace state
    StatusCode ENUM('UNSET', 'OK', 'ERROR') NOT NULL DEFAULT 'UNSET',
    StatusMessage TEXT,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    AttributesJson JSON,
    FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id),
    UNIQUE KEY uk_trace_span (TraceId, SpanId),
    INDEX idx_trace_id (TraceId),
    INDEX idx_span_id (SpanId),
    INDEX idx_parent_span (ParentSpanId),
    INDEX idx_start_time (StartTimeUnixNano),
    INDEX idx_end_time (EndTimeUnixNano),
    INDEX idx_duration (StartTimeUnixNano, EndTimeUnixNano),
    INDEX idx_name (Name),
    INDEX idx_kind (Kind),
    INDEX idx_status (StatusCode),
    INDEX idx_resource_time (ResourceId, StartTimeUnixNano)
) ENGINE=InnoDB;

-- Span events
CREATE TABLE span_events (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    SpanId BIGINT NOT NULL,
    Name VARCHAR(255) NOT NULL,
    TimeUnixNano BIGINT NOT NULL,
    DroppedAttributesCount INT DEFAULT 0,
    AttributesJson JSON,
    FOREIGN KEY (SpanId) REFERENCES spans(Id) ON DELETE CASCADE,
    INDEX idx_span_time (SpanId, TimeUnixNano)
) ENGINE=InnoDB;

-- Span links
CREATE TABLE span_links (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    SpanId BIGINT NOT NULL,
    LinkedTraceId char(32) NOT NULL,
    LinkedSpanId char(16) NOT NULL,
    TraceState TEXT,
    DroppedAttributesCount INT DEFAULT 0,
    AttributesJson JSON,
    FOREIGN KEY (SpanId) REFERENCES spans(Id) ON DELETE CASCADE,
    INDEX idx_span_link (SpanId, LinkedTraceId, LinkedSpanId)
) ENGINE=InnoDB;

-- =============================================================================
-- METRICS TABLES
-- =============================================================================

-- Base metrics table
CREATE TABLE metrics (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    Name VARCHAR(255) NOT NULL,
    Description TEXT,
    Unit VARCHAR(63),
    Type ENUM('GAUGE', 'SUM', 'HISTOGRAM', 'EXPONENTIAL_HISTOGRAM', 'SUMMARY') NOT NULL,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id),
    INDEX idx_name (Name),
    INDEX idx_type (Type),
    INDEX idx_resource_name (ResourceId, Name)
) ENGINE=InnoDB;

-- Gauge data points
CREATE TABLE gauge_data_points (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble DOUBLE,
    ValueInt BIGINT,
    Flags INT DEFAULT 0,
    ExemplarId BIGINT,
    AttributesJson JSON,
    FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE,
    INDEX idx_metric_time (MetricId, TimeUnixNano),
    INDEX idx_time (TimeUnixNano)
) ENGINE=InnoDB;

-- Sum data points
CREATE TABLE sum_data_points (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble DOUBLE,
    ValueInt BIGINT,
    AggregationTemporality ENUM('UNSPECIFIED', 'DELTA', 'CUMULATIVE') NOT NULL DEFAULT 'UNSPECIFIED',
    IsMonotonic BOOLEAN DEFAULT FALSE,
    Flags INT DEFAULT 0,
    ExemplarId BIGINT,
    AttributesJson JSON,
    FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE,
    INDEX idx_metric_time (MetricId, TimeUnixNano),
    INDEX idx_temporality (AggregationTemporality)
) ENGINE=InnoDB;

-- Histogram data points
CREATE TABLE histogram_data_points (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue DOUBLE,
    BucketCounts JSON, -- Array of bucket counts
    ExplicitBounds JSON, -- Array of explicit bucket bounds
    AggregationTemporality ENUM('UNSPECIFIED', 'DELTA', 'CUMULATIVE') NOT NULL DEFAULT 'UNSPECIFIED',
    Flags INT DEFAULT 0,
    Min_Value DOUBLE,
    Max_Value DOUBLE,
    ExemplarId BIGINT,
    AttributesJson JSON,
    FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE,
    INDEX idx_metric_time (MetricId, TimeUnixNano)
) ENGINE=InnoDB;

-- Exponential histogram data points
CREATE TABLE exponential_histogram_data_points (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue DOUBLE,
    Scale INT NOT NULL,
    ZeroCount BIGINT NOT NULL,
    PositiveOffset INT,
    PositiveBucketCounts JSON,
    NegativeOffset INT,
    NegativeBucketCounts JSON,
    AggregationTemporality ENUM('UNSPECIFIED', 'DELTA', 'CUMULATIVE') NOT NULL DEFAULT 'UNSPECIFIED',
    Flags INT DEFAULT 0,
    Min_Value DOUBLE,
    Max_Value DOUBLE,
    ExemplarId BIGINT,
    AttributesJson JSON,
    FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE,
    INDEX idx_metric_time (MetricId, TimeUnixNano)
) ENGINE=InnoDB;

-- Summary data points
CREATE TABLE summary_data_points (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    MetricId BIGINT NOT NULL,
    StartTimeUnixNano BIGINT,
    TimeUnixNano BIGINT NOT NULL,
    Count BIGINT NOT NULL,
    SumValue DOUBLE NOT NULL,
    QuantileValues JSON, -- Array of {quantile, value} objects
    Flags INT DEFAULT 0,
    AttributesJson JSON,
    FOREIGN KEY (MetricId) REFERENCES metrics(Id) ON DELETE CASCADE,
    INDEX idx_metric_time (MetricId, TimeUnixNano)
) ENGINE=InnoDB;

-- Exemplars (for metrics)
CREATE TABLE exemplars (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    FilteredAttributes JSON, -- Key-value pairs as JSON
    TimeUnixNano BIGINT NOT NULL,
    ValueDouble DOUBLE,
    ValueInt BIGINT,
    SpanId char(16),
    TraceId char(32),
    INDEX idx_time (TimeUnixNano),
    INDEX idx_trace_span (TraceId, SpanId)
) ENGINE=InnoDB;

-- =============================================================================
-- LOGS TABLES
-- =============================================================================

-- Log records
CREATE TABLE log_records (
    Id BIGINT AUTO_INCREMENT PRIMARY KEY,
    ResourceId BIGINT NOT NULL,
    ScopeId BIGINT NOT NULL,
    TimeUnixNano BIGINT, -- when the event occurred
    ObservedTimeUnixNano BIGINT, -- when the event was observed
    SeverityNumber INT, -- 1-24 based on standard levels
    SeverityText VARCHAR(32), -- DEBUG, INFO, WARN, ERROR, etc.
    BodyType ENUM('STRING', 'BOOL', 'INT', 'DOUBLE', 'BYTES', 'ARRAY', 'KVLIST') DEFAULT 'STRING',
    BodyValue TEXT, -- main log content
    DroppedAttributesCount INT DEFAULT 0,
    Flags INT DEFAULT 0,
    TraceId char(32), -- trace context
    SpanId char(16),   -- span context
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    AttributesJson JSON,
    FOREIGN KEY (ResourceId) REFERENCES resources(Id),
    FOREIGN KEY (ScopeId) REFERENCES instrumentation_scopes(Id),
    INDEX idx_time (TimeUnixNano),
    INDEX idx_observed_time (ObservedTimeUnixNano),
    INDEX idx_severity (SeverityNumber),
    INDEX idx_trace_span (TraceId, SpanId),
    INDEX idx_resource_time (ResourceId, TimeUnixNano),
    INDEX idx_body_text (BodyValue(255)), -- For text search
    FULLTEXT idx_body_fulltext (BodyValue)
) ENGINE=InnoDB;

-- =============================================================================
-- UTILITY TABLES AND VIEWS
-- =============================================================================

-- Schema version for migrations
CREATE TABLE schema_version (
    Version VARCHAR(20) PRIMARY KEY,
    AppliedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO schema_version (Version) VALUES ('1.0.0');

-- =============================================================================
-- PERFORMANCE OPTIMIZATIONS
-- =============================================================================

-- Partitioning suggestions for large deployments (uncomment as needed)
-- Note: Partitioning should be applied based on your data retention and query patterns

/*
-- Partition spans by date (monthly partitions)
ALTER TABLE spans PARTITION BY RANGE (UNIX_TIMESTAMP(FROM_UNIXTIME(StartTimeUnixNano/1000000000))) (
    PARTITION p202401 VALUES LESS THAN (UNIX_TIMESTAMP('2024-02-01')),
    PARTITION p202402 VALUES LESS THAN (UNIX_TIMESTAMP('2024-03-01')),
    -- Add more partitions as needed
    PARTITION p_future VALUES LESS THAN MAXVALUE
);

-- Partition log records by date
ALTER TABLE log_records PARTITION BY RANGE (UNIX_TIMESTAMP(FROM_UNIXTIME(TimeUnixNano/1000000000))) (
    PARTITION p202401 VALUES LESS THAN (UNIX_TIMESTAMP('2024-02-01')),
    PARTITION p202402 VALUES LESS THAN (UNIX_TIMESTAMP('2024-03-01')),
    -- Add more partitions as needed
    PARTITION p_future VALUES LESS THAN MAXVALUE
);
*/

-- =============================================================================
-- USEFUL VIEWS
-- =============================================================================


-- Traces with resource information
CREATE VIEW trace_summary AS
SELECT 
    HEX(s.TraceId) as TraceIdHex,
    s.TraceId,
    COUNT(*) as SpanCount,
    MIN(s.StartTimeUnixNano) as TraceStartTime,
    MAX(s.EndTimeUnixNano) as TraceEndTime,
    MAX(s.EndTimeUnixNano) - MIN(s.StartTimeUnixNano) as TraceDurationNs,
    r.Id as ResourceId
FROM spans s
JOIN resources r ON s.ResourceId = r.Id
GROUP BY s.TraceId, r.Id;

-- Service Map View
-- Shows service-to-service relationships based on span parent-child relationships
-- Extracts service.name from Resource AttributesJson
CREATE VIEW service_map AS
SELECT DISTINCT
    parent_res.AttributesJson->>'$.\"service.name\"' AS ParentService,
    child_res.AttributesJson->>'$.\"service.name\"' AS ChildService,
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
    parent_res.AttributesJson->>'$.\"service.name\"' IS NOT NULL
    AND child_res.AttributesJson->>'$.\"service.name\"' IS NOT NULL
    AND parent_res.AttributesJson->>'$.\"service.name\"' != 
        child_res.AttributesJson->>'$.\"service.name\"'
GROUP BY 
    parent_res.AttributesJson->>'$.\"service.name\"',
    child_res.AttributesJson->>'$.\"service.name\"',
    child.Kind;

-- Service Map with Additional Metrics
CREATE VIEW service_map_detailed AS
SELECT DISTINCT
    parent_res.AttributesJson->>'$.\"service.name\"' AS ParentService,
    child_res.AttributesJson->>'$.\"service.name\"' AS ChildService,
    child.Kind AS SpanKind,
    COUNT(*) AS CallCount,
    AVG(child.EndTimeUnixNano - child.StartTimeUnixNano) / 1000000 AS AvgDurationMs,
    MIN(child.EndTimeUnixNano - child.StartTimeUnixNano) / 1000000 AS MinDurationMs,
    MAX(child.EndTimeUnixNano - child.StartTimeUnixNano) / 1000000 AS MaxDurationMs,
    SUM(CASE WHEN child.StatusCode = 'ERROR' THEN 1 ELSE 0 END) AS ErrorCount,
    (SUM(CASE WHEN child.StatusCode = 'ERROR' THEN 1 ELSE 0 END) / COUNT(*)) * 100 AS ErrorRate
FROM spans child
INNER JOIN spans parent 
    ON child.ParentSpanId = parent.SpanId 
    AND child.TraceId = parent.TraceId
INNER JOIN resources parent_res 
    ON parent.ResourceId = parent_res.Id
INNER JOIN resources child_res 
    ON child.ResourceId = child_res.Id
WHERE 
    parent_res.AttributesJson->>'$.\"service.name\"' IS NOT NULL
    AND child_res.AttributesJson->>'$.\"service.name\"' IS NOT NULL
    AND parent_res.AttributesJson->>'$.\"service.name\"' != 
        child_res.AttributesJson->>'$.\"service.name\"'
GROUP BY 
    parent_res.AttributesJson->>'$.\"service.name\"',
    child_res.AttributesJson->>'$.\"service.name\"',
    child.Kind;

-- Log severity distribution
CREATE VIEW log_severity_stats AS
SELECT 
    SeverityText,
    SeverityNumber,
    COUNT(*) as Count,
    DATE(FROM_UNIXTIME(TimeUnixNano/1000000000)) as LogDate
FROM log_records 
WHERE TimeUnixNano IS NOT NULL
GROUP BY SeverityText, SeverityNumber, DATE(FROM_UNIXTIME(TimeUnixNano/1000000000));

SET foreign_key_checks = 1;

-- =============================================================================
-- NOTES
-- =============================================================================
/*
Key Design Decisions:
1. Normalized schema to reduce storage overhead and maintain referential integrity
2. Binary storage for trace/span IDs for space efficiency
3. Separate tables for each metric type to optimize storage and queries
4. JSON columns for arrays/complex structures where appropriate
5. Comprehensive indexing strategy for common query patterns
6. Support for all OTLP data types and structures
7. Partitioning ready for high-volume deployments
8. Views for common analytical queries
9. Column names converted to PascalCase for consistency

Scaling Considerations:
1. Consider partitioning large tables by time
2. Implement data retention policies
3. Use read replicas for analytical queries
4. Consider column compression for large text fields
5. Monitor and optimize index usage based on actual query patterns

Data Types Supported:
- All OpenTelemetry attribute value types
- All metric types (Gauge, Sum, Histogram, ExponentialHistogram, Summary)
- Complete trace context and relationships
- Full log record structure with correlation
- Resource and instrumentation scope metadata
*/