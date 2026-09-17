using System.Diagnostics.Metrics;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Process-lifetime <c>System.Diagnostics.Metrics</c> instruments for the ingestion write path.
/// A dedicated class rather than fields on <see cref="TelemetryIngestionWorker"/> so the
/// instrument's lifetime is independent of the worker and it can be exported by any standard
/// .NET metrics listener (<c>dotnet-counters</c>, an OpenTelemetry .NET SDK
/// <c>MeterProvider</c>, etc.) without depending on this platform's own ingestion pipeline —
/// deliberately: a batch failing to reach the database is exactly the case where this platform's
/// own OTLP path cannot be trusted to report it.
///
/// Replaces a log line as the only signal that ingestion is losing data. A dropped-batch log
/// entry is easy to miss in volume; a counter is something an operator can alert on.
/// </summary>
public sealed class IngestionMetrics : IDisposable
{
    private readonly Meter _meter = new("Keryhe.Telemetry.Ingestion", "1.0.0");
    private readonly Counter<long> _recordsDropped;

    public IngestionMetrics()
    {
        _recordsDropped = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_dropped",
            unit: "{record}",
            description: "Records (log records, spans, or metrics) dropped from an ingestion " +
                          "flush after MaxFlushRetries was exhausted, tagged by signal.");
    }

    /// <param name="signal">"logs", "traces", or "metrics".</param>
    /// <param name="count">
    /// Records dropped, in the same unit <see cref="TelemetryIngestionOptions"/> already
    /// measures that signal in -- log/metric records for "logs"/"metrics", SPANS (not traces)
    /// for "traces".
    /// </param>
    public void RecordDropped(string signal, int count)
    {
        if (count <= 0) return;
        _recordsDropped.Add(count, new KeyValuePair<string, object?>("signal", signal));
    }

    public void Dispose() => _meter.Dispose();
}
