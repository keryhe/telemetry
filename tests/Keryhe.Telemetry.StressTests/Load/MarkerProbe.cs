using System.Diagnostics;
using System.Net;
using System.Text.Json;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>
/// One marker probe: how long a just-sent log record and span took to become visible through the API.
/// A lag of <c>null</c> means it never appeared within the timeout.
/// </summary>
public sealed record MarkerResult(int Sequence, DateTimeOffset SentAt, double? LogLagMs, double? TraceLagMs, string? Error);

/// <summary>
/// A probe still polling: sent, acknowledged, and not yet visible on at least one signal. A lag already known for the other signal is carried;
/// a null lag with its pending flag set means that signal is still being polled.
/// </summary>
public sealed record PendingMarker(int Sequence, DateTimeOffset SentAt, bool LogPending, double? LogLagMs, bool TracePending, double? TraceLagMs);

/// <summary>
/// Measures ingest-to-queryable lag (stress-test plan, Phase 2). Every interval it sends a log record
/// and a span carrying a unique marker for one tenant, then polls the API until each is visible; the
/// elapsed time from the start of the send is the lag. It goes through the API, not the database, so
/// it measures what a user would see: the log by free-text search on its body (the marker), the trace
/// by fetching its spans.
///
/// The two lags differ by design on some providers: list pages pin every query on <c>asOf</c>, and on
/// PostgreSQL/Timescale that pin is <c>NOW() - 5 seconds</c> (a transaction-start race guard, see
/// <c>DapperReadRepository.DatabaseClockNowExpr</c>), so a log sits ~5s behind ingestion in the list,
/// while a trace fetched by id is not pinned. Both are what a user sees, so both are reported.
///
/// A probe still polling when the run is cancelled is dropped, not recorded as a timeout.
/// </summary>
public sealed class MarkerProbe(
    OtlpExporter exporter,
    Topology topology,
    int tenantIndex,
    HttpClient api,
    TimeSpan interval,
    TimeSpan pollInterval,
    TimeSpan timeout)
{
    private static readonly Resource MarkerResource = BuildResource();

    private readonly List<MarkerResult> _results = [];
    private readonly Dictionary<int, PendingMarker> _pending = [];
    private int _sequence;

    public IReadOnlyList<MarkerResult> Results
    {
        get { lock (_results) return _results.ToList(); }
    }

    /// <summary>Probes whose export was acknowledged but which have not yet become visible on both signals.</summary>
    public IReadOnlyList<PendingMarker> Pending
    {
        get { lock (_results) return _pending.Values.ToList(); }
    }

    private static Resource BuildResource()
    {
        var r = new Resource();
        r.Attributes.Add(Topology.Str("service.name", "stress.marker"));
        return r;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var running = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var seq = ++_sequence;
                // Each probe polls concurrently, so a slow one never delays the next probe's send.
                running.RemoveAll(t => t.IsCompleted);
                running.Add(ProbeAsync(seq, ct));
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) { }
        await Task.WhenAll(running);
    }

    private async Task ProbeAsync(int sequence, CancellationToken ct)
    {
        var sentAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var marker = $"stress-marker-{Guid.NewGuid():N}";
        var traceId = Topology.RandomId(new Random(), 16);
        var traceIdHex = Convert.ToHexString(traceId.ToByteArray()).ToLowerInvariant();
        var tenant = topology.Tenants[tenantIndex];
        var nowNanos = TimeStamps.NowNanos();
        string? error = null;

        try
        {
            var logRequest = new ExportLogsServiceRequest();
            var rl = new ResourceLogs { Resource = MarkerResource };
            var scopeLogs = new ScopeLogs { Scope = topology.Scope };
            scopeLogs.LogRecords.Add(new LogRecord
            {
                TimeUnixNano = (ulong)nowNanos, ObservedTimeUnixNano = (ulong)nowNanos,
                SeverityNumber = SeverityNumber.Info, SeverityText = "INFO",
                Body = new AnyValue { StringValue = marker }
            });
            rl.ScopeLogs.Add(scopeLogs);
            logRequest.ResourceLogs.Add(rl);

            var traceRequest = new ExportTraceServiceRequest();
            var rs = new ResourceSpans { Resource = MarkerResource };
            var scopeSpans = new ScopeSpans { Scope = topology.Scope };
            var span = new Span
            {
                TraceId = traceId, SpanId = Topology.RandomId(new Random(), 8), Name = "stress.marker",
                Kind = Span.Types.SpanKind.Internal,
                StartTimeUnixNano = (ulong)nowNanos, EndTimeUnixNano = (ulong)(nowNanos + 1_000_000)
            };
            span.Attributes.Add(Topology.Str("stress.marker", marker));
            scopeSpans.Spans.Add(span);
            rs.ScopeSpans.Add(scopeSpans);
            traceRequest.ResourceSpans.Add(rs);

            // Markers go through the ledgered export path, so the correctness check counts them too.
            var logEntry = new[] { new LedgerEntry("log_records", RecordAge.Current, 1, false, false) };
            var spanEntry = new[] { new LedgerEntry("spans", RecordAge.Current, 1, false, true) };
            var sends = await Task.WhenAll(
                exporter.ExportLogsAsync(new Payload<ExportLogsServiceRequest>(logRequest, tenantIndex, 1, logEntry, false), false, CancellationToken.None),
                exporter.ExportTracesAsync(new Payload<ExportTraceServiceRequest>(traceRequest, tenantIndex, 1, spanEntry, false), false, CancellationToken.None));

            if (sends.Any(s => s.Outcome != ExportOutcome.Ok))
            {
                Add(new MarkerResult(sequence, sentAt, null, null, "export failed: " + string.Join(",", sends.Select(s => s.Status))));
                return;
            }

            lock (_results) _pending[sequence] = new PendingMarker(sequence, sentAt, true, null, true, null);
            var logTask = PollAsync(() => LogVisibleAsync(tenant.Id, marker, sentAt, ct), started, ct)
                .ContinueWith(t => { Update(sequence, p => p with { LogPending = false, LogLagMs = t.IsCompletedSuccessfully ? t.Result : null }); return t; }, TaskScheduler.Default).Unwrap();
            var traceTask = PollAsync(() => TraceVisibleAsync(tenant.Id, traceIdHex, ct), started, ct)
                .ContinueWith(t => { Update(sequence, p => p with { TracePending = false, TraceLagMs = t.IsCompletedSuccessfully ? t.Result : null }); return t; }, TaskScheduler.Default).Unwrap();
            await Task.WhenAll(logTask, traceTask);
            Add(new MarkerResult(sequence, sentAt, logTask.Result, traceTask.Result, null));
        }
        catch (OperationCanceledException) { lock (_results) _pending.Remove(sequence); }
        catch (Exception ex)
        {
            error = ex.Message;
            Add(new MarkerResult(sequence, sentAt, null, null, error));
        }
    }

    private void Add(MarkerResult result)
    {
        lock (_results)
        {
            _results.Add(result);
            _pending.Remove(result.Sequence);
        }
    }

    private void Update(int sequence, Func<PendingMarker, PendingMarker> change)
    {
        lock (_results)
            if (_pending.TryGetValue(sequence, out var p)) _pending[sequence] = change(p);
    }

    /// <summary>Polls until <paramref name="visible"/> is true; returns ms since <paramref name="started"/>, or null on timeout.</summary>
    private async Task<double?> PollAsync(Func<Task<bool>> visible, long started, CancellationToken ct)
    {
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (await visible())
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await Task.Delay(pollInterval, ct);
        }
        return null;
    }

    private async Task<bool> LogVisibleAsync(long tenantId, string marker, DateTimeOffset sentAt, CancellationToken ct)
    {
        var start = Uri.EscapeDataString(sentAt.AddMinutes(-1).UtcDateTime.ToString("o"));
        var end = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("o"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/logs/page?start={start}&end={end}&q={Uri.EscapeDataString(marker)}&size=1");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        using var response = await api.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0;
    }

    private async Task<bool> TraceVisibleAsync(long tenantId, string traceIdHex, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/traces/{traceIdHex}/spans");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        using var response = await api.SendAsync(request, ct);
        return response.StatusCode == HttpStatusCode.OK;
    }
}
