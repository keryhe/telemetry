using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Keryhe.Telemetry.StressTests.Load;

public sealed class TraceShaper(LoadProfile profile, Topology topology, Random rng)
{
    private readonly TraceLoad _load = profile.Traces;

    internal sealed class Node
    {
        public required Span Span;
        public required int Depth;
        public required ServiceInfo Service;
        public int Children;
    }

    /// <summary>Builds one export of at least <see cref="TransportLoad.RecordsPerExport"/> spans (whole traces, so it may run over).</summary>
    public Payload<ExportTraceServiceRequest> Next()
    {
        var tenantIndex = topology.PickTenant(rng);
        var services = topology.ServicesByTenant[tenantIndex];
        var target = profile.Transport.RecordsPerExport;

        // service -> ScopeSpans, so one ResourceSpans per service however many traces contribute.
        var byService = new Dictionary<string, ScopeSpans>();
        long current = 0, backdated = 0;

        while (current + backdated < target)
        {
            var (baseNanos, age) = TimeStamps.Pick(profile.Time, rng);
            var spans = BuildTrace(services, baseNanos);

            // An orphan trace never delivers its root: its children keep a parent id that matches no span.
            if (spans.Count > 1 && rng.NextDouble() < profile.Time.OrphanFraction)
                spans.RemoveAt(0);

            foreach (var node in spans)
            {
                if (!byService.TryGetValue(node.Service.Name, out var scope))
                {
                    scope = new ScopeSpans { Scope = topology.Scope };
                    byService[node.Service.Name] = scope;
                }
                scope.Spans.Add(node.Span);
            }

            if (age == RecordAge.Backdated) backdated += spans.Count; else current += spans.Count;
        }

        var request = new ExportTraceServiceRequest();
        foreach (var service in services.Where(s => byService.ContainsKey(s.Name)))
        {
            var rs = new ResourceSpans { Resource = service.Resource };
            rs.ScopeSpans.Add(byService[service.Name]);
            request.ResourceSpans.Add(rs);
        }

        var entries = new List<LedgerEntry>(2);
        if (current > 0) entries.Add(new LedgerEntry("spans", RecordAge.Current, current, false, Dedups: _load.RedeliveryCollapses));
        if (backdated > 0) entries.Add(new LedgerEntry("spans", RecordAge.Backdated, backdated, false, Dedups: _load.RedeliveryCollapses));

        return new Payload<ExportTraceServiceRequest>(request, tenantIndex, (int)(current + backdated), entries,
            rng.NextDouble() < profile.Time.RedeliveryFraction);
    }

    /// <summary>
    /// One export of whole traces starting at <paramref name="baseNanos"/> (history seeding): like <see cref="Next"/> but with a fixed start
    /// instead of the profile's time shaping, so it is ledgered as current. Each trace's id and start go into <paramref name="remember"/>
    /// when given, for the old-trace detail probe.
    /// </summary>
    public Payload<ExportTraceServiceRequest> NextAt(long baseNanos, List<SeededTrace>? remember)
    {
        var tenantIndex = topology.PickTenant(rng);
        var services = topology.ServicesByTenant[tenantIndex];
        var target = profile.Transport.RecordsPerExport;
        var byService = new Dictionary<string, ScopeSpans>();
        long spanCount = 0;
        while (spanCount < target)
        {
            // A little spread inside the export so its traces do not all start in the same nanosecond.
            var spans = BuildTrace(services, baseNanos + (long)(rng.NextDouble() * 1_000_000_000));
            remember?.Add(new SeededTrace(tenantIndex, topology.Tenants[tenantIndex].Id, ToHex(spans[0].Span.TraceId),
                (long)spans.Min(n => n.Span.StartTimeUnixNano), (long)spans.Max(n => n.Span.EndTimeUnixNano), spans.Count, "history"));
            remember = null; // one per export is plenty
            foreach (var node in spans)
            {
                if (!byService.TryGetValue(node.Service.Name, out var scope))
                {
                    scope = new ScopeSpans { Scope = topology.Scope };
                    byService[node.Service.Name] = scope;
                }
                scope.Spans.Add(node.Span);
            }
            spanCount += spans.Count;
        }
        var request = new ExportTraceServiceRequest();
        foreach (var service in services.Where(sv => byService.ContainsKey(sv.Name)))
        {
            var rs = new ResourceSpans { Resource = service.Resource };
            rs.ScopeSpans.Add(byService[service.Name]);
            request.ResourceSpans.Add(rs);
        }
        return new Payload<ExportTraceServiceRequest>(request, tenantIndex, (int)spanCount,
            [new LedgerEntry("spans", RecordAge.Current, spanCount, false, Dedups: _load.RedeliveryCollapses)], false);
    }

    /// <summary>
    /// One trace of exactly <paramref name="spanCount"/> spans for <paramref name="tenantIndex"/>, split into exports of at most
    /// <paramref name="chunkSpans"/> spans (all carrying the same trace id), plus the <see cref="SeededTrace"/> to probe it by.
    /// Wide and moderately deep: a few levels with many children, the shape of a fan-out batch job.
    /// </summary>
    public (List<Payload<ExportTraceServiceRequest>> Chunks, SeededTrace Trace) BuildLargeTrace(int tenantIndex, long baseNanos, int spanCount, int chunkSpans)
    {
        var largeLoad = new LoadProfile { Seed = profile.Seed, Traces = new TraceLoad
        {
            SpansPerTrace = new IntRange(spanCount, spanCount), MaxDepth = 6, FanOut = 60,
            ErrorRate = profile.Traces.ErrorRate, AttributeCount = profile.Traces.AttributeCount,
            AttributeCardinality = profile.Traces.AttributeCardinality, EventsPerSpan = profile.Traces.EventsPerSpan, LinksPerSpan = profile.Traces.LinksPerSpan,
        } };
        var services = topology.ServicesByTenant[tenantIndex];
        var nodes = new TraceShaper(largeLoad, topology, rng).BuildTrace(services, baseNanos);

        var chunks = new List<Payload<ExportTraceServiceRequest>>();
        for (var from = 0; from < nodes.Count; from += chunkSpans)
        {
            var slice = nodes.Skip(from).Take(chunkSpans).ToList();
            var request = new ExportTraceServiceRequest();
            foreach (var group in slice.GroupBy(n => n.Service.Name))
            {
                var rs = new ResourceSpans { Resource = group.First().Service.Resource };
                var scope = new ScopeSpans { Scope = topology.Scope };
                foreach (var node in group) scope.Spans.Add(node.Span);
                rs.ScopeSpans.Add(scope);
                request.ResourceSpans.Add(rs);
            }
            chunks.Add(new Payload<ExportTraceServiceRequest>(request, tenantIndex, slice.Count, [new LedgerEntry("spans", RecordAge.Current, slice.Count, false, Dedups: _load.RedeliveryCollapses)], false));
        }
        var first = nodes[0].Span;
        return (chunks, new SeededTrace(tenantIndex, topology.Tenants[tenantIndex].Id, ToHex(first.TraceId),
            (long)nodes.Min(n => n.Span.StartTimeUnixNano), (long)nodes.Max(n => n.Span.EndTimeUnixNano), nodes.Count, "large"));
    }

    private static string ToHex(ByteString id) => Convert.ToHexString(id.ToByteArray()).ToLowerInvariant();

    internal List<Node> BuildTrace(ServiceInfo[] services, long baseNanos)
    {
        var count = Math.Max(1, _load.SpansPerTrace.Sample(rng));
        var traceId = Topology.RandomId(rng, 16);
        var rootService = services[rng.Next(services.Length)];
        var hasError = rng.NextDouble() < _load.ErrorRate;
        var errorAt = hasError ? rng.Next(count) : -1;

        var rootDurationNs = (long)(Math.Exp(Math.Log(20) + rng.NextDouble() * Math.Log(25)) * 1_000_000); // ~20ms..500ms
        var nodes = new List<Node>(count);
        nodes.Add(MakeNode(traceId, null, 0, rootService, baseNanos, rootDurationNs, Span.Types.SpanKind.Server, error: errorAt == 0));

        for (var i = 1; i < count; i++)
        {
            var parent = PickParent(nodes);
            var pSpan = parent.Span;
            var window = (long)(pSpan.EndTimeUnixNano - pSpan.StartTimeUnixNano);
            var offset = (long)(rng.NextDouble() * window * 0.4);
            var duration = Math.Max(1_000, (long)(rng.NextDouble() * (window - offset) * 0.8));
            var service = rng.NextDouble() < 0.3 ? services[rng.Next(services.Length)] : parent.Service;
            var kind = service == parent.Service ? Span.Types.SpanKind.Internal : Span.Types.SpanKind.Server;
            nodes.Add(MakeNode(traceId, pSpan.SpanId, parent.Depth + 1, service,
                (long)pSpan.StartTimeUnixNano + offset, duration, kind, error: errorAt == i));
            parent.Children++;
        }

        if (hasError && errorAt > 0)
        {
            nodes[0].Span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "downstream error" };
        }
        return nodes;
    }

    private Node PickParent(List<Node> nodes)
    {
        // Random earlier span with headroom in depth and fan-out; the root is always a fallback so this terminates.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = nodes[rng.Next(nodes.Count)];
            if (candidate.Depth < _load.MaxDepth && candidate.Children < _load.FanOut)
                return candidate;
        }
        return nodes.FirstOrDefault(n => n.Depth < _load.MaxDepth && n.Children < _load.FanOut) ?? nodes[0];
    }

    private Node MakeNode(ByteString traceId, ByteString? parentId, int depth, ServiceInfo service,
        long startNanos, long durationNanos, Span.Types.SpanKind kind, bool error)
    {
        var op = service.Operations[rng.Next(service.Operations.Length)];
        var span = new Span
        {
            TraceId = traceId,
            SpanId = Topology.RandomId(rng, 8),
            Name = op,
            Kind = kind,
            StartTimeUnixNano = (ulong)startNanos,
            EndTimeUnixNano = (ulong)(startNanos + durationNanos)
        };
        if (parentId is not null) span.ParentSpanId = parentId;
        if (error) span.Status = new Status { Code = Status.Types.StatusCode.Error, Message = "operation failed" };

        span.Attributes.Add(Topology.Str("http.route", "/" + op));
        for (var a = 0; a < _load.AttributeCount; a++)
            span.Attributes.Add(Topology.Str($"attr.k{a}", $"v{rng.Next(Math.Max(1, _load.AttributeCardinality))}"));

        var events = _load.EventsPerSpan.Sample(rng);
        for (var e = 0; e < events; e++)
        {
            var ev = new Span.Types.Event { Name = $"event.{e}", TimeUnixNano = (ulong)(startNanos + (long)(rng.NextDouble() * durationNanos)) };
            ev.Attributes.Add(Topology.Int("event.index", e));
            span.Events.Add(ev);
        }

        var links = _load.LinksPerSpan.Sample(rng);
        for (var l = 0; l < links; l++)
            span.Links.Add(new Span.Types.Link { TraceId = Topology.RandomId(rng, 16), SpanId = Topology.RandomId(rng, 8) });

        return new Node { Span = span, Depth = depth, Service = service };
    }
}
