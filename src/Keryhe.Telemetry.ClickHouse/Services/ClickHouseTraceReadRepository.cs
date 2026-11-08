using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// <see cref="ITraceReadRepository"/> over the ClickHouse row model (plans/clickhouse-redesign phase 4, README R4): it
/// implements the contract directly rather than extending <c>TraceReadRepositoryBase</c>, whose SQL assumes reference
/// tables, JSON attribute text and <c>*_unix_nano</c> columns.
///
/// <b>Anchors.</b> A trace is listed once, by its <i>anchor</i>: the earliest span in scope (the selected service's own
/// spans, else the whole trace's), ranked by <c>(start_time, span_id)</c>. The list reads <i>slices</i> of start times from
/// the window's newest (or oldest) end, widened until it has <c>limit + 1</c> anchors. Within a slice each trace's
/// earliest span is its <i>candidate</i>; the candidate is the anchor when its start equals the trace's earliest start in
/// scope according to <c>trace_index</c> (one point lookup by trace id), so a trace that began before the slice is
/// anchored in an older slice or, if before the window, not listed. A trace that began more than a day before the slice
/// is anchored on its earliest span inside the two days looked up (accepted). The error flag and whole-trace bounds
/// come from <c>trace_index</c>, and the row's duration is the whole trace's (<c>max(end_max) - min(start_min)</c>); the
/// span count is exact (<c>uniqExact(span_id)</c>) because a re-delivered span is stored twice. Operation and search
/// filters narrow the candidates; each is a property of the anchor or of the trace, so applying it before the
/// confirmation is equivalent to applying it after. Slow mode's duration is the whole trace's, which no candidate span
/// carries, so it is checked at the confirmation, against the same <c>trace_index</c> bounds.
///
/// Attribute values come back as strings (README R7); ids are converted at the edges only (README R8).
/// </summary>
public sealed class ClickHouseTraceReadRepository : DapperReadRepository, ITraceReadRepository
{
    private const long NanosPerMinute = 60_000_000_000L;
    private const long NanosPerSecond = 1_000_000_000L;
    private const int ExportChunkSize = 1000;
    private const int IdChunkSize = 1000;

    private readonly string _connectionString;
    private readonly int _summaryTimeoutSeconds;
    private readonly long _anchorLookbackNanos;
    private readonly long _pageSliceNanos;
    private readonly int _pageSliceGrowth;
    private readonly bool _traceHintEnabled;
    private readonly long _traceHintMarginNanos;

    public ClickHouseTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext) : base(tenantContext)
    {
        _connectionString = configuration.GetConnectionString("Api")!;
        string? Setting(string key) => configuration[$"{QueryOptions.SectionName}:{key}"];
        _summaryTimeoutSeconds = int.TryParse(Setting("SummaryTimeoutSeconds"), out var timeout) ? timeout : 5;
        _anchorLookbackNanos = (int.TryParse(Setting("AnchorLookbackMinutes"), out var lookback) && lookback >= 0 ? lookback : QueryOptions.DefaultAnchorLookbackMinutes) * NanosPerMinute;
        _pageSliceNanos = (int.TryParse(Setting("PageSliceSeconds"), out var slice) && slice >= 1 ? slice : QueryOptions.DefaultPageSliceSeconds) * NanosPerSecond;
        _pageSliceGrowth = int.TryParse(Setting("PageSliceGrowth"), out var growth) && growth >= 2 ? growth : QueryOptions.DefaultPageSliceGrowth;
        _traceHintEnabled = !bool.TryParse(Setting("TraceHintEnabled"), out var hintEnabled) || hintEnabled;
        _traceHintMarginNanos = (int.TryParse(Setting("TraceHintMarginMinutes"), out var margin) && margin >= 0 ? margin : QueryOptions.DefaultTraceHintMarginMinutes) * NanosPerMinute;
    }

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // A nanosecond bound as a constant of the column's type, so partitions and the sort key prune.
    private static string At(string parameter) => $"fromUnixTimestamp64Nano(CAST(@{parameter} AS Int64), 'UTC')";

    // =========================================================================
    // TRACE DETAIL
    // =========================================================================

    public Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
        => GetTraceByIdAsync(traceIdHex, null, cancellationToken);

    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, TraceTimeHint? hint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        var trace = ParseTraceId(traceIdHex);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        (long Min, long Max)? hinted = _traceHintEnabled && hint is { } h
            ? (TimeConversion.DateTimeToUnixNano(h.Start) - _traceHintMarginNanos, TimeConversion.DateTimeToUnixNano(h.End) + _traceHintMarginNanos)
            : null;
        var spans = await LoadSpansAsync(conn, trace, null, null, hinted ?? await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken);

        // A hint that finds nothing must not turn an existing trace into a 404: read it again without the hint's bounds.
        if (spans.Count == 0 && hinted is not null)
            spans = await LoadSpansAsync(conn, trace, null, null, await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken);

        return DistinctSpans(spans);
    }

    public async Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex)) throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex)) throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));
        var trace = ParseTraceId(traceIdHex);
        var span = ParseSpanId(spanIdHex);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var spans = await LoadSpansAsync(conn, trace, span, null, await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken);
        return spans.FirstOrDefault();
    }

    public async Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex)) throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(parentSpanIdHex)) throw new ArgumentException("Parent Span ID cannot be null or empty", nameof(parentSpanIdHex));
        var trace = ParseTraceId(traceIdHex);
        var parent = ParseSpanId(parentSpanIdHex);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        return DistinctSpans(await LoadSpansAsync(conn, trace, null, parent, await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken));
    }

    // A malformed id is a client error, never a ClickHouse exception (README R8).
    private static Guid ParseTraceId(string hex)
        => ClickHouseIds.TryTraceIdToGuid(hex, out var id) ? id : throw new ArgumentException("Trace ID must be 32 hex characters", nameof(hex));

    private static ulong ParseSpanId(string hex)
        => ClickHouseIds.TrySpanIdToUInt64(hex, out var id) ? id : throw new ArgumentException("Span ID must be 16 hex characters", nameof(hex));

    /// <summary>The trace's whole extent from <c>trace_index</c>; null when the index has no row for it.</summary>
    private async Task<(long Min, long Max)?> TraceBoundsAsync(DbConnection conn, Guid trace, CancellationToken ct)
    {
        var row = await conn.QuerySingleAsync<BoundsRow>(new CommandDefinition("""
            SELECT count() AS Cnt, toUnixTimestamp64Nano(min(start_min)) AS MinStart, toUnixTimestamp64Nano(max(end_max)) AS MaxEnd
            FROM trace_index
            WHERE tenant_id = @tenantId AND trace_id = @trace
            """, new { tenantId = (ulong)TenantId, trace }, cancellationToken: ct));
        return row.Cnt == 0 ? null : (row.MinStart, row.MaxEnd);
    }

    private static List<SpanModel> DistinctSpans(List<SpanModel> spans)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return spans.Where(s => seen.Add(s.SpanIdHex)).ToList();
    }

    private async Task<List<SpanModel>> LoadSpansAsync(
        DbConnection conn, Guid trace, ulong? spanId, ulong? parentSpanId, (long Min, long Max)? bounds, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", (ulong)TenantId);
        parameters.Add("trace", trace);
        var where = new StringBuilder("tenant_id = @tenantId AND trace_id = @trace");
        if (spanId is { } sid) { where.Append(" AND span_id = @spanId"); parameters.Add("spanId", sid); }
        if (parentSpanId is { } pid) { where.Append(" AND parent_span_id = @parentSpanId"); parameters.Add("parentSpanId", pid); }
        if (bounds is { } b)
        {
            where.Append($" AND start_time >= {At("boundMin")} AND start_time <= {At("boundMax")}");
            parameters.Add("boundMin", b.Min);
            parameters.Add("boundMax", b.Max);
        }

        var sql = $"""
            SELECT trace_id AS TraceId, span_id AS SpanId, parent_span_id AS ParentSpanId, span_name AS SpanName,
                   toString(kind) AS Kind, toUnixTimestamp64Nano(start_time) AS StartNano, duration_ns AS DurationNs,
                   dropped_attributes_count AS DroppedAttributes, dropped_events_count AS DroppedEvents, dropped_links_count AS DroppedLinks,
                   trace_state AS TraceState, flags AS SpanFlags, toString(status_code) AS StatusCode, status_message AS StatusMessage,
                   attributes AS SpanAttributes,
                   arrayMap(t -> toUnixTimestamp64Nano(t), events.time) AS EventTimes, events.name AS EventNames,
                   events.attributes AS EventAttributes, events.dropped_attributes_count AS EventDropped,
                   links.trace_id AS LinkTraceIds, links.span_id AS LinkSpanIds, links.trace_state AS LinkStates,
                   links.flags AS LinkFlags, links.attributes AS LinkAttributes, links.dropped_attributes_count AS LinkDropped,
                   resource_attributes AS ResourceAttributes, resource_schema_url AS ResourceSchemaUrl,
                   scope_name AS ScopeName, scope_version AS ScopeVersion, scope_attributes AS ScopeAttributes, scope_schema_url AS ScopeSchemaUrl,
                   cityHash64(toString(resource_attributes), resource_schema_url) AS ResourceKey,
                   cityHash64(toString(scope_attributes), scope_name, scope_version, scope_schema_url) AS ScopeKey
            FROM spans
            WHERE {where}
            ORDER BY start_time, span_id
            """;
        var rows = (await conn.QueryAsync<SpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        // Spans that share resource and scope values share one instance, so the response lists each once.
        var resources = new Dictionary<ulong, ResourceModel>();
        var scopes = new Dictionary<ulong, InstrumentationScopeModel>();
        var result = new List<SpanModel>(rows.Count);
        foreach (var r in rows)
        {
            if (!resources.TryGetValue(r.ResourceKey, out var resource))
                resources[r.ResourceKey] = resource = new ResourceModel
                {
                    SchemaUrl = NullIfEmpty(r.ResourceSchemaUrl),
                    Attributes = ToObjects(r.ResourceAttributes) ?? new Dictionary<string, object>()
                };
            if (!scopes.TryGetValue(r.ScopeKey, out var scope))
                scopes[r.ScopeKey] = scope = new InstrumentationScopeModel
                {
                    Name = r.ScopeName ?? "",
                    Version = NullIfEmpty(r.ScopeVersion),
                    SchemaUrl = NullIfEmpty(r.ScopeSchemaUrl),
                    Attributes = ToObjects(r.ScopeAttributes) ?? new Dictionary<string, object>()
                };

            var events = new List<SpanEventModel>();
            for (var i = 0; i < (r.EventNames?.Length ?? 0); i++)
                events.Add(new SpanEventModel
                {
                    Name = r.EventNames![i], TimeUnixNano = r.EventTimes[i], DroppedAttributesCount = (int)r.EventDropped[i],
                    Attributes = ToObjects(r.EventAttributes[i])
                });
            var links = new List<SpanLinkModel>();
            for (var i = 0; i < (r.LinkTraceIds?.Length ?? 0); i++)
                links.Add(new SpanLinkModel
                {
                    LinkedTraceIdHex = ClickHouseIds.GuidToTraceIdHex(r.LinkTraceIds![i]), LinkedSpanIdHex = ClickHouseIds.UInt64ToSpanIdHex(r.LinkSpanIds[i]),
                    TraceState = NullIfEmpty(r.LinkStates[i]), Flags = (int)r.LinkFlags[i], DroppedAttributesCount = (int)r.LinkDropped[i],
                    Attributes = ToObjects(r.LinkAttributes[i])
                });

            result.Add(new SpanModel
            {
                TraceIdHex = ClickHouseIds.GuidToTraceIdHex(r.TraceId),
                SpanIdHex = ClickHouseIds.UInt64ToSpanIdHex(r.SpanId),
                ParentSpanIdHex = r.ParentSpanId == 0 ? null : ClickHouseIds.UInt64ToSpanIdHex(r.ParentSpanId),
                Name = r.SpanName,
                Kind = Enum.Parse<SpanKind>(r.Kind),
                StartTimeUnixNano = r.StartNano,
                EndTimeUnixNano = r.StartNano + (long)r.DurationNs,
                DroppedAttributesCount = (int)r.DroppedAttributes,
                DroppedEventsCount = (int)r.DroppedEvents,
                DroppedLinksCount = (int)r.DroppedLinks,
                TraceState = NullIfEmpty(r.TraceState),
                Flags = (int)r.SpanFlags,
                StatusCode = Enum.Parse<SpanStatusCode>(r.StatusCode),
                StatusMessage = NullIfEmpty(r.StatusMessage),
                Attributes = ToObjects(r.SpanAttributes),
                Events = events,
                Links = links,
                Resource = resource,
                InstrumentationScope = scope
            });
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static Dictionary<string, object>? ToObjects(IDictionary<string, string>? map)
        => map is null || map.Count == 0 ? null : map.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

    // =========================================================================
    // LIST
    // =========================================================================

    private sealed class Anchor
    {
        public Guid TraceId { get; set; }
        public ulong AnchorSpanId { get; set; }
        public long AnchorStart { get; set; }
        public ulong DurationNs { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "UNSPECIFIED";
        public string Service { get; set; } = "";
        public byte HasErrorInt { get; set; }

        // filled by the trace_index confirmation
        public long TraceMin, TraceMax;
        public bool HasError;
        public int SpanCount = 1;
    }

    public async Task<TraceListResult> GetTraceListAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Max(1, query.Limit);
        var descending = !ListOrder.IsOldest(query.Order);
        var service = string.IsNullOrEmpty(query.Service) ? null : query.Service;
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var parsed = SearchQueryParser.Parse(query.Search);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var filter = new CandidateFilter(query.Mode, service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);

        var anchors = await FetchSlicedAnchorsAsync(conn, filter, limit + 1, descending, startNano, endNano + 1, cancellationToken);
        var truncated = anchors.Count > limit;
        if (truncated) anchors.RemoveAt(anchors.Count - 1);

        await FillSpanCountsAsync(conn, anchors, service, cancellationToken);
        return new TraceListResult { Items = anchors.Select(a => ToInfo(a)).ToList(), Truncated = truncated };
    }

    private TraceInfo ToInfo(Anchor a) => new()
    {
        TraceIdHex = ClickHouseIds.GuidToTraceIdHex(a.TraceId),
        SpanCount = a.SpanCount,
        TraceStartTime = TimeConversion.UnixNanoToDateTime(a.TraceMin),
        TraceEndTime = TimeConversion.UnixNanoToDateTime(a.TraceMax),
        TraceDuration = TimeSpan.FromTicks((a.TraceMax - a.TraceMin) / 100),
        ServiceName = string.IsNullOrEmpty(a.Service) ? null : a.Service,
        RootOperationName = a.Name,
        AnchorKind = a.Kind,
        HasErrors = a.HasError,
        DisplaySpanIdHex = ClickHouseIds.UInt64ToSpanIdHex(a.AnchorSpanId)
    };

    /// <summary>The filters of a list, compiled once into the predicates every slice shares.</summary>
    private sealed class CandidateFilter
    {
        public readonly string? Service;
        public readonly string Mode;
        public readonly string SpanPredicates;   // AND ... terms on the candidate span
        public readonly DurationBounds Duration;   // slow mode, on the whole trace: checked at the confirmation
        public readonly bool ErrorsOnly;
        public readonly DynamicParameters Parameters = new();

        public CandidateFilter(string mode, string? service, string? operation, double? minDurationMs, double? maxDurationMs,
            ParsedSearchQuery parsed, long startNano, long endNano, long searchFromNano = 0)
        {
            Mode = mode; Service = service;
            var sql = new StringBuilder();
            if (service is not null) Parameters.Add("service", service);
            if (!string.IsNullOrEmpty(operation))
            {
                sql.Append(" AND span_name = @operation");
                Parameters.Add("operation", operation);
            }
            Duration = DurationBounds.For(mode, minDurationMs, maxDurationMs);
            ErrorsOnly = mode == "errors";
            if (parsed.IsTraceIdSearch)
            {
                var ok = ClickHouseIds.TryTraceIdToGuid(parsed.TraceId, out var id);
                sql.Append(" AND trace_id = @traceIdSearch");
                Parameters.Add("traceIdSearch", ok ? id : Guid.Empty);
            }
            SpanPredicates = sql.ToString();
            SearchSql = parsed.IsTraceIdSearch ? "" : BuildSearch(parsed, Parameters);
        }

        /// <summary>The <c>AND trace_id IN (...)</c> clauses of the search terms.</summary>
        public string SearchSql { get; }
    }

    /// <summary>Slow mode's bounds on the whole trace's duration (none outside slow mode).</summary>
    private readonly record struct DurationBounds(long? Min, long? Max)
    {
        public static DurationBounds For(string mode, double? minDurationMs, double? maxDurationMs) => mode == "slow"
            ? new(Math.Max(0, (long)((minDurationMs ?? 500) * 1_000_000)),
                  maxDurationMs.HasValue ? Math.Max(0, (long)(maxDurationMs.Value * 1_000_000)) : null)
            : default;

        /// <summary>Whether the anchor's whole-trace duration (its bounds, already filled) passes.</summary>
        public bool Accepts(Anchor a)
        {
            var duration = a.TraceMax - a.TraceMin;
            return (Min is not { } min || duration >= min) && (Max is not { } max || duration <= max);
        }
    }

    /// <summary>
    /// One <c>AND trace_id IN (...)</c> clause per search term: a term matches if ANY span of the trace in the search
    /// window matches, regardless of the service filter (each term may match a different span).
    /// </summary>
    private static string BuildSearch(ParsedSearchQuery parsed, DynamicParameters parameters)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var term in parsed.Terms)
        {
            string predicate;
            if (term.IsAttributeFilter)
            {
                parameters.Add($"searchKey{i}", term.Key ?? "");
                parameters.Add($"searchVal{i}", (term.Value ?? "").ToLowerInvariant());
                string Match(string map) => $"(mapContains({map}, @searchKey{i}) = 1 AND lowerUTF8({map}[@searchKey{i}]) = @searchVal{i})";
                // A negated term is the NOT of the positive match on the span or its resource: a span carrying the
                // value on either is excluded, one with neither is kept.
                var positive = $"({Match("attributes")} OR {Match("resource_attributes")})";
                predicate = term.Negate ? $"NOT {positive}" : positive;
            }
            else
            {
                parameters.Add($"searchText{i}", term.FreeText ?? "");
                var m = $"(positionCaseInsensitiveUTF8(span_name, @searchText{i}) > 0 OR positionCaseInsensitiveUTF8(status_message, @searchText{i}) > 0)";
                predicate = term.Negate ? $"NOT {m}" : m;
            }
            sb.Append($" AND trace_id IN (SELECT trace_id FROM spans WHERE tenant_id = @tenantId AND start_time >= {At("searchFrom")} AND start_time <= {At("searchTo")} AND {predicate})");
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Binds what depends on the request: the tenant and the search window (the list's window, extended back by the look-back margin).</summary>
    private static void Bind(CandidateFilter f, long tenant, long startNano, long endNano, long lookbackNanos)
    {
        f.Parameters.Add("tenantId", (ulong)tenant);
        f.Parameters.Add("searchFrom", startNano - lookbackNanos);
        f.Parameters.Add("searchTo", endNano);
    }

    private async Task<List<Anchor>> FetchSlicedAnchorsAsync(
        DbConnection conn, CandidateFilter f, int need, bool descending, long rangeFrom, long rangeTo, CancellationToken ct)
    {
        Bind(f, TenantId, rangeFrom, rangeTo - 1, _anchorLookbackNanos);
        var rows = new List<Anchor>(need);
        var width = _pageSliceNanos;
        long lo = rangeFrom, hi = rangeTo;

        while (rows.Count < need && lo < hi)
        {
            long sliceFrom, sliceTo;
            if (descending) { sliceTo = hi; sliceFrom = Math.Max(lo, hi - width); }
            else { sliceFrom = lo; sliceTo = Math.Min(hi, lo + width); }

            // Confirm in list order and stop once the page is full (as on Parquet): slow mode has no candidate-span
            // predicate, so a widened slice can hold every trace of the range, each needing a trace_index lookup.
            var candidates = await CandidatesAsync(conn, f, sliceFrom, sliceTo, ct);
            var ordered = descending
                ? candidates.OrderByDescending(a => a.AnchorStart).ThenByDescending(a => a.AnchorSpanId)
                : candidates.OrderBy(a => a.AnchorStart).ThenBy(a => a.AnchorSpanId);
            var confirmed = await ConfirmAsync(conn, ordered.ToList(), f, sliceFrom, sliceTo, ct, stopAfter: need - rows.Count);
            rows.AddRange(confirmed.Take(need - rows.Count));

            if (descending) hi = sliceFrom; else lo = sliceTo;
            var remaining = hi - lo;
            width = width > long.MaxValue / _pageSliceGrowth ? remaining : width * _pageSliceGrowth;
            if (width >= remaining / 2) width = Math.Max(1, remaining);
        }
        return rows;
    }

    private const string TieBreak = "(start_time, span_id)";

    /// <summary>Each trace's earliest matching span in the slice (before confirmation against trace_index).</summary>
    private async Task<List<Anchor>> CandidatesAsync(DbConnection conn, CandidateFilter f, long sliceFrom, long sliceTo, CancellationToken ct)
    {
        var service = f.Service is null ? "" : " AND service_name = @service";
        var errors = f.ErrorsOnly
            ? $" AND trace_id IN (SELECT trace_id FROM trace_index WHERE tenant_id = @tenantId AND has_error = 1{(f.Service is null ? "" : " AND service_name = @service")} AND day >= toDate({At("sliceFrom")}) - 1 AND day <= toDate({At("sliceTo")}))"
            : "";
        var sql = $"""
            SELECT trace_id AS TraceId,
                   argMin(span_id, {TieBreak}) AS AnchorSpanId,
                   toUnixTimestamp64Nano(min(start_time)) AS AnchorStart,
                   argMin(duration_ns, {TieBreak}) AS DurationNs,
                   argMin(span_name, {TieBreak}) AS Name,
                   argMin(toString(kind), {TieBreak}) AS Kind,
                   argMin(service_name, {TieBreak}) AS Service
            FROM spans
            WHERE tenant_id = @tenantId{service} AND start_time >= {At("sliceFrom")} AND start_time < {At("sliceTo")}{f.SpanPredicates}{errors}{f.SearchSql}
            GROUP BY trace_id
            """;
        var p = new DynamicParameters(f.Parameters);
        p.Add("sliceFrom", sliceFrom);
        p.Add("sliceTo", sliceTo);
        return (await conn.QueryAsync<Anchor>(new CommandDefinition(sql, p, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Keeps the candidates that are their trace's anchor (start equals the trace's earliest start in scope per
    /// <c>trace_index</c>) and pass slow mode's whole-trace duration, filling the whole-trace bounds and the scoped error flag,
    /// in the candidates' order. With <paramref name="stopAfter"/>, no further chunk is read once that many are confirmed.
    /// </summary>
    private async Task<List<Anchor>> ConfirmAsync(
        DbConnection conn, List<Anchor> candidates, CandidateFilter f, long sliceFrom, long sliceTo, CancellationToken ct,
        int stopAfter = int.MaxValue)
    {
        var confirmed = new List<Anchor>();
        for (var offset = 0; offset < candidates.Count && confirmed.Count < stopAfter; offset += IdChunkSize)
        {
            var chunk = candidates.Skip(offset).Take(IdChunkSize).ToList();
            var index = await TraceIndexAsync(conn, chunk.Select(c => c.TraceId).ToArray(), f.Service, sliceFrom, sliceTo, ct);
            foreach (var c in chunk)
            {
                if (!index.TryGetValue(c.TraceId, out var row) || row.ScopeMin != c.AnchorStart) continue;
                c.TraceMin = row.TraceMin; c.TraceMax = row.TraceMax; c.HasError = row.ScopeError != 0;
                if (f.Duration.Accepts(c)) confirmed.Add(c);
            }
        }
        return confirmed;
    }

    private async Task<Dictionary<Guid, IndexRow>> TraceIndexAsync(
        DbConnection conn, Guid[] ids, string? service, long from, long to, CancellationToken ct)
    {
        var scopeMin = service is null ? "min(start_min)" : "minIf(start_min, service_name = @service)";
        var scopeError = service is null ? "max(has_error)" : "maxIf(has_error, service_name = @service)";
        var sql = $"""
            SELECT trace_id AS TraceId,
                   toUnixTimestamp64Nano({scopeMin}) AS ScopeMin, {scopeError} AS ScopeError,
                   toUnixTimestamp64Nano(min(start_min)) AS TraceMin, toUnixTimestamp64Nano(max(end_max)) AS TraceMax
            FROM trace_index
            WHERE tenant_id = @tenantId AND trace_id IN @ids AND day >= toDate({At("from")}) - 1 AND day <= toDate({At("to")}) + 1
            GROUP BY trace_id
            """;
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("ids", ids);
        p.Add("from", from);
        p.Add("to", to);
        if (service is not null) p.Add("service", service);
        return (await conn.QueryAsync<IndexRow>(new CommandDefinition(sql, p, cancellationToken: ct))).ToDictionary(r => r.TraceId);
    }

    /// <summary>Exact span counts (a re-delivered span counts once) for the page's traces, scoped to the service when one is selected.</summary>
    private async Task FillSpanCountsAsync(DbConnection conn, List<Anchor> anchors, string? service, CancellationToken ct)
    {
        if (anchors.Count == 0) return;
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("ids", anchors.Select(a => a.TraceId).ToArray());
        p.Add("minStart", anchors.Min(a => a.TraceMin));
        p.Add("maxStart", anchors.Max(a => a.TraceMax));
        var scope = service is null ? "" : " AND service_name = @service";
        if (service is not null) p.Add("service", service);
        var rows = await conn.QueryAsync<CountRow>(new CommandDefinition($"""
            SELECT trace_id AS TraceId, uniqExact(span_id) AS SpanCount
            FROM spans
            WHERE tenant_id = @tenantId AND trace_id IN @ids AND start_time >= {At("minStart")} AND start_time <= {At("maxStart")}{scope}
            GROUP BY trace_id
            """, p, cancellationToken: ct));
        var counts = rows.ToDictionary(r => r.TraceId, r => (int)r.SpanCount);
        foreach (var a in anchors) if (counts.TryGetValue(a.TraceId, out var n)) a.SpanCount = n;
    }

    // =========================================================================
    // SAMPLES (dashboard)
    // =========================================================================

    public async Task<TraceSamplesResult> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Clamp(query.Limit, 1, 50);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        var (anchors, timedOut) = await TimedQuery.RunAsync(async (timeoutSeconds, ct) =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return query.Kind == "errors"
                ? await FetchSlicedAnchorsAsync(conn, new CandidateFilter("errors", null, null, null, null, ParsedSearchQuery.Empty, startNano, endNano),
                    limit, descending: true, startNano, endNano + 1, ct)
                : await SlowestAnchorsAsync(conn, limit, startNano, endNano, ct);
        }, _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || anchors is null) return new TraceSamplesResult { TimedOut = true };

        // The follow-up runs on its own connection: the one the (possibly aborted) first stage used is never reused.
        await using var followUp = await OpenConnectionAsync(cancellationToken);
        await FillSpanCountsAsync(followUp, anchors, null, cancellationToken);
        return new TraceSamplesResult { Items = anchors.Select(a => ToInfo(a)).ToList() };
    }

    /// <summary>
    /// The slowest traces above a 500 ms floor, by the whole trace's duration: <c>trace_index</c> is aggregated per trace
    /// over the window's days (one row per trace, service and day, so far fewer rows than spans), traces starting before
    /// the window or over its end dropped, and the top <paramref name="limit"/> by <c>max(end_max) - min(start_min)</c>
    /// kept. Their anchors (each trace's earliest span) are then read for just those ids.
    /// </summary>
    private async Task<List<Anchor>> SlowestAnchorsAsync(DbConnection conn, int limit, long startNano, long endNano, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("start", startNano);
        p.Add("end", endNano);
        p.Add("floor", 500_000_000L);
        p.Add("take", limit);
        var traces = (await conn.QueryAsync<SlowTraceRow>(new CommandDefinition($"""
            SELECT trace_id AS TraceId, toUnixTimestamp64Nano(min(start_min)) AS TraceMin, toUnixTimestamp64Nano(max(end_max)) AS TraceMax,
                   max(has_error) AS HasError
            FROM trace_index
            WHERE tenant_id = @tenantId AND day >= toDate({At("start")}) - 1 AND day <= toDate({At("end")}) + 1
            GROUP BY trace_id
            HAVING min(start_min) >= {At("start")} AND min(start_min) <= {At("end")} AND TraceMax - TraceMin > @floor
            ORDER BY TraceMax - TraceMin DESC, trace_id DESC
            LIMIT @take
            """, p, cancellationToken: ct))).ToList();
        if (traces.Count == 0) return [];

        // Each trace's earliest span, read between the earliest and latest trace start of the page.
        var q = new DynamicParameters();
        q.Add("tenantId", (ulong)TenantId);
        q.Add("ids", traces.Select(t => t.TraceId).ToArray());
        q.Add("from", traces.Min(t => t.TraceMin));
        q.Add("to", traces.Max(t => t.TraceMin));
        var anchors = (await conn.QueryAsync<Anchor>(new CommandDefinition($"""
            SELECT trace_id AS TraceId,
                   argMin(span_id, {TieBreak}) AS AnchorSpanId,
                   toUnixTimestamp64Nano(min(start_time)) AS AnchorStart,
                   argMin(duration_ns, {TieBreak}) AS DurationNs,
                   argMin(span_name, {TieBreak}) AS Name,
                   argMin(toString(kind), {TieBreak}) AS Kind,
                   argMin(service_name, {TieBreak}) AS Service
            FROM spans
            WHERE tenant_id = @tenantId AND trace_id IN @ids AND start_time >= {At("from")} AND start_time <= {At("to")}
            GROUP BY trace_id
            """, q, cancellationToken: ct))).ToDictionary(a => a.TraceId);

        var result = new List<Anchor>(traces.Count);
        foreach (var t in traces)
        {
            if (!anchors.TryGetValue(t.TraceId, out var a)) continue;
            a.TraceMin = t.TraceMin; a.TraceMax = t.TraceMax; a.HasError = t.HasError != 0;
            result.Add(a);
        }
        return result;   // already in the query's order (duration, then trace id): re-sorting on another key would break ties differently
    }

    public async Task<SlowRequestCount> CountSlowInboundSpansAsync(
        DateTime start, DateTime end, string? service, double minDurationMs, CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", (ulong)TenantId);
        parameters.Add("start", TimeConversion.DateTimeToUnixNano(start));
        parameters.Add("end", TimeConversion.DateTimeToUnixNano(end));
        parameters.Add("minNanos", (ulong)Math.Max(0, (long)Math.Ceiling(minDurationMs * 1_000_000.0)));
        var serviceFilter = "";
        if (service != null)
        {
            parameters.Add("service", service);
            serviceFilter = " AND service_name = @service";
        }

        var sql = $"""
            SELECT count() FROM spans
            WHERE tenant_id = @tenantId AND start_time >= {At("start")} AND start_time < {At("end")}
              AND kind IN ('SERVER', 'CONSUMER') AND duration_ns >= @minNanos{serviceFilter}
            """;
        var (count, timedOut) = await TimedQuery.RunAsync(async (timeoutSeconds, ct) =>
        {
            await using var conn = await OpenConnectionAsync(ct);
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct));
        }, _summaryTimeoutSeconds, cancellationToken);
        return new SlowRequestCount(timedOut ? 0 : count, timedOut);
    }

    // =========================================================================
    // ANALYSIS
    // =========================================================================

    public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        // A child's parent is found by (trace_id, span_id) among the spans of the same window (extended back by the look-back
        // margin so a parent that started just before the window is found).
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        var child = "";
        var parent = "";
        if (startTime.HasValue)
        {
            p.Add("start", TimeConversion.DateTimeToUnixNano(startTime.Value));
            p.Add("parentFrom", TimeConversion.DateTimeToUnixNano(startTime.Value) - _anchorLookbackNanos);
            child += $" AND c.start_time >= {At("start")}";
            parent += $" AND start_time >= {At("parentFrom")}";
        }
        if (endTime.HasValue)
        {
            p.Add("end", TimeConversion.DateTimeToUnixNano(endTime.Value));
            child += $" AND c.start_time <= {At("end")}";
            parent += $" AND start_time <= {At("end")}";
        }

        var sql = $"""
            SELECT pr.service_name AS ParentService, c.service_name AS ChildService, toString(c.kind) AS Kind,
                   count() AS CallCount, avg(c.duration_ns) AS AvgNs, min(c.duration_ns) AS MinNs, max(c.duration_ns) AS MaxNs,
                   countIf(c.status_code = 'ERROR') AS ErrorCount
            FROM spans AS c
            INNER JOIN (
                SELECT trace_id, span_id, any(service_name) AS service_name
                FROM spans
                WHERE tenant_id = @tenantId{parent}
                GROUP BY trace_id, span_id
            ) AS pr ON c.trace_id = pr.trace_id AND c.parent_span_id = pr.span_id
            WHERE c.tenant_id = @tenantId{child} AND c.service_name != '' AND pr.service_name != '' AND c.service_name != pr.service_name
            GROUP BY ParentService, ChildService, Kind
            ORDER BY CallCount DESC
            """;
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<DependencyRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));
        return rows.Select(r => new ServiceDependency
        {
            ParentService = r.ParentService, ChildService = r.ChildService, SpanKind = Enum.Parse<SpanKind>(r.Kind),
            CallCount = (int)r.CallCount, AvgDurationMs = r.AvgNs / 1_000_000.0, MinDurationMs = r.MinNs / 1_000_000.0,
            MaxDurationMs = r.MaxNs / 1_000_000.0, ErrorCount = (int)r.ErrorCount, ErrorRate = r.ErrorCount / (double)r.CallCount * 100
        }).ToList();
    }

    public async Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        var rows = await ReadOperationRollupAsync(serviceName, startTime, endTime, cancellationToken);
        return rows.ToDictionary(r => r.Operation, r => (int)r.Requests);
    }

    public async Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        var rows = await ReadOperationRollupAsync(serviceName, startTime, endTime, cancellationToken);
        return rows.Where(r => r.Requests > 0).ToDictionary(r => r.Operation, r => r.SumNanos / (double)r.Requests / 1_000_000.0);
    }

    public async Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName)) throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));
        var rows = await ReadOperationRollupAsync(serviceName, startTime, endTime, cancellationToken);
        var windowSeconds = Math.Max((endTime - startTime).TotalSeconds, 1);
        return rows.Where(r => r.Requests > 0).Select(r =>
        {
            var bands = r.Bands();
            double Ms(double p) => DurationBands.Percentile(bands, p, r.MaxNanos) / 1_000_000.0;
            return new OperationStats
            {
                Operation = r.Operation, Count = (int)r.Requests, ErrorCount = (int)r.Errors,
                ErrorRate = r.Errors / (double)r.Requests * 100, RatePerSecond = r.Requests / windowSeconds,
                AvgMs = r.SumNanos / (double)r.Requests / 1_000_000.0, P50Ms = Ms(0.50), P95Ms = Ms(0.95), P99Ms = Ms(0.99)
            };
        }).OrderByDescending(o => o.Count).ToList();
    }

    /// <summary>
    /// Per-operation figures from <c>request_rollup_minute</c>. They describe inbound requests (SERVER/CONSUMER spans), not
    /// every span, and the percentiles are the rollup's approximate ones (README decision 8).
    /// </summary>
    private async Task<List<OperationRollupRow>> ReadOperationRollupAsync(string service, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("service", service);
        var where = "";
        if (start.HasValue) { where += " AND bucket_start_unix_nano >= @start"; p.Add("start", TimeConversion.DateTimeToUnixNano(start.Value)); }
        if (end.HasValue) { where += " AND bucket_start_unix_nano < @end"; p.Add("end", TimeConversion.DateTimeToUnixNano(end.Value)); }
        var bands = string.Join(", ", Enumerable.Range(0, DurationBands.Count).Select(i => $"sum(h{i:00}) AS H{i:00}"));
        await using var conn = await OpenConnectionAsync(ct);
        return (await conn.QueryAsync<OperationRollupRow>(new CommandDefinition($"""
            SELECT operation AS Operation, sum(request_count) AS Requests, sum(error_count) AS Errors,
                   sum(sum_duration_nanos) AS SumNanos, max(max_duration_nanos) AS MaxNanos, {bands}
            FROM request_rollup_minute
            WHERE tenant_id = @tenantId AND service_name = @service{where}
            GROUP BY operation
            """, p, cancellationToken: ct))).ToList();
    }

    // =========================================================================
    // EXPORT
    // =========================================================================

    /// <summary>
    /// Streams one row per matching trace, oldest first, with no row cap. The anchors of the whole window are derived once
    /// (<c>GROUP BY trace_id</c> with <c>argMin</c>; export reads the whole window anyway) and streamed; every
    /// <see cref="ExportChunkSize"/> anchors a second connection fetches that chunk's bounds, error flag and exact span counts.
    /// </summary>
    public async IAsyncEnumerable<TraceInfo> ExportTracesAsync(
        TraceExportQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var service = string.IsNullOrEmpty(query.Service) ? null : query.Service;
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var parsed = SearchQueryParser.Parse(query.Search);
        var f = new CandidateFilter("all", service, null, null, null, parsed, startNano, endNano);
        Bind(f, TenantId, startNano, endNano, _anchorLookbackNanos);
        f.Parameters.Add("anchorFrom", startNano - _anchorLookbackNanos);
        f.Parameters.Add("start", startNano);
        f.Parameters.Add("end", endNano);

        var having = new StringBuilder($"min(start_time) >= {At("start")}");
        if (!string.IsNullOrEmpty(query.Operation)) { having.Append($" AND argMin(span_name, {TieBreak}) = @operation"); f.Parameters.Add("operation", query.Operation); }
        if (query.Mode == "errors") having.Append(" AND max(status_code = 'ERROR') = 1");
        // Slow mode is on the whole trace's duration, known only once trace_index is read: CompleteAsync filters it.
        var slow = DurationBounds.For(query.Mode, query.MinDurationMs, query.MaxDurationMs);
        var traceIdFilter = f.SpanPredicates.Contains("traceIdSearch") ? " AND trace_id = @traceIdSearch" : "";

        var sql = $"""
            SELECT trace_id AS TraceId,
                   argMin(span_id, {TieBreak}) AS AnchorSpanId,
                   toUnixTimestamp64Nano(min(start_time)) AS AnchorStart,
                   argMin(duration_ns, {TieBreak}) AS DurationNs,
                   argMin(span_name, {TieBreak}) AS Name,
                   argMin(toString(kind), {TieBreak}) AS Kind,
                   argMin(service_name, {TieBreak}) AS Service
            FROM spans
            WHERE tenant_id = @tenantId{(service is null ? "" : " AND service_name = @service")}
              AND start_time >= {At("anchorFrom")} AND start_time <= {At("end")}{traceIdFilter}{f.SearchSql}
            GROUP BY trace_id
            HAVING {having}
            ORDER BY AnchorStart ASC, AnchorSpanId ASC
            """;

        await using var readConn = await OpenConnectionAsync(cancellationToken);
        await using var lookupConn = await OpenConnectionAsync(cancellationToken);
        await using var reader = await readConn.ExecuteReaderAsync(new CommandDefinition(sql, f.Parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<Anchor>();

        var chunk = new List<Anchor>(ExportChunkSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            chunk.Add(parse(reader));
            if (chunk.Count < ExportChunkSize) continue;
            foreach (var info in await CompleteAsync(lookupConn, chunk, service, slow, startNano, endNano, cancellationToken)) yield return info;
            chunk.Clear();
        }
        if (chunk.Count > 0)
            foreach (var info in await CompleteAsync(lookupConn, chunk, service, slow, startNano, endNano, cancellationToken)) yield return info;
    }

    private async Task<List<TraceInfo>> CompleteAsync(
        DbConnection conn, List<Anchor> chunk, string? service, DurationBounds slow, long from, long to, CancellationToken ct)
    {
        var index = await TraceIndexAsync(conn, chunk.Select(c => c.TraceId).ToArray(), service, from - _anchorLookbackNanos, to, ct);
        // A trace not (yet) in trace_index takes its whole-trace bounds from its spans, so slow mode still sees the whole
        // trace's duration; only a trace its spans miss too falls back to the anchor span's own.
        var missing = chunk.Where(a => !index.ContainsKey(a.TraceId)).Select(a => a.TraceId).ToArray();
        var spanBounds = missing.Length == 0 ? [] : await SpanBoundsAsync(conn, missing, from - _anchorLookbackNanos, to + NanosPerDay, ct);
        foreach (var a in chunk)
        {
            if (index.TryGetValue(a.TraceId, out var row)) { a.TraceMin = row.TraceMin; a.TraceMax = row.TraceMax; a.HasError = row.ScopeError != 0; continue; }
            a.HasError = a.HasErrorInt != 0;
            if (spanBounds.TryGetValue(a.TraceId, out var b)) { a.TraceMin = b.TraceMin; a.TraceMax = b.TraceMax; }
            else { a.TraceMin = a.AnchorStart; a.TraceMax = a.AnchorStart + (long)a.DurationNs; }
        }
        chunk.RemoveAll(a => !slow.Accepts(a));
        if (chunk.Count == 0) return [];
        await FillSpanCountsAsync(conn, chunk, service, ct);
        return chunk.Select(a => ToInfo(a)).ToList();
    }

    private const long NanosPerDay = 86_400_000_000_000L;

    /// <summary>Each trace's earliest span start and latest span end among its spans starting in <c>[from, to]</c>.</summary>
    private async Task<Dictionary<Guid, TraceBoundsRow>> SpanBoundsAsync(DbConnection conn, Guid[] ids, long from, long to, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("ids", ids);
        p.Add("from", from);
        p.Add("to", to);
        return (await conn.QueryAsync<TraceBoundsRow>(new CommandDefinition($"""
            SELECT trace_id AS TraceId, toUnixTimestamp64Nano(min(start_time)) AS TraceMin, max(toUnixTimestamp64Nano(start_time) + toInt64(duration_ns)) AS TraceMax
            FROM spans
            WHERE tenant_id = @tenantId AND trace_id IN @ids AND start_time >= {At("from")} AND start_time <= {At("to")}
            GROUP BY trace_id
            """, p, cancellationToken: ct))).ToDictionary(r => r.TraceId);
    }

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class SlowTraceRow
    {
        public Guid TraceId { get; set; }
        public long TraceMin { get; set; }
        public long TraceMax { get; set; }
        public byte HasError { get; set; }
    }

    private sealed class IndexRow
    {
        public Guid TraceId { get; set; }
        public long ScopeMin { get; set; }
        public byte ScopeError { get; set; }
        public long TraceMin { get; set; }
        public long TraceMax { get; set; }
    }

    private sealed class TraceBoundsRow
    {
        public Guid TraceId { get; set; }
        public long TraceMin { get; set; }
        public long TraceMax { get; set; }
    }

    private sealed class CountRow
    {
        public Guid TraceId { get; set; }
        public ulong SpanCount { get; set; }
    }

    private sealed class BoundsRow
    {
        public ulong Cnt { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
    }

    private sealed class DependencyRow
    {
        public string ParentService { get; set; } = "";
        public string ChildService { get; set; } = "";
        public string Kind { get; set; } = "";
        public ulong CallCount { get; set; }
        public double AvgNs { get; set; }
        public ulong MinNs { get; set; }
        public ulong MaxNs { get; set; }
        public ulong ErrorCount { get; set; }
    }

    private sealed class OperationRollupRow
    {
        public string Operation { get; set; } = "";
        public long Requests { get; set; }
        public long Errors { get; set; }
        public long SumNanos { get; set; }
        public long MaxNanos { get; set; }
        public long H00 { get; set; } public long H01 { get; set; } public long H02 { get; set; } public long H03 { get; set; }
        public long H04 { get; set; } public long H05 { get; set; } public long H06 { get; set; } public long H07 { get; set; }
        public long H08 { get; set; } public long H09 { get; set; } public long H10 { get; set; } public long H11 { get; set; }
        public long H12 { get; set; } public long H13 { get; set; } public long H14 { get; set; } public long H15 { get; set; }
        public long H16 { get; set; } public long H17 { get; set; } public long H18 { get; set; } public long H19 { get; set; }
        public long H20 { get; set; } public long H21 { get; set; } public long H22 { get; set; } public long H23 { get; set; }

        public long[] Bands() =>
        [
            H00, H01, H02, H03, H04, H05, H06, H07, H08, H09, H10, H11, H12, H13, H14, H15, H16, H17, H18, H19, H20, H21, H22, H23
        ];
    }

    private sealed class SpanRow
    {
        public Guid TraceId { get; set; }
        public ulong SpanId { get; set; }
        public ulong ParentSpanId { get; set; }
        public string SpanName { get; set; } = "";
        public string Kind { get; set; } = "UNSPECIFIED";
        public long StartNano { get; set; }
        public ulong DurationNs { get; set; }
        public uint DroppedAttributes { get; set; }
        public uint DroppedEvents { get; set; }
        public uint DroppedLinks { get; set; }
        public string? TraceState { get; set; }
        public uint SpanFlags { get; set; }
        public string StatusCode { get; set; } = "UNSET";
        public string? StatusMessage { get; set; }
        public Dictionary<string, string>? SpanAttributes { get; set; }
        public long[] EventTimes { get; set; } = [];
        public string[] EventNames { get; set; } = [];
        public Dictionary<string, string>[] EventAttributes { get; set; } = [];
        public uint[] EventDropped { get; set; } = [];
        public Guid[] LinkTraceIds { get; set; } = [];
        public ulong[] LinkSpanIds { get; set; } = [];
        public string[] LinkStates { get; set; } = [];
        public uint[] LinkFlags { get; set; } = [];
        public Dictionary<string, string>[] LinkAttributes { get; set; } = [];
        public uint[] LinkDropped { get; set; } = [];
        public Dictionary<string, string>? ResourceAttributes { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public Dictionary<string, string>? ScopeAttributes { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public ulong ResourceKey { get; set; }
        public ulong ScopeKey { get; set; }
    }
}
