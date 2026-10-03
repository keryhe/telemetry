using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Api.Models;

/// <summary>
/// <c>GET /api/traces/{traceId}/spans</c>'s response (trace-list-detail-performance plan, Phase 6c): the trace's spans
/// plus the distinct resources and instrumentation scopes they refer to, each listed once and referenced by index.
/// A span used to carry its own copy of its resource and scope attributes, so a trace of thousands of spans repeated the
/// same few (often large, e.g. Kubernetes) attribute sets thousands of times.
/// </summary>
public sealed class TraceDetailResponse
{
    public List<ResourceModel> Resources { get; init; } = [];
    public List<InstrumentationScopeModel> Scopes { get; init; } = [];
    public List<TraceSpanDto> Spans { get; init; } = [];

    /// <summary>
    /// Builds the response from the repository's spans, listing each distinct resource and scope instance once. The
    /// repository hands every span of a resource the same instance, so identity is what de-duplicates.
    /// </summary>
    public static TraceDetailResponse From(IEnumerable<SpanModel> spans)
    {
        var resources = new List<ResourceModel>();
        var scopes = new List<InstrumentationScopeModel>();
        var resourceIndex = new Dictionary<ResourceModel, int>(ReferenceEqualityComparer.Instance);
        var scopeIndex = new Dictionary<InstrumentationScopeModel, int>(ReferenceEqualityComparer.Instance);
        var dtos = new List<TraceSpanDto>();

        foreach (var span in spans)
        {
            var resource = span.Resource ?? new ResourceModel();
            if (!resourceIndex.TryGetValue(resource, out var r))
            {
                r = resources.Count;
                resourceIndex[resource] = r;
                resources.Add(resource);
            }
            var scope = span.InstrumentationScope ?? new InstrumentationScopeModel { Name = "" };
            if (!scopeIndex.TryGetValue(scope, out var sc))
            {
                sc = scopes.Count;
                scopeIndex[scope] = sc;
                scopes.Add(scope);
            }
            dtos.Add(TraceSpanDto.From(span, r, sc));
        }
        return new TraceDetailResponse { Resources = resources, Scopes = scopes, Spans = dtos };
    }
}

/// <summary>A span as <see cref="SpanModel"/> but with its resource and scope replaced by indexes into the response's lists.</summary>
public sealed class TraceSpanDto
{
    public string TraceIdHex { get; init; } = null!;
    public string SpanIdHex { get; init; } = null!;
    public string? ParentSpanIdHex { get; init; }
    public string Name { get; init; } = null!;
    public SpanKind Kind { get; init; }
    public long StartTimeUnixNano { get; init; }
    public long EndTimeUnixNano { get; init; }
    public int DroppedAttributesCount { get; init; }
    public int DroppedEventsCount { get; init; }
    public int DroppedLinksCount { get; init; }
    public string? TraceState { get; init; }
    public int Flags { get; init; }
    public SpanStatusCode StatusCode { get; init; }
    public string? StatusMessage { get; init; }
    public Dictionary<string, object>? Attributes { get; init; }
    public List<SpanEventModel> Events { get; init; } = [];
    public List<SpanLinkModel> Links { get; init; } = [];

    /// <summary>Index into <see cref="TraceDetailResponse.Resources"/>.</summary>
    public int ResourceIndex { get; init; }

    /// <summary>Index into <see cref="TraceDetailResponse.Scopes"/>.</summary>
    public int ScopeIndex { get; init; }

    public static TraceSpanDto From(SpanModel s, int resourceIndex, int scopeIndex) => new()
    {
        TraceIdHex = s.TraceIdHex,
        SpanIdHex = s.SpanIdHex,
        ParentSpanIdHex = s.ParentSpanIdHex,
        Name = s.Name,
        Kind = s.Kind,
        StartTimeUnixNano = s.StartTimeUnixNano,
        EndTimeUnixNano = s.EndTimeUnixNano,
        DroppedAttributesCount = s.DroppedAttributesCount,
        DroppedEventsCount = s.DroppedEventsCount,
        DroppedLinksCount = s.DroppedLinksCount,
        TraceState = s.TraceState,
        Flags = s.Flags,
        StatusCode = s.StatusCode,
        StatusMessage = s.StatusMessage,
        Attributes = s.Attributes,
        Events = s.Events,
        Links = s.Links,
        ResourceIndex = resourceIndex,
        ScopeIndex = scopeIndex,
    };
}
