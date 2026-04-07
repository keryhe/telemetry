using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Generates logs at various severity levels with optional trace correlation.
/// </summary>
public class LogGenerator
{
    private readonly ILogger<LogGenerator> _logger;
    private readonly Random _random;
    private readonly ActivitySource? _activitySource;

    private static readonly string[] LogMessages = new[]
    {
        "User login successful",
        "Database query executed",
        "Cache miss - fetching from database",
        "API request completed",
        "Configuration reloaded",
        "Scheduled job started",
        "Report generation in progress",
        "Email notification sent",
        "User session expired",
        "Data validation passed",
        "Backup process initiated",
        "Performance threshold exceeded",
        "Service health check passed",
        "Message queue processed",
        "File upload completed",
        "Authentication token issued",
        "User profile updated",
        "Payment transaction processed",
        "Inventory stock adjusted",
        "Order fulfillment initiated",
        "Shipping label generated",
        "Invoice created",
        "Customer notification sent",
        "Product catalog refreshed",
        "Search index rebuilt",
        "Cache entry evicted",
        "Rate limit reset",
        "Connection pool refreshed",
        "Database migration completed",
        "Feature flag evaluation",
        "Analytics event recorded",
        "Metrics snapshot collected",
        "Log aggregation batch processed",
        "Trace batch exported",
        "Resource quota updated",
        "Service endpoint registered",
        "Load balancer health check passed",
        "SSL certificate validated",
        "API documentation generated",
        "Schema validation passed",
        "Data transformation completed",
        "ETL pipeline executed",
        "Webhook delivery attempted",
        "Background job enqueued",
        "Task deadline approaching",
        "Subscription renewal processed",
        "License validation completed",
        "Audit log entry recorded",
        "Permission check passed",
        "Resource allocation successful",
        "Thread pool task queued",
        "Database connection pooled",
        "OAuth token refreshed",
        "CMS content published",
        "Workflow step completed",
        "Approval request created",
        "Notification preference updated",
        "Export job started",
        "Import operation completed",
        "Sync completed successfully",
        "Version check passed",
        "Dependency injection container built",
        "Middleware pipeline initialized",
        "Request intercepted and logged",
        "Response compression applied",
        "CORS headers validated",
        "Security policy enforced",
        "Timezone conversion applied",
        "Localization strings loaded",
        "Template rendered successfully",
        "View state serialized",
        "Session stored in cache",
        "User preference loaded",
        "Recommendation engine processed request",
        "Machine learning model inference completed",
        "Data aggregation pipeline finished",
        "Queue depth monitored",
        "Dead letter queue checked",
        "Retry mechanism activated",
        "Fallback service called",
        "Circuit breaker state changed",
        "Graceful degradation initiated"
    };

    private static readonly string[] ErrorMessages = new[]
    {
        "Failed to connect to database",
        "Invalid user credentials",
        "API rate limit exceeded",
        "Timeout waiting for response",
        "Resource allocation failed",
        "Configuration validation failed",
        "Data integrity check failed",
        "Payment processing failed",
        "Email delivery failed",
        "Network connection lost",
        "File system access denied",
        "Invalid JSON format in request",
        "Database transaction rollback occurred",
        "Memory allocation failed",
        "Thread pool exhaustion",
        "Stream reader exception",
        "XML parsing failed",
        "Encryption decryption error",
        "Foreign key constraint violation",
        "Deadlock detected in database",
        "Index out of bounds",
        "Null reference exception",
        "Division by zero error",
        "Stack overflow detected",
        "Heap corruption detected",
        "Service unavailable - maintenance",
        "Third-party API unreachable",
        "Certificate validation failed",
        "TLS handshake failed",
        "Message queue connection dropped",
        "Cache server unreachable",
        "Search index corrupt",
        "Backup restoration failed",
        "Disk space exhausted",
        "Maximum retry attempts exceeded",
        "Circuit breaker opened",
        "Unhealthy service instance detected",
        "Load balancer health check failed",
        "DNS resolution failed",
        "Port already in use",
        "Permission denied on system resource",
        "Unsupported data type encountered",
        "Schema mismatch detected",
        "Duplicate key violation",
        "Referential integrity violation",
        "Invalid state transition",
        "Concurrency conflict detected",
        "Session expired before completion",
        "Unauthorized access attempt"
    };

    private static readonly string[] WarningMessages = new[]
    {
        "High memory usage detected",
        "Slow query detected",
        "Retrying failed operation",
        "Deprecated API usage",
        "Low disk space warning",
        "High latency detected",
        "Connection pool exhaustion",
        "Certificate expiration approaching",
        "Database backup size exceeded threshold",
        "CPU utilization above 80%",
        "Garbage collection pause detected",
        "Thread count increasing rapidly",
        "Unhandled exception in background task",
        "Response time degradation detected",
        "Error rate above baseline",
        "Missing required configuration",
        "Deprecated library version in use",
        "Clock skew detected",
        "Suspicious access pattern detected",
        "API response time outlier",
        "Cache hit ratio below threshold",
        "Database connection timeout approaching",
        "Request queue length increasing",
        "Unusual user agent detected",
        "Large payload detected",
        "Bulk operation in progress",
        "Stream not closed properly",
        "Resource leak detected",
        "Potential SQL injection attempt blocked",
        "Cross-site scripting attempt blocked",
        "Duplicate request detected",
        "Message delivery delayed",
        "Partial data returned from query",
        "Service degradation mode enabled",
        "Fallback mechanism activated",
        "Alternative data source being used",
        "Timeout approaching for operation",
        "High queue latency detected",
        "Index fragmentation above threshold",
        "Transaction lock wait detected",
        "Non-optimal query execution path",
        "External service degradation detected",
        "Client version mismatch warning",
        "Recommended software update available",
        "License expiration approaching"
    };

    public LogGenerator(ILogger<LogGenerator> logger, ActivitySource? activitySource = null)
    {
        _logger = logger;
        _activitySource = activitySource;
        _random = new Random();
    }

    /// <summary>
    /// Generate a batch of logs with mixed severity levels.
    /// </summary>
    public void GenerateBatch(int logCount)
    {
        for (int i = 0; i < logCount; i++)
        {
            var severityRoll = _random.Next(0, 100);
            
            if (severityRoll < 60)
            {
                LogInformation();
            }
            else if (severityRoll < 85)
            {
                LogWarning();
            }
            else
            {
                LogError();
            }
        }
    }

    private void LogInformation()
    {
        var message = LogMessages[_random.Next(LogMessages.Length)];
        var userId = $"user_{_random.Next(100, 200)}";
        var duration = _random.Next(10, 500);

        using var activity = StartLogActivity("log.info", "info", message);

        _logger.LogInformation(
            "Operation: {Message} | User: {UserId} | Duration: {Duration}ms | Timestamp: {Timestamp}",
            message,
            userId,
            duration,
            DateTime.UtcNow
        );
    }

    private void LogWarning()
    {
        var message = WarningMessages[_random.Next(WarningMessages.Length)];
        var component = new[] { "database", "api", "cache", "queue", "filesystem" }[_random.Next(5)];
        var value = _random.Next(50, 100);

        using var activity = StartLogActivity("log.warning", "warning", message);
        activity?.SetTag("component", component);

        _logger.LogWarning(
            "Warning: {Message} | Component: {Component} | Value: {Value}% | Timestamp: {Timestamp}",
            message,
            component,
            value,
            DateTime.UtcNow
        );
    }

    private void LogError()
    {
        var message = ErrorMessages[_random.Next(ErrorMessages.Length)];
        var errorCode = $"ERR_{_random.Next(1000, 9999)}";
        var retryCount = _random.Next(1, 4);

        using var activity = StartLogActivity("log.error", "error", message);
        activity?.SetTag("error.code", errorCode);
        activity?.SetTag("error.retry_count", retryCount);

        _logger.LogError(
            "Error: {Message} | Code: {ErrorCode} | Retry Count: {RetryCount} | Timestamp: {Timestamp}",
            message,
            errorCode,
            retryCount,
            DateTime.UtcNow
        );
    }

    private Activity? StartLogActivity(string name, string severity, string message)
    {
        var activity = _activitySource?.StartActivity(name, ActivityKind.Internal);
        if (activity == null)
            return null;

        activity.SetTag("otel.signal", "log");
        activity.SetTag("log.severity", severity);
        activity.SetTag("log.message", message);

        return activity;
    }
}
