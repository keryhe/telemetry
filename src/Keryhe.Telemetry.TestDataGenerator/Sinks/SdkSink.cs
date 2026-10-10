using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keryhe.Telemetry.TestDataGenerator.Model;

namespace Keryhe.Telemetry.TestDataGenerator.Sinks;

/// <summary>
/// The live sink: replays the simulated activity through the real OpenTelemetry SDK, one provider set per pod.
/// Spans are started with explicit start and end times (so a request does not have to take real time), and the
/// logs and measurements of each span are recorded while that span is the current Activity, which is how the
/// SDK attaches trace ids to logs and exemplars to histograms.
/// </summary>
public sealed class SdkSink : ISink
{
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly int _metricIntervalMs;
    private readonly Dictionary<string, PodTelemetry> _pods = [];

    /// <summary>Producer span contexts, so a consumer trace in a later chunk can still link to its producer.</summary>
    private readonly ConditionalWeakTable<SimSpan, StrongBox<ActivityContext>> _contexts = new();

    private readonly bool _http;

    public SdkSink(Uri endpoint, string apiKey, int metricIntervalSeconds, bool httpProtobuf = false)
    {
        _http = httpProtobuf;
        _endpoint = endpoint;
        _apiKey = apiKey;
        _metricIntervalMs = metricIntervalSeconds * 1000;
    }

    public int PodCount => _pods.Count;

    private PodTelemetry Pod(ServiceInstance instance)
    {
        if (!_pods.TryGetValue(instance.PodName, out var pod))
            _pods[instance.PodName] = pod = new PodTelemetry(instance, _endpoint, _apiKey, _metricIntervalMs, _http);
        return pod;
    }

    public Task WriteAsync(SimChunk chunk, CancellationToken cancellationToken)
    {
        foreach (var sample in chunk.Samples) Pod(sample.Instance).SetSample(sample);
        foreach (var log in chunk.BackgroundLogs) Pod(log.Instance).Log(log);

        foreach (var root in chunk.Traces)
        {
            Activity.Current = null;
            Replay(root, default);
        }
        Activity.Current = null;
        return Task.CompletedTask;
    }

    private void Replay(SimSpan span, ActivityContext parent)
    {
        var pod = Pod(span.Instance);
        var links = span.Links
            .Where(l => _contexts.TryGetValue(l.Target, out _))
            .Select(l => new ActivityLink(_contexts.TryGetValue(l.Target, out var c) ? c.Value : default, new ActivityTagsCollection(l.Attributes)))
            .ToList();

        var activity = pod.Source.StartActivity(
            span.Name, ToKind(span.Kind), parent, span.Attributes, links, span.Start);
        if (activity is null) return; // not sampled: its subtree is dropped with it

        Activity.Current = activity;
        if (span.Kind == SimSpanKind.Producer) _contexts.AddOrUpdate(span, new StrongBox<ActivityContext>(activity.Context));

        foreach (var e in span.Events)
            activity.AddEvent(new ActivityEvent(e.Name, e.Time, new ActivityTagsCollection(e.Attributes)));
        foreach (var log in span.Logs) pod.Log(log);
        foreach (var m in span.Measurements) pod.Record(m);

        foreach (var child in span.Children)
        {
            Replay(child, activity.Context);
            Activity.Current = activity;
        }

        if (span.Status == SimStatus.Error) activity.SetStatus(ActivityStatusCode.Error, span.StatusMessage);
        else if (span.Status == SimStatus.Ok) activity.SetStatus(ActivityStatusCode.Ok);
        activity.SetEndTime(span.End.UtcDateTime);
        activity.Stop();
    }

    private static ActivityKind ToKind(SimSpanKind kind) => kind switch
    {
        SimSpanKind.Server => ActivityKind.Server,
        SimSpanKind.Client => ActivityKind.Client,
        SimSpanKind.Producer => ActivityKind.Producer,
        SimSpanKind.Consumer => ActivityKind.Consumer,
        _ => ActivityKind.Internal,
    };

    public ValueTask DisposeAsync()
    {
        foreach (var pod in _pods.Values) pod.Dispose();
        _pods.Clear();
        return ValueTask.CompletedTask;
    }
}
