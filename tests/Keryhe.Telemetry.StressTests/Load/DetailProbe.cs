using System.Diagnostics;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>Trace-detail latency for one group of seeded traces, as the client saw it (request to the whole body read).</summary>
public sealed record DetailProbeResult(string Kind, int Spans, bool Hinted, int Runs, int Errors, double P50Ms, double P95Ms, double MaxMs, double AvgBytes);

/// <summary>
/// After the run, requests trace detail for the traces the seed step sent (old ones, and the large ones), each with and without the
/// start-time hint the trace list passes (<c>?start=</c>). The comparison of the two is the measure of the hint, as the report's
/// own line in <c>result.json</c> rather than an inference from route latencies that mix everything.
/// </summary>
public static class DetailProbe
{
    private const int HistoryTraces = 20;
    private const int RunsPerHistoryTrace = 3;
    private const int RunsPerLargeTrace = 5;

    public static async Task<IReadOnlyList<DetailProbeResult>> RunAsync(HttpClient api, HistorySeedResult seed, CancellationToken ct)
    {
        var results = new List<DetailProbeResult>();
        var history = seed.Traces.Where(t => t.Kind == "history").Take(HistoryTraces).ToList();
        foreach (var hinted in new[] { false, true })
        {
            if (history.Count > 0)
                results.Add(await MeasureAsync(api, "history", history, hinted, RunsPerHistoryTrace, ct));
            foreach (var large in seed.Traces.Where(t => t.Kind == "large"))
                results.Add(await MeasureAsync(api, "large", [large], hinted, RunsPerLargeTrace, ct));
        }
        return results;
    }

    private static async Task<DetailProbeResult> MeasureAsync(HttpClient api, string kind, IReadOnlyList<SeededTrace> traces, bool hinted, int runsPerTrace, CancellationToken ct)
    {
        var times = new List<double>();
        var errors = 0;
        long bytes = 0;
        foreach (var trace in traces)
        {
            var url = $"api/traces/{trace.TraceIdHex}/spans"
                      + (hinted ? $"?start={Uri.EscapeDataString(ToIso(trace.StartUnixNano))}&end={Uri.EscapeDataString(ToIso(trace.EndUnixNano))}" : "");
            for (var i = 0; i < runsPerTrace; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Tenant-Id", trace.TenantId.ToString());
                var started = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await api.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                    var body = await response.Content.ReadAsByteArrayAsync(ct);
                    if (!response.IsSuccessStatusCode) { errors++; continue; }
                    times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    bytes += body.Length;
                }
                catch (HttpRequestException) { errors++; }
            }
        }
        times.Sort();
        double Pct(double p) => times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)Math.Ceiling(p * times.Count) - 1)];
        return new DetailProbeResult(kind, traces.Count == 1 ? traces[0].Spans : traces.Sum(t => t.Spans) / traces.Count, hinted, times.Count + errors, errors,
            Pct(0.5), Pct(0.95), times.Count == 0 ? 0 : times[^1], times.Count == 0 ? 0 : (double)bytes / times.Count);
    }

    private static string ToIso(long unixNano) => DateTime.UnixEpoch.AddTicks(unixNano / 100).ToString("O");
}
