using System.Collections.Concurrent;
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

    /// <summary>This instance's meter, so a listener can tell it from another instance's in the same process (tests run several hosts at once).</summary>
    public Meter Meter => _meter;
    private readonly Counter<long> _recordsDropped;
    private readonly Histogram<double> _gateWait;
    private readonly Histogram<double> _flushDuration;
    private readonly Counter<long> _flushRetries;
    private readonly Counter<long> _recordsFlushed;
    private readonly Histogram<long> _flushBatchSize;
    private readonly Histogram<double> _commitLag;
    private readonly Counter<long> _authFailures;
    private readonly Counter<long> _recordsAccepted;
    private readonly Counter<long> _recordsRefused;
    private readonly Counter<long> _rollupRowsWritten;
    private readonly Counter<long> _rollupRowsDropped;
    private readonly Histogram<double> _rollupFlushDuration;

    // signal -> reader of that signal's gate's resident count; see RegisterResidentRecords.
    private readonly ConcurrentDictionary<string, Func<int>> _residentRecords = new();
    private readonly ConcurrentDictionary<string, Func<long>> _residentBytes = new();
    private readonly Counter<long> _recordsTruncated;
    private readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<long, int>>> _tenantResident = new();

    public IngestionMetrics()
    {
        _gateWait = _meter.CreateHistogram<double>(
            "keryhe.telemetry.ingestion.gate_wait",
            unit: "ms",
            description: "Time an export spent waiting for ingestion gate capacity before its records " +
                          "could be enqueued -- the backpressure signal. Tagged by signal.");
        _flushDuration = _meter.CreateHistogram<double>(
            "keryhe.telemetry.ingestion.flush_duration",
            unit: "ms",
            description: "Duration of one flush attempt against the database, tagged by signal and " +
                          "outcome (ok/failed).");
        _flushRetries = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.flush_retries",
            unit: "{retry}",
            description: "Flush retries after a failed attempt, tagged by signal.");
        _recordsFlushed = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_flushed",
            unit: "{record}",
            description: "Records successfully flushed to the database, tagged by signal.");
        _flushBatchSize = _meter.CreateHistogram<long>(
            "keryhe.telemetry.ingestion.flush_batch_size",
            unit: "{record}",
            description: "Merged batch size handed to each flush, tagged by signal.");
        _commitLag = _meter.CreateHistogram<double>(
            "keryhe.telemetry.ingestion.commit_lag",
            unit: "ms",
            description: "Time from an export being enqueued to the flush that persisted it committing, " +
                          "one measurement per export, tagged by signal. The write-path health signal: " +
                          "independent of any read query, it grows when the database cannot keep up.");
        _authFailures = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.auth_failures",
            unit: "{request}",
            description: "Collector requests rejected by API key authentication, tagged by signal and " +
                          "reason (missing, malformed, invalid, expired, unavailable).");
        _recordsAccepted = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_accepted",
            unit: "{record}",
            description: "Records enqueued for storage, tagged by signal and tenant.");
        _recordsRefused = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_refused",
            unit: "{record}",
            description: "Records in exports that were refused before being enqueued, tagged by signal, tenant and " +
                          "reason (throttled, shutting_down, invalid). Authentication failures are auth_failures.");
        _rollupRowsWritten = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.rollup_rows_written",
            unit: "{row}",
            description: "Partial summary-rollup rows appended, tagged by kind (request/log).");
        _rollupRowsDropped = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.rollup_rows_dropped",
            unit: "{row}",
            description: "Summary-rollup rows dropped (buffer full, or lost at the shutdown deadline), tagged by kind.");
        _rollupFlushDuration = _meter.CreateHistogram<double>(
            "keryhe.telemetry.ingestion.rollup_flush_duration",
            unit: "ms",
            description: "Duration of one rollup append cycle, tagged by outcome (ok/failed).");
        _meter.CreateObservableGauge(
            "keryhe.telemetry.ingestion.resident_records",
            ObserveResidentRecords,
            unit: "{record}",
            description: "Records currently held by each signal's ingestion gate (enqueued or in flight), tagged by signal.");

        _meter.CreateObservableGauge(
            "keryhe.telemetry.ingestion.resident_bytes",
            ObserveResidentBytes,
            unit: "By",
            description: "Protobuf bytes of the exports currently held by each signal's ingestion gate, tagged by signal (0 when the byte budget is off).");
        _meter.CreateObservableGauge(
            "keryhe.telemetry.ingestion.tenant_resident_records",
            ObserveTenantResident,
            unit: "{record}",
            description: "Records each tenant currently holds in each signal's ingestion queue, tagged by signal and tenant: who is holding the queue.");
        _recordsTruncated = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_truncated",
            unit: "{value}",
            description: "Values cut to fit a configured limit or a database column while converting an export, tagged by signal and " +
                          "limit (attributes, attribute_key, attribute_value, events, links, body, depth, elements, or a column such as span_name).");

        _recordsDropped = _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.records_dropped",
            unit: "{record}",
            description: "Records (log records, spans, or metrics) dropped from an ingestion " +
                          "flush after MaxFlushRetries was exhausted, tagged by signal and reason.");
    }

    /// <param name="signal">"logs", "traces", or "metrics".</param>
    /// <param name="count">
    /// Records dropped, in the same unit <see cref="TelemetryIngestionOptions"/> already
    /// measures that signal in -- log/metric records for "logs"/"metrics", SPANS (not traces)
    /// for "traces".
    /// </param>
    /// <param name="reason">
    /// Why: <c>retries_exhausted</c> (transient failures outlasted the retries), <c>permanent</c> (a record the
    /// database refuses, isolated by splitting the batch), <c>split_cap</c> (the split budget ran out),
    /// <c>out_of_retention</c> (ClickHouse, older than the retention window at ingest) or <c>shutdown</c>.
    /// </param>
    public void RecordDropped(string signal, int count, string reason)
    {
        if (count <= 0) return;
        _recordsDropped.Add(count, new KeyValuePair<string, object?>("signal", signal), new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>Records of one export enqueued for <paramref name="tenantId"/>.</summary>
    public void RecordAccepted(string signal, long tenantId, int count, string protocol = "grpc")
    {
        if (count <= 0) return;
        _recordsAccepted.Add(count, new KeyValuePair<string, object?>("signal", signal), new KeyValuePair<string, object?>("tenant", tenantId),
            new KeyValuePair<string, object?>("protocol", protocol));
    }

    /// <summary>Records of one export refused for <paramref name="tenantId"/> for <paramref name="reason"/> (see <see cref="RefusalReasons"/>).</summary>
    public void RecordRefused(string signal, long tenantId, string reason, int count, string protocol = "grpc")
    {
        if (count <= 0) return;
        _recordsRefused.Add(count, new KeyValuePair<string, object?>("signal", signal), new KeyValuePair<string, object?>("tenant", tenantId),
            new KeyValuePair<string, object?>("reason", reason), new KeyValuePair<string, object?>("protocol", protocol));
    }

    public void RecordGateWait(string signal, double milliseconds) =>
        _gateWait.Record(milliseconds, new KeyValuePair<string, object?>("signal", signal));

    /// <param name="outcome">"ok" or "failed".</param>
    public void RecordFlushDuration(string signal, double milliseconds, string outcome) =>
        _flushDuration.Record(milliseconds,
            new KeyValuePair<string, object?>("signal", signal),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <param name="signal">"logs", "traces", "metrics", or "unknown".</param>
    /// <param name="reason">missing, malformed, invalid, expired or unavailable.</param>
    public void RecordAuthFailure(string signal, string reason) =>
        _authFailures.Add(1,
            new KeyValuePair<string, object?>("signal", signal),
            new KeyValuePair<string, object?>("reason", reason));

    public void RecordFlushRetry(string signal) =>
        _flushRetries.Add(1, new KeyValuePair<string, object?>("signal", signal));

    public void RecordFlushed(string signal, int count)
    {
        if (count <= 0) return;
        _recordsFlushed.Add(count, new KeyValuePair<string, object?>("signal", signal));
    }

    /// <param name="milliseconds">Enqueue-to-commit time of one export.</param>
    public void RecordCommitLag(string signal, double milliseconds) =>
        _commitLag.Record(milliseconds, new KeyValuePair<string, object?>("signal", signal));

    /// <param name="kind">"request" or "log".</param>
    public void RecordRollupRowsWritten(string kind, int count)
    {
        if (count <= 0) return;
        _rollupRowsWritten.Add(count, new KeyValuePair<string, object?>("kind", kind));
    }

    /// <param name="kind">"request" or "log".</param>
    public void RecordRollupRowsDropped(string kind, int count)
    {
        if (count <= 0) return;
        _rollupRowsDropped.Add(count, new KeyValuePair<string, object?>("kind", kind));
    }

    /// <param name="outcome">"ok" or "failed".</param>
    public void RecordRollupFlushDuration(double milliseconds, string outcome) =>
        _rollupFlushDuration.Record(milliseconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void RecordFlushBatchSize(string signal, int count) =>
        _flushBatchSize.Record(count, new KeyValuePair<string, object?>("signal", signal));

    /// <summary>
    /// Registers how to read a signal's current resident-record count for the
    /// <c>resident_records</c> gauge. Called by <see cref="TelemetryIngestionChannel"/>, which owns
    /// the gates, so this class needs no reference to them and its lifetime stays independent.
    /// </summary>
    public void RegisterResidentRecords(string signal, Func<int> read) => _residentRecords[signal] = read;

    public void RegisterResidentBytes(string signal, Func<long> read) => _residentBytes[signal] = read;

    public void RegisterTenantResident(string signal, Func<IReadOnlyDictionary<long, int>> read) => _tenantResident[signal] = read;

    private IEnumerable<Measurement<long>> ObserveTenantResident()
    {
        foreach (var (signal, read) in _tenantResident)
            foreach (var (tenant, records) in read())
                yield return new Measurement<long>(records, new KeyValuePair<string, object?>("signal", signal), new KeyValuePair<string, object?>("tenant", tenant));
    }

    /// <summary>A value (or list) of <paramref name="signal"/> was cut by <paramref name="limit"/>; <paramref name="count"/> says how many.</summary>
    public void RecordTruncated(string signal, string limit, int count = 1)
    {
        if (count <= 0) return;
        _recordsTruncated.Add(count, new KeyValuePair<string, object?>("signal", signal), new KeyValuePair<string, object?>("limit", limit));
    }

    private IEnumerable<Measurement<long>> ObserveResidentBytes()
    {
        foreach (var (signal, read) in _residentBytes)
            yield return new Measurement<long>(read(), new KeyValuePair<string, object?>("signal", signal));
    }

    private IEnumerable<Measurement<long>> ObserveResidentRecords()
    {
        foreach (var (signal, read) in _residentRecords)
            yield return new Measurement<long>(read(), new KeyValuePair<string, object?>("signal", signal));
    }

    // signal -> reader of the records waiting in non-current day buffers; see RegisterLateBufferRecords.
    private readonly ConcurrentDictionary<string, Func<int>> _lateBufferRecords = new();
    private int _lateGaugeCreated;

    /// <summary>
    /// Registers how to read a signal's count of records waiting in non-current day buffers, for the
    /// <c>late_buffer_records</c> gauge (the ClickHouse worker only; the instrument is created on first use).
    /// </summary>
    public void RegisterLateBufferRecords(string signal, Func<int> read)
    {
        _lateBufferRecords[signal] = read;
        if (Interlocked.Exchange(ref _lateGaugeCreated, 1) == 0)
            _meter.CreateObservableGauge(
                "keryhe.telemetry.ingestion.late_buffer_records",
                () => _lateBufferRecords.Select(kv => new Measurement<long>(kv.Value(), new KeyValuePair<string, object?>("signal", kv.Key))).ToList(),
                unit: "{record}",
                description: "Records waiting in non-current day buffers (late data), tagged by signal.");
    }

    private Counter<long>? _derivedRowsDropped;

    /// <summary>
    /// Derived rows (ClickHouse's <c>trace_index</c>, rollups, catalog) lost because their insert still failed after
    /// the raw rows were stored, tagged by <c>table</c>. Created on first use.
    /// </summary>
    public void RecordDerivedRowsDropped(string table, int count)
    {
        if (count <= 0) return;
        (_derivedRowsDropped ??= _meter.CreateCounter<long>(
            "keryhe.telemetry.ingestion.derived_rows_dropped",
            unit: "{row}",
            description: "Derived rows lost after a failed derived insert, tagged by table.")).Add(count, new KeyValuePair<string, object?>("table", table));
    }

    public void Dispose() => _meter.Dispose();
}
