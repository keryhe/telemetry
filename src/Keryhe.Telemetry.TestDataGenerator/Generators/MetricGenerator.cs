using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Generates gauges, sums (monotonic + up/down), explicit histograms, and one exponential histogram
/// (http.request.duration_exp_ms, made exponential via an AddView in Program.cs). OTLP Summary is a
/// legacy type the .NET SDK cannot emit, so it is not produced here.
/// </summary>
public class MetricGenerator
{
    private readonly Meter _meter;
    private readonly Random _random;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private long _requestCount = 0;
    private long _bytesProcessed = 0;

    // Metric instruments
    private ObservableGauge<double>? _cpuUsage;
    private ObservableGauge<long>? _memoryUsage;
    private UpDownCounter<long>? _activeConnections;
    private Counter<long>? _requestCounter;
    private Histogram<double>? _requestDuration;
    private Histogram<double>? _requestDurationExp;
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

        // Same measurements as _requestDuration, but exported as a base-2 exponential histogram
        // (configured via AddView in Program.cs) to exercise the exponential-histogram path.
        _requestDurationExp = _meter.CreateHistogram<double>(
            "http.request.duration_exp_ms",
            unit: "ms",
            description: "HTTP request duration in milliseconds (exponential histogram)"
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

        // Slowly drifting latency baseline so the percentile/throughput charts aren't flat.
        var baselineMs = CurrentLatencyBaselineMs();

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

            // Record request duration (explicit + exponential histogram, same value/tags).
            // Right-skewed (lognormal-ish) around the drifting baseline, with a fatter tail for
            // errors, so p50/p95/p99 separate and the distribution has a realistic shape.
            var duration = NextLatencyMs(baselineMs * (statusCode == "200" ? 1.0 : 1.6));
            var durationTags = new TagList
            {
                { "endpoint", endpoint },
                { "method", method },
                { "status", statusCode },
            };
            _requestDuration?.Record(duration, durationTags);
            _requestDurationExp?.Record(duration, durationTags);

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

    /// <summary>
    /// Slowly drifting median latency (ms) so the time-series charts show movement rather than a
    /// flat line: a ~10-minute sine wave between ~0.6x and ~1.4x of a 120 ms center, plus mild jitter.
    /// </summary>
    private double CurrentLatencyBaselineMs()
    {
        var minutes = (DateTime.UtcNow - _startTime).TotalMinutes;
        var wave = 1.0 + 0.4 * Math.Sin(2 * Math.PI * minutes / 10.0);
        var jitter = 0.9 + _random.NextDouble() * 0.2; // 0.9–1.1
        return 120.0 * wave * jitter;
    }

    /// <summary>
    /// Draws a right-skewed (approximately lognormal) latency in ms around <paramref name="medianMs"/>,
    /// with ~3% slow-tail outliers, so p95/p99 pull away from p50 like real request latency.
    /// </summary>
    private double NextLatencyMs(double medianMs)
    {
        // Box–Muller standard normal, exponentiated → lognormal (right-skewed) shape.
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        const double sigma = 0.5; // log-space spread → controls tail heaviness
        var value = medianMs * Math.Exp(sigma * normal);

        // Occasional tail spikes (2x–6x) to fatten p99.
        if (_random.NextDouble() < 0.03)
            value *= 2.0 + _random.NextDouble() * 4.0;

        return Math.Max(1.0, value);
    }
}
