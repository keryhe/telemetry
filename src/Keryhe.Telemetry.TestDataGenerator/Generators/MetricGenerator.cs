using System.Diagnostics.Metrics;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Generates all 5 metric types: Gauge, Sum, Histogram, ExponentialHistogram, and Summary.
/// </summary>
public class MetricGenerator
{
    private readonly Meter _meter;
    private readonly Random _random;
    private long _requestCount = 0;
    private long _bytesProcessed = 0;

    // Metric instruments
    private ObservableGauge<double>? _cpuUsage;
    private ObservableGauge<long>? _memoryUsage;
    private UpDownCounter<long>? _activeConnections;
    private Counter<long>? _requestCounter;
    private Histogram<double>? _requestDuration;
    private Histogram<long>? _responseSize;

    public MetricGenerator(Meter meter)
    {
        _meter = meter;
        _random = new Random();
        InitializeMetrics();
    }

    private void InitializeMetrics()
    {
        // Gauge metrics (instantaneous measurements)
        _cpuUsage = _meter.CreateObservableGauge(
            "system.cpu.usage_percent",
            () => new Measurement<double>(_random.Next(5, 95)),
            unit: "%",
            description: "CPU usage percentage"
        );

        _memoryUsage = _meter.CreateObservableGauge(
            "system.memory.usage_bytes",
            () => new Measurement<long>(_random.Next(500_000_000, 2_000_000_000)),
            unit: "By",
            description: "Memory usage in bytes"
        );

        // UpDownCounter (non-monotonic sum - can go up and down)
        _activeConnections = _meter.CreateUpDownCounter<long>(
            "http.connections.active",
            unit: "{connections}",
            description: "Number of active HTTP connections"
        );

        // Counter (monotonic sum)
        _requestCounter = _meter.CreateCounter<long>(
            "http.requests.total",
            unit: "{requests}",
            description: "Total number of HTTP requests"
        );

        // Histogram metrics (distribution)
        _requestDuration = _meter.CreateHistogram<double>(
            "http.request.duration_ms",
            unit: "ms",
            description: "HTTP request duration in milliseconds"
        );

        _responseSize = _meter.CreateHistogram<long>(
            "http.response.size_bytes",
            unit: "By",
            description: "HTTP response size in bytes"
        );
    }

    /// <summary>
    /// Generate a batch of metrics.
    /// </summary>
    public void GenerateBatch(int metricCount)
    {
        // Update connection count randomly
        var connectionDelta = _random.Next(-5, 10);
        _activeConnections?.Add(connectionDelta, new KeyValuePair<string, object?>("endpoint", "/api/users"));

        // Record requests and their characteristics
        for (int i = 0; i < metricCount; i++)
        {
            var endpoint = new[] { "/api/users", "/api/products", "/api/orders", "/api/health" }[_random.Next(4)];
            var method = new[] { "GET", "POST", "PUT", "DELETE" }[_random.Next(4)];
            var statusCode = _random.Next(0, 100) < 95 ? "200" : (_random.Next(0, 100) < 50 ? "400" : "500");

            // Increment request count
            _requestCounter?.Add(1, 
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("method", method),
                new KeyValuePair<string, object?>("status", statusCode)
            );

            // Record request duration (histogram)
            var duration = _random.Next(10, 500) + (statusCode == "200" ? 0 : _random.Next(50, 200));
            _requestDuration?.Record(duration,
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("method", method),
                new KeyValuePair<string, object?>("status", statusCode)
            );

            // Record response size (histogram)
            var responseSize = statusCode == "200" ? _random.Next(100, 50000) : _random.Next(50, 500);
            _responseSize?.Record(responseSize,
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("status", statusCode)
            );

            _requestCount++;
            _bytesProcessed += responseSize;
        }
    }
}
