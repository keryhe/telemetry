# OpenTelemetry MySQL Storage Server

A high-performance storage backend for OpenTelemetry telemetry data (traces, metrics, and logs) using MySQL as the persistence layer.

## Overview

This server receives OpenTelemetry Protocol (OTLP) data and stores it in a MySQL database with a normalized, optimized schema. It provides a complete storage solution for observability data with support for all OpenTelemetry signal types.

## Features

- **Complete OTLP Support**: Handles traces, metrics, and logs as defined in opentelemetry-proto
- **Normalized Schema**: Efficient storage with proper relationships and deduplication
- **High Performance**: Optimized indexing strategy for common query patterns
- **All Metric Types**: Support for Gauge, Sum, Histogram, Exponential Histogram, and Summary metrics
- **Trace Correlation**: Links logs and metrics to traces via trace and span IDs
- **Resource Management**: Efficient resource and instrumentation scope handling with hash-based deduplication
- **Built-in Analytics**: Pre-configured views for service maps, trace summaries, and log analysis
- **Scalable Design**: Partition-ready for high-volume deployments

## Supported Signal Types

### Traces
- Spans with complete context (trace ID, span ID, parent relationships)
- Span events and links
- W3C trace state support
- Status codes and messages

### Metrics
- Gauge data points
- Sum data points (delta and cumulative)
- Histogram data points
- Exponential histogram data points
- Summary data points
- Exemplars with trace correlation

### Logs
- Structured log records
- Severity levels (1-24)
- Multiple body types (string, bool, int, double, bytes, array, kvlist)
- Trace and span correlation
- Full-text search capability

## Database Schema

The schema is designed with the following principles:

- **Normalization**: Reduces storage overhead and maintains referential integrity
- **Efficient Storage**: JSON for complex structures
- **Query Optimization**: Comprehensive indexing on common access patterns
- **PascalCase Naming**: Consistent column naming convention
- **Extensibility**: Easy to add custom attributes via JSON columns

### Key Tables

- `resources` - Entities producing telemetry (services, hosts, etc.)
- `instrumentation_scopes` - Library/instrumentation information
- `spans` - Trace span data
- `metrics` - Base metrics metadata
- `*_data_points` - Type-specific metric data
- `log_records` - Log entries with correlation

### Built-in Views

- `trace_summary` - Aggregated trace information
- `service_map` - Service-to-service relationships
- `service_map_detailed` - Service map with performance metrics
- `log_severity_stats` - Log severity distribution over time

## Prerequisites

- MySQL 8.0 or higher
- Database with UTF-8 support (`utf8mb4_unicode_ci` collation)



### Query Traces

```sql
-- Find all spans for a specific trace
SELECT * FROM spans WHERE TraceId = 'your-trace-id';

-- Get trace summary
SELECT * FROM trace_summary WHERE TraceIdHex = 'your-trace-id-hex';

-- Find slow operations
SELECT Name, AVG(EndTimeUnixNano - StartTimeUnixNano) / 1000000 AS AvgDurationMs
FROM spans
GROUP BY Name
HAVING AvgDurationMs > 1000
ORDER BY AvgDurationMs DESC;
```

### Query Metrics

```sql
-- Get latest gauge values
SELECT m.Name, g.ValueDouble, g.TimeUnixNano
FROM gauge_data_points g
JOIN metrics m ON g.MetricId = m.Id
ORDER BY g.TimeUnixNano DESC
LIMIT 100;
```

### Query Logs

```sql
-- Find error logs with trace context
SELECT BodyValue, TraceId, SpanId, TimeUnixNano
FROM log_records
WHERE SeverityText = 'ERROR'
  AND TraceId IS NOT NULL
ORDER BY TimeUnixNano DESC;

-- Full-text search in logs
SELECT * FROM log_records
WHERE MATCH(BodyValue) AGAINST('exception error' IN NATURAL LANGUAGE MODE);
```

### Service Map Analysis

```sql
-- View service dependencies
SELECT ParentService, ChildService, CallCount, AvgDurationMs, ErrorRate
FROM service_map_detailed
ORDER BY CallCount DESC;
```

## Performance Tuning

### Partitioning for Large Deployments

For high-volume environments, enable table partitioning:

```sql
-- Partition spans by month
ALTER TABLE spans PARTITION BY RANGE (UNIX_TIMESTAMP(FROM_UNIXTIME(StartTimeUnixNano/1000000000))) (
    PARTITION p202401 VALUES LESS THAN (UNIX_TIMESTAMP('2024-02-01')),
    PARTITION p202402 VALUES LESS THAN (UNIX_TIMESTAMP('2024-03-01')),
    -- Add partitions as needed
    PARTITION p_future VALUES LESS THAN MAXVALUE
);
```

### Index Optimization

Monitor and adjust indexes based on your query patterns:

```sql
-- Check index usage
SELECT * FROM sys.schema_unused_indexes WHERE object_schema = 'opentelemetry';
```

### Data Retention

Implement automated cleanup for old data:

```sql
-- Delete traces older than 30 days
DELETE FROM spans 
WHERE StartTimeUnixNano < UNIX_TIMESTAMP(DATE_SUB(NOW(), INTERVAL 30 DAY)) * 1000000000;
```

## Scaling Considerations

1. **Read Replicas**: Use MySQL read replicas for analytical queries
2. **Connection Pooling**: Configure appropriate connection pool sizes
3. **Batch Inserts**: Group incoming telemetry for bulk inserts
4. **Compression**: Enable InnoDB compression for large text fields
5. **Monitoring**: Track MySQL performance metrics (slow queries, connection count, buffer pool usage)

## Architecture

```
OpenTelemetry SDKs → OTLP Protocol → Storage Server → MySQL Database
                                                    ↓
                                          Analytics/Queries
```

## License

[MIT]

## Acknowledgments

Built according to the [OpenTelemetry Protocol Specification](https://github.com/open-telemetry/opentelemetry-proto)
