using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace Keryhe.Telemetry.StressTests.Observers.Process;

/// <summary>
/// Reads a running .NET process's <c>System.Diagnostics.Metrics</c> from OUTSIDE it, over EventPipe
/// (stress-test plan, Phase 1/3) — the mechanism <c>dotnet-counters</c> uses, so the hosts need no
/// exporter or config change. Subscribes to the ingestion meter, the built-in ASP.NET Core / Kestrel /
/// gRPC meters and <c>System.Runtime</c>, one sample per instrument per second.
/// </summary>
public sealed class ProcessMetricsListener : IAsyncDisposable
{
    public static readonly string[] DefaultMeters =
    [
        "Keryhe.Telemetry.Ingestion",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Server.Kestrel",
        "Grpc.AspNetCore.Server",
        "System.Runtime"
    ];

    private readonly EventPipeSession _session;
    private readonly EventPipeEventSource _source;
    private readonly Task _processing;
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public MetricStore Store { get; } = new();

    /// <summary>Set if the event stream ended abnormally (a host that exited normally ends it cleanly).</summary>
    public Exception? Error { get; private set; }

    public ProcessMetricsListener(int processId, IEnumerable<string>? meters = null, int refreshSeconds = 1)
    {
        var args = new Dictionary<string, string>
        {
            ["SessionId"] = _sessionId,
            ["Metrics"] = string.Join(',', meters ?? DefaultMeters),
            ["RefreshInterval"] = refreshSeconds.ToString(),
            ["MaxTimeSeries"] = "2000",
            ["MaxHistograms"] = "200"
        };
        var provider = new EventPipeProvider("System.Diagnostics.Metrics", EventLevel.Informational, 0x3, args);

        _session = new DiagnosticsClient(processId).StartEventPipeSession([provider], requestRundown: false);
        _source = new EventPipeEventSource(_session.EventStream);
        _source.Dynamic.All += OnEvent;
        _processing = Task.Run(() =>
        {
            try { _source.Process(); }
            catch (Exception ex) { Error = ex; }
        });
    }

    private void OnEvent(TraceEvent e)
    {
        if (e.ProviderName != "System.Diagnostics.Metrics") return;
        var kind = e.EventName switch
        {
            "CounterRateValuePublished" => "counter",
            "UpDownCounterRateValuePublished" => "updowncounter",
            "GaugeValuePublished" => "gauge",
            "HistogramValuePublished" => "histogram",
            _ => null
        };
        if (kind is null) return;
        if (Convert.ToString(e.PayloadByName("sessionId")) != _sessionId) return;

        var meter = Convert.ToString(e.PayloadByName("meterName")) ?? "";
        var instrument = Convert.ToString(e.PayloadByName("instrumentName")) ?? "";
        var tags = ParseTags(Convert.ToString(e.PayloadByName("tags")));
        var at = new DateTimeOffset(e.TimeStamp.ToUniversalTime());

        MetricSample sample;
        if (kind == "histogram")
        {
            var quantiles = ParseQuantiles(Convert.ToString(e.PayloadByName("quantiles")));
            var count = e.PayloadNames.Contains("count") ? MetricStore.ParseDouble(e.PayloadByName("count")) : (double?)null;
            var sum = e.PayloadNames.Contains("sum") ? MetricStore.ParseDouble(e.PayloadByName("sum")) : (double?)null;
            sample = new MetricSample(at, meter, instrument, kind, tags,
                count is > 0 && sum is not null ? sum.Value / count.Value : double.NaN, count, sum,
                quantiles.GetValueOrDefault(0.5), quantiles.GetValueOrDefault(0.95), quantiles.GetValueOrDefault(0.99));
        }
        else
        {
            // Counters publish the per-interval increase ("rate"); gauges publish "lastValue"; an
            // up-down counter publishes both a per-interval change and its absolute "value", which is
            // the one that means something for an observable like the working set.
            var name = kind switch { "counter" => "rate", "updowncounter" => "value", _ => "lastValue" };
            sample = new MetricSample(at, meter, instrument, kind, tags, MetricStore.ParseDouble(e.PayloadByName(name)));
        }
        Store.Add(sample);
    }

    // "key=value,key2=value2"
    public static Dictionary<string, string> ParseTags(string? tags)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(tags)) return result;
        foreach (var part in tags.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    // "0.5=1.2;0.95=3.4;0.99=5.6"
    public static Dictionary<double, double> ParseQuantiles(string? quantiles)
    {
        var result = new Dictionary<double, double>();
        if (string.IsNullOrEmpty(quantiles)) return result;
        foreach (var part in quantiles.Split(';', ','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0
                && double.TryParse(part[..eq], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var q)
                && double.TryParse(part[(eq + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                result[q] = v;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try { _session.Stop(); } catch { /* the process may already be gone */ }
        await _processing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _source.Dispose();
        _session.Dispose();
    }
}
