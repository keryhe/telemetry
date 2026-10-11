using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// <see cref="ITraceReadRepository"/> over the Parquet row model (plans/parquet-duckdb phase 3), following
/// <c>ClickHouseTraceReadRepository</c> method by method with DuckDB SQL: it implements the contract directly because the
/// shared base's SQL assumes reference tables.
///
/// <b>Views.</b> Each method opens a connection with the temp views its statements need (README D5): <c>spans</c> over the
/// hours its time range names, <c>trace_index</c> over a day further back, then reads them as plain tables.
///
/// <b>Anchors.</b> A trace is listed once, by its <i>anchor</i>: the earliest span in scope (the selected service's own
/// spans, else the whole trace's), ranked by <c>(start_time_unix_nano, span_id)</c>. The list reads <i>slices</i> of start
/// times from the window's newest (or oldest) end, widened until it has <c>limit + 1</c> anchors. Within a slice each trace's
/// earliest span is its <i>candidate</i>; the candidate is the anchor when its start equals the trace's earliest start in scope
/// according to <c>trace_index</c>. A trace that began more than a day before the slice is anchored on its earliest span inside
/// the day looked up (accepted, as on ClickHouse). The error flag and whole-trace bounds come from <c>trace_index</c>; the span
/// count is exact (<c>COUNT(DISTINCT span_id)</c>) because a re-delivered span is stored twice.
///
/// <b>Detail.</b> The trace's hours come from the <c>?start=&amp;end=</c> hint, else from <c>trace_index</c> (which carries the
/// trace id in sorted order, so the lookup prunes on file statistics); then only those hours' span files are scanned for
/// <c>trace_hi = ? AND trace_lo = ?</c> (phase 0 spike 2). A hint that finds nothing falls back to the unhinted read.
/// </summary>
public sealed class ParquetTraceReadRepository : DapperReadRepository, ITraceReadRepository
{
    private const long NanosPerMinute = 60_000_000_000L;
    private const long NanosPerSecond = 1_000_000_000L;
    private const long NanosPerDay = 86_400_000_000_000L;
    private const int ExportChunkSize = 1000;
    private const int IdChunkSize = 1000;

    private readonly ParquetQueryEngine _engine;
    private readonly int _summaryTimeoutSeconds;
    private readonly long _anchorLookbackNanos;
    private readonly long _pageSliceNanos;
    private readonly int _pageSliceGrowth;
    private readonly bool _traceHintEnabled;
    private readonly long _traceHintMarginNanos;

    public ParquetTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext, ParquetQueryEngine engine) : base(tenantContext)
    {
        _engine = engine;
        string? Setting(string key) => configuration[$"{QueryOptions.SectionName}:{key}"];
        _summaryTimeoutSeconds = int.TryParse(Setting("SummaryTimeoutSeconds"), out var timeout) ? timeout : 5;
        _anchorLookbackNanos = (int.TryParse(Setting("AnchorLookbackMinutes"), out var lookback) && lookback >= 0 ? lookback : QueryOptions.DefaultAnchorLookbackMinutes) * NanosPerMinute;
        _pageSliceNanos = (int.TryParse(Setting("PageSliceSeconds"), out var slice) && slice >= 1 ? slice : QueryOptions.DefaultPageSliceSeconds) * NanosPerSecond;
        _pageSliceGrowth = int.TryParse(Setting("PageSliceGrowth"), out var growth) && growth >= 2 ? growth : QueryOptions.DefaultPageSliceGrowth;
        _traceHintEnabled = !bool.TryParse(Setting("TraceHintEnabled"), out var hintEnabled) || hintEnabled;
        _traceHintMarginNanos = (int.TryParse(Setting("TraceHintMarginMinutes"), out var margin) && margin >= 0 ? margin : QueryOptions.DefaultTraceHintMarginMinutes) * NanosPerMinute;
    }

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await _engine.OpenAsync([], cancellationToken);

    private ParquetViewScope SpanScope(long? from, long? to) => new(ParquetTables.SpanTable, TenantId, from, to);
    private ParquetViewScope IndexScope(long? from, long? to) => new(ParquetTables.TraceIndexTable, TenantId, from, to);
    private ParquetViewScope RollupScope(long? from, long? to) => new(ParquetTables.RequestRollupTable, TenantId, from, to);

    private Task<NamedParameterConnection> OpenAsync(CancellationToken ct, params ParquetViewScope[] scopes) => _engine.OpenAsync(scopes, ct);

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

        await using var conn = await OpenAsync(cancellationToken);
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

        await using var conn = await OpenAsync(cancellationToken);
        var spans = await LoadSpansAsync(conn, trace, span, null, await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken);
        return spans.FirstOrDefault();
    }

    public async Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex)) throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(parentSpanIdHex)) throw new ArgumentException("Parent Span ID cannot be null or empty", nameof(parentSpanIdHex));
        var trace = ParseTraceId(traceIdHex);
        var parent = ParseSpanId(parentSpanIdHex);

        await using var conn = await OpenAsync(cancellationToken);
        return DistinctSpans(await LoadSpansAsync(conn, trace, null, parent, await TraceBoundsAsync(conn, trace, cancellationToken), cancellationToken));
    }

    // A malformed id is a client error, never a DuckDB exception.
    private static (ulong Hi, ulong Lo) ParseTraceId(string hex)
        => ParquetIds.TryTraceId(hex, out var id) ? id : throw new ArgumentException("Trace ID must be 32 hex characters", nameof(hex));

    private static ulong ParseSpanId(string hex)
        => ParquetIds.TrySpanId(hex, out var id) ? id : throw new ArgumentException("Span ID must be 16 hex characters", nameof(hex));

    /// <summary>The trace's whole extent from <c>trace_index</c> (every hour the tenant has); null when the index has no row for it.</summary>
    private async Task<(long Min, long Max)?> TraceBoundsAsync(NamedParameterConnection conn, (ulong Hi, ulong Lo) trace, CancellationToken ct)
    {
        await _engine.DefineAsync(conn, IndexScope(null, null), ct);
        var row = await conn.QuerySingleAsync<BoundsRow>(new CommandDefinition("""
            SELECT count(*) AS Cnt, CAST(min(start_min) AS BIGINT) AS MinStart, CAST(max(end_max) AS BIGINT) AS MaxEnd
            FROM trace_index
            WHERE tenant_id = @tenantId AND trace_hi = @hi AND trace_lo = @lo
            """, new { tenantId = TenantId, hi = trace.Hi, lo = trace.Lo }, cancellationToken: ct));
        return row.Cnt == 0 ? null : (row.MinStart, row.MaxEnd);
    }

    private static List<SpanModel> DistinctSpans(List<SpanModel> spans)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return spans.Where(s => seen.Add(s.SpanIdHex)).ToList();
    }

    private async Task<List<SpanModel>> LoadSpansAsync(
        NamedParameterConnection conn, (ulong Hi, ulong Lo) trace, ulong? spanId, ulong? parentSpanId, (long Min, long Max)? bounds, CancellationToken ct)
    {
        // No bounds means the index knows nothing of the trace: read nothing rather than every span file.
        if (bounds is not { } b) return [];
        await _engine.DefineAsync(conn, SpanScope(b.Min, b.Max), ct);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("hi", trace.Hi);
        parameters.Add("lo", trace.Lo);
        parameters.Add("boundMin", b.Min);
        parameters.Add("boundMax", b.Max);
        var where = new StringBuilder("tenant_id = @tenantId AND trace_hi = @hi AND trace_lo = @lo AND start_time_unix_nano >= @boundMin AND start_time_unix_nano <= @boundMax");
        if (spanId is { } sid) { where.Append(" AND span_id = @spanId"); parameters.Add("spanId", sid); }
        if (parentSpanId is { } pid) { where.Append(" AND parent_span_id = @parentSpanId"); parameters.Add("parentSpanId", pid); }

        var sql = $"""
            SELECT trace_hi AS TraceHi, trace_lo AS TraceLo, span_id AS SpanId, parent_span_id AS ParentSpanId, span_name AS SpanName,
                   kind AS Kind, start_time_unix_nano AS StartNano, end_time_unix_nano AS EndNano,
                   dropped_attributes_count AS DroppedAttributes, dropped_events_count AS DroppedEvents, dropped_links_count AS DroppedLinks,
                   trace_state AS TraceState, flags AS SpanFlags, status_code AS StatusCode, status_message AS StatusMessage,
                   to_json(attributes) AS SpanAttributes, to_json(events) AS EventsJson, to_json(links) AS LinksJson,
                   to_json(resource_attributes) AS ResourceAttributes, resource_schema_url AS ResourceSchemaUrl,
                   scope_name AS ScopeName, scope_version AS ScopeVersion, to_json(scope_attributes) AS ScopeAttributes, scope_schema_url AS ScopeSchemaUrl,
                   hash(CAST(resource_attributes AS VARCHAR), resource_schema_url) AS ResourceKey,
                   hash(CAST(scope_attributes AS VARCHAR), scope_name, scope_version, scope_schema_url) AS ScopeKey
            FROM spans
            WHERE {where}
            ORDER BY start_time_unix_nano, span_id
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

            result.Add(new SpanModel
            {
                TraceIdHex = ParquetIds.TraceIdHex(r.TraceHi, r.TraceLo),
                SpanIdHex = ParquetIds.SpanIdHex(r.SpanId),
                ParentSpanIdHex = r.ParentSpanId == 0 ? null : ParquetIds.SpanIdHex(r.ParentSpanId),
                Name = r.SpanName,
                Kind = Enum.Parse<SpanKind>(r.Kind),
                StartTimeUnixNano = r.StartNano,
                EndTimeUnixNano = r.EndNano,
                DroppedAttributesCount = (int)r.DroppedAttributes,
                DroppedEventsCount = (int)r.DroppedEvents,
                DroppedLinksCount = (int)r.DroppedLinks,
                TraceState = NullIfEmpty(r.TraceState),
                Flags = (int)r.SpanFlags,
                StatusCode = Enum.Parse<SpanStatusCode>(r.StatusCode),
                StatusMessage = NullIfEmpty(r.StatusMessage),
                Attributes = ToObjects(r.SpanAttributes),
                Events = ParseEvents(r.EventsJson),
                Links = ParseLinks(r.LinksJson),
                Resource = resource,
                InstrumentationScope = scope
            });
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static Dictionary<string, object>? ToObjects(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, object>();
        foreach (var p in doc.RootElement.EnumerateObject()) result[p.Name] = p.Value.GetString() ?? "";
        return result.Count == 0 ? null : result;
    }

    private static List<SpanEventModel> ParseEvents(string? json)
    {
        var events = new List<SpanEventModel>();
        if (string.IsNullOrEmpty(json)) return events;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return events;
        foreach (var e in doc.RootElement.EnumerateArray())
            events.Add(new SpanEventModel
            {
                Name = e.GetProperty("name").GetString() ?? "",
                TimeUnixNano = e.GetProperty("time_unix_nano").GetInt64(),
                DroppedAttributesCount = (int)e.GetProperty("dropped_attributes_count").GetUInt64(),
                Attributes = ToObjects(e.GetProperty("attributes").GetRawText())
            });
        return events;
    }

    private static List<SpanLinkModel> ParseLinks(string? json)
    {
        var links = new List<SpanLinkModel>();
        if (string.IsNullOrEmpty(json)) return links;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return links;
        foreach (var l in doc.RootElement.EnumerateArray())
            links.Add(new SpanLinkModel
            {
                LinkedTraceIdHex = ParquetIds.TraceIdHex(l.GetProperty("trace_hi").GetUInt64(), l.GetProperty("trace_lo").GetUInt64()),
                LinkedSpanIdHex = ParquetIds.SpanIdHex(l.GetProperty("span_id").GetUInt64()),
                TraceState = NullIfEmpty(l.GetProperty("trace_state").GetString()),
                Flags = (int)l.GetProperty("flags").GetUInt64(),
                DroppedAttributesCount = (int)l.GetProperty("dropped_attributes_count").GetUInt64(),
                Attributes = ToObjects(l.GetProperty("attributes").GetRawText())
            });
        return links;
    }

    // =========================================================================
    // LIST
    // =========================================================================

    private sealed class Anchor
    {
        public ulong TraceHi { get; set; }
        public ulong TraceLo { get; set; }
        public ulong AnchorSpanId { get; set; }
        public long AnchorStart { get; set; }
        public long DurationNs { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "UNSPECIFIED";
        public string Service { get; set; } = "";
        public byte HasErrorInt { get; set; }

        // filled by the trace_index confirmation
        public long TraceMin, TraceMax;
        public bool HasError;
        public int SpanCount = 1;

        public (ulong, ulong) Key => (TraceHi, TraceLo);
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

        await using var conn = await OpenWindowAsync(startNano, endNano, cancellationToken);
        var filter = new CandidateFilter(query.Mode, service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed);

        var anchors = await FetchSlicedAnchorsAsync(conn, filter, limit + 1, descending, startNano, endNano + 1, cancellationToken);
        var truncated = anchors.Count > limit;
        if (truncated) anchors.RemoveAt(anchors.Count - 1);

        await FillSpanCountsAsync(conn, anchors, service, cancellationToken);
        return new TraceListResult { Items = anchors.Select(ToInfo).ToList(), Truncated = truncated };
    }

    /// <summary>A connection over the spans of a window (extended back by the look-back margin) and the trace index a day further back.</summary>
    private Task<NamedParameterConnection> OpenWindowAsync(long startNano, long endNano, CancellationToken ct)
        => OpenAsync(ct, SpanScope(startNano - _anchorLookbackNanos, endNano), IndexScope(startNano - NanosPerDay - _anchorLookbackNanos, endNano + NanosPerDay));

    private static TraceInfo ToInfo(Anchor a) => new()
    {
        TraceIdHex = ParquetIds.TraceIdHex(a.TraceHi, a.TraceLo),
        SpanCount = a.SpanCount,
        TraceStartTime = TimeConversion.UnixNanoToDateTime(a.TraceMin),
        TraceEndTime = TimeConversion.UnixNanoToDateTime(a.TraceMax),
        TraceDuration = TimeSpan.FromTicks(a.DurationNs / 100),
        ServiceName = string.IsNullOrEmpty(a.Service) ? null : a.Service,
        RootOperationName = a.Name,
        AnchorKind = a.Kind,
        HasErrors = a.HasError,
        DisplaySpanIdHex = ParquetIds.SpanIdHex(a.AnchorSpanId)
    };

    /// <summary>The filters of a list, compiled once into the predicates every slice shares.</summary>
    private sealed class CandidateFilter
    {
        public readonly string? Service;
        public readonly bool Selective;          // slow mode filters on the anchor's own duration: one pass over the range
        public readonly string SpanPredicates;   // AND ... terms on the candidate span
        public readonly bool ErrorsOnly;
        public readonly DynamicParameters Parameters = new();

        public CandidateFilter(string mode, string? service, string? operation, double? minDurationMs, double? maxDurationMs, ParsedSearchQuery parsed)
        {
            Service = service;
            var sql = new StringBuilder();
            if (service is not null) Parameters.Add("service", service);
            if (!string.IsNullOrEmpty(operation))
            {
                sql.Append(" AND span_name = @operation");
                Parameters.Add("operation", operation);
            }
            if (mode == "slow")
            {
                sql.Append(" AND duration_ns >= @minDuration");
                Parameters.Add("minDuration", Math.Max(0, (long)((minDurationMs ?? 500) * 1_000_000)));
                if (maxDurationMs.HasValue)
                {
                    sql.Append(" AND duration_ns <= @maxDuration");
                    Parameters.Add("maxDuration", Math.Max(0, (long)(maxDurationMs.Value * 1_000_000)));
                }
                Selective = true;
            }
            ErrorsOnly = mode == "errors";
            if (parsed.IsTraceIdSearch)
            {
                var ok = ParquetIds.TryTraceId(parsed.TraceId, out var id);
                sql.Append(" AND trace_hi = @searchHi AND trace_lo = @searchLo");
                Parameters.Add("searchHi", ok ? id.Hi : 0UL);
                Parameters.Add("searchLo", ok ? id.Lo : 0UL);
            }
            SpanPredicates = sql.ToString();
            SearchSql = parsed.IsTraceIdSearch ? "" : BuildSearch(parsed, Parameters);
        }

        /// <summary>The <c>AND (trace_hi, trace_lo) IN (...)</c> clauses of the search terms.</summary>
        public string SearchSql { get; }
    }

    /// <summary>
    /// One clause per search term: a term matches if ANY span of the trace in the search window matches, regardless of the
    /// service filter (each term may match a different span).
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
                string Match(string map) => $"(lower({map}[@searchKey{i}]) = @searchVal{i})";
                // A negated term is the NOT of the positive match on the span or its resource: a span carrying the
                // value on either is excluded, one with neither is kept.
                var positive = $"({Match("attributes")} OR {Match("resource_attributes")})";
                predicate = term.Negate ? $"NOT COALESCE({positive}, false)" : $"COALESCE({positive}, false)";
            }
            else
            {
                parameters.Add($"searchText{i}", (term.FreeText ?? "").ToLowerInvariant());
                var m = $"(contains(lower(span_name), @searchText{i}) OR contains(lower(status_message), @searchText{i}))";
                predicate = term.Negate ? $"NOT {m}" : m;
            }
            sb.Append($" AND (trace_hi, trace_lo) IN (SELECT trace_hi, trace_lo FROM spans WHERE tenant_id = @tenantId AND start_time_unix_nano >= @searchFrom AND start_time_unix_nano <= @searchTo AND {predicate})");
            i++;
        }
        return sb.ToString();
    }

    private void Bind(CandidateFilter f, long startNano, long endNano)
    {
        f.Parameters.Add("tenantId", TenantId);
        f.Parameters.Add("searchFrom", startNano - _anchorLookbackNanos);
        f.Parameters.Add("searchTo", endNano);
    }

    private async Task<List<Anchor>> FetchSlicedAnchorsAsync(
        NamedParameterConnection conn, CandidateFilter f, int need, bool descending, long rangeFrom, long rangeTo, CancellationToken ct)
    {
        Bind(f, rangeFrom, rangeTo - 1);
        var rows = new List<Anchor>(need);
        var width = f.Selective ? Math.Max(1, rangeTo - rangeFrom) : _pageSliceNanos;
        long lo = rangeFrom, hi = rangeTo;

        while (rows.Count < need && lo < hi)
        {
            long sliceFrom, sliceTo;
            if (descending) { sliceTo = hi; sliceFrom = Math.Max(lo, hi - width); }
            else { sliceFrom = lo; sliceTo = Math.Min(hi, lo + width); }

            var candidates = await CandidatesAsync(conn, f, sliceFrom, sliceTo, ct);
            var confirmed = await ConfirmAsync(conn, candidates, f.Service, ct);
            var ordered = descending
                ? confirmed.OrderByDescending(a => a.AnchorStart).ThenByDescending(a => a.AnchorSpanId)
                : confirmed.OrderBy(a => a.AnchorStart).ThenBy(a => a.AnchorSpanId);
            rows.AddRange(ordered.Take(need - rows.Count));

            if (descending) hi = sliceFrom; else lo = sliceTo;
            var remaining = hi - lo;
            width = width > long.MaxValue / _pageSliceGrowth ? remaining : width * _pageSliceGrowth;
            if (width >= remaining / 2) width = Math.Max(1, remaining);
        }
        return rows;
    }

    private const string TieBreak = "(start_time_unix_nano, span_id)";

    /// <summary>Each trace's earliest matching span in the slice (before confirmation against trace_index).</summary>
    private async Task<List<Anchor>> CandidatesAsync(NamedParameterConnection conn, CandidateFilter f, long sliceFrom, long sliceTo, CancellationToken ct)
    {
        var service = f.Service is null ? "" : " AND service_name = @service";
        var errors = f.ErrorsOnly
            ? $" AND (trace_hi, trace_lo) IN (SELECT trace_hi, trace_lo FROM trace_index WHERE tenant_id = @tenantId AND has_error = 1{service} AND start_min >= @sliceFrom - {NanosPerDay} AND start_min < @sliceTo + {NanosPerDay})"
            : "";
        var sql = $"""
            SELECT trace_hi AS TraceHi, trace_lo AS TraceLo,
                   arg_min(span_id, {TieBreak}) AS AnchorSpanId,
                   CAST(min(start_time_unix_nano) AS BIGINT) AS AnchorStart,
                   arg_min(duration_ns, {TieBreak}) AS DurationNs,
                   arg_min(span_name, {TieBreak}) AS Name,
                   arg_min(kind, {TieBreak}) AS Kind,
                   arg_min(service_name, {TieBreak}) AS Service
            FROM spans
            WHERE tenant_id = @tenantId{service} AND start_time_unix_nano >= @sliceFrom AND start_time_unix_nano < @sliceTo{f.SpanPredicates}{errors}{f.SearchSql}
            GROUP BY trace_hi, trace_lo
            """;
        var p = new DynamicParameters(f.Parameters);
        p.Add("sliceFrom", sliceFrom);
        p.Add("sliceTo", sliceTo);
        return (await conn.QueryAsync<Anchor>(new CommandDefinition(sql, p, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Keeps the candidates that are their trace's anchor (start equals the trace's earliest start in scope per
    /// <c>trace_index</c>) and fills the whole-trace bounds and the scoped error flag.
    /// </summary>
    private async Task<List<Anchor>> ConfirmAsync(NamedParameterConnection conn, List<Anchor> candidates, string? service, CancellationToken ct)
    {
        var confirmed = new List<Anchor>();
        for (var offset = 0; offset < candidates.Count; offset += IdChunkSize)
        {
            var chunk = candidates.Skip(offset).Take(IdChunkSize).ToList();
            var index = await TraceIndexAsync(conn, chunk.Select(c => c.Key).ToArray(), service, ct);
            foreach (var c in chunk)
            {
                if (!index.TryGetValue(c.Key, out var row) || row.ScopeMin != c.AnchorStart) continue;
                c.TraceMin = row.TraceMin; c.TraceMax = row.TraceMax; c.HasError = row.ScopeError != 0;
                confirmed.Add(c);
            }
        }
        return confirmed;
    }

    /// <summary>A literal tuple list for the ids: they are unsigned integers parsed from hex, never user text.</summary>
    private static string IdTuples(IEnumerable<(ulong Hi, ulong Lo)> ids)
        => string.Join(", ", ids.Select(i => $"({i.Hi}, {i.Lo})"));

    private async Task<Dictionary<(ulong, ulong), IndexRow>> TraceIndexAsync(
        NamedParameterConnection conn, (ulong Hi, ulong Lo)[] ids, string? service, CancellationToken ct)
    {
        var scopeMin = service is null ? "min(start_min)" : "min(start_min) FILTER (WHERE service_name = @service)";
        var scopeError = service is null ? "max(has_error)" : "max(has_error) FILTER (WHERE service_name = @service)";
        var sql = $"""
            SELECT trace_hi AS TraceHi, trace_lo AS TraceLo,
                   CAST(COALESCE({scopeMin}, -1) AS BIGINT) AS ScopeMin, CAST(COALESCE({scopeError}, 0) AS UTINYINT) AS ScopeError,
                   CAST(min(start_min) AS BIGINT) AS TraceMin, CAST(max(end_max) AS BIGINT) AS TraceMax
            FROM trace_index
            WHERE tenant_id = @tenantId AND (trace_hi, trace_lo) IN ({IdTuples(ids)})
            GROUP BY trace_hi, trace_lo
            """;
        var p = new DynamicParameters();
        p.Add("tenantId", TenantId);
        if (service is not null) p.Add("service", service);
        return (await conn.QueryAsync<IndexRow>(new CommandDefinition(sql, p, cancellationToken: ct))).ToDictionary(r => (r.TraceHi, r.TraceLo));
    }

    /// <summary>Exact span counts (a re-delivered span counts once) for the page's traces, scoped to the service when one is selected.</summary>
    private async Task FillSpanCountsAsync(NamedParameterConnection conn, List<Anchor> anchors, string? service, CancellationToken ct)
    {
        if (anchors.Count == 0) return;
        // A trace can reach further back (or on) than the window the connection was opened with.
        var minStart = anchors.Min(a => a.TraceMin);
        var maxStart = anchors.Max(a => a.TraceMax);
        await _engine.DefineAsync(conn, SpanScope(minStart, maxStart), ct);

        var p = new DynamicParameters();
        p.Add("tenantId", TenantId);
        p.Add("minStart", minStart);
        p.Add("maxStart", maxStart);
        var scope = service is null ? "" : " AND service_name = @service";
        if (service is not null) p.Add("service", service);
        var rows = await conn.QueryAsync<CountRow>(new CommandDefinition($"""
            SELECT trace_hi AS TraceHi, trace_lo AS TraceLo, count(DISTINCT span_id) AS SpanCount
            FROM spans
            WHERE tenant_id = @tenantId AND (trace_hi, trace_lo) IN ({IdTuples(anchors.Select(a => a.Key))})
              AND start_time_unix_nano >= @minStart AND start_time_unix_nano <= @maxStart{scope}
            GROUP BY trace_hi, trace_lo
            """, p, cancellationToken: ct));
        var counts = rows.ToDictionary(r => (r.TraceHi, r.TraceLo), r => (int)r.SpanCount);
        foreach (var a in anchors) if (counts.TryGetValue(a.Key, out var n)) a.SpanCount = n;
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
            await using var conn = await OpenWindowAsync(startNano, endNano, ct);
            return query.Kind == "errors"
                ? await FetchSlicedAnchorsAsync(conn, new CandidateFilter("errors", null, null, null, null, ParsedSearchQuery.Empty),
                    limit, descending: true, startNano, endNano + 1, ct)
                : await SlowestAnchorsAsync(conn, limit, startNano, endNano, ct);
        }, _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || anchors is null) return new TraceSamplesResult { TimedOut = true };

        // The follow-up runs on its own connection: the one the (possibly aborted) first stage used is never reused.
        await using var followUp = await OpenAsync(cancellationToken);
        await FillSpanCountsAsync(followUp, anchors, null, cancellationToken);
        return new TraceSamplesResult { Items = anchors.Select(ToInfo).ToList() };
    }

    /// <summary>
    /// The slowest anchors above a 500 ms floor: the window's longest spans, each checked for being its trace's earliest.
    /// The candidate cap grows until enough anchors are found or the spans run out.
    /// </summary>
    private async Task<List<Anchor>> SlowestAnchorsAsync(NamedParameterConnection conn, int limit, long startNano, long endNano, CancellationToken ct)
    {
        var take = limit * 20;
        while (true)
        {
            var p = new DynamicParameters();
            p.Add("tenantId", TenantId);
            p.Add("start", startNano);
            p.Add("end", endNano);
            p.Add("floor", 500_000_000L);
            p.Add("take", take);
            var spans = (await conn.QueryAsync<Anchor>(new CommandDefinition("""
                SELECT trace_hi AS TraceHi, trace_lo AS TraceLo, span_id AS AnchorSpanId, start_time_unix_nano AS AnchorStart,
                       duration_ns AS DurationNs, span_name AS Name, kind AS Kind, service_name AS Service
                FROM spans
                WHERE tenant_id = @tenantId AND start_time_unix_nano >= @start AND start_time_unix_nano <= @end AND duration_ns > @floor
                ORDER BY duration_ns DESC, span_id DESC
                LIMIT @take
                """, p, cancellationToken: ct))).ToList();

            // one candidate per trace (a re-delivered span appears twice)
            var perTrace = spans.GroupBy(s => s.Key).Select(g => g.OrderBy(s => s.AnchorStart).ThenBy(s => s.AnchorSpanId).First()).ToList();
            var confirmed = await ConfirmAsync(conn, perTrace, null, ct);
            var best = confirmed.OrderByDescending(a => a.DurationNs).ThenByDescending(a => a.AnchorSpanId).Take(limit).ToList();
            if (best.Count >= limit || spans.Count < take) return best;
            take *= 4;
        }
    }

    public async Task<SlowRequestCount> CountSlowInboundSpansAsync(
        DateTime start, DateTime end, string? service, double minDurationMs, CancellationToken cancellationToken = default)
    {
        var startNano = TimeConversion.DateTimeToUnixNano(start);
        var endNano = TimeConversion.DateTimeToUnixNano(end);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);
        parameters.Add("minNanos", Math.Max(0, (long)Math.Ceiling(minDurationMs * 1_000_000.0)));
        var serviceFilter = "";
        if (service != null)
        {
            parameters.Add("service", service);
            serviceFilter = " AND service_name = @service";
        }

        var sql = $"""
            SELECT count(*) FROM spans
            WHERE tenant_id = @tenantId AND start_time_unix_nano >= @start AND start_time_unix_nano < @end
              AND kind IN ('SERVER', 'CONSUMER') AND duration_ns >= @minNanos{serviceFilter}
            """;
        var (count, timedOut) = await TimedQuery.RunAsync(async (timeoutSeconds, ct) =>
        {
            await using var conn = await OpenAsync(ct, SpanScope(startNano, endNano));
            return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct));
        }, _summaryTimeoutSeconds, cancellationToken);
        return new SlowRequestCount(timedOut ? 0 : count, timedOut);
    }

    // =========================================================================
    // ANALYSIS
    // =========================================================================

    public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        // A child's parent is found by (trace id, span id) among the spans of the same window (extended back by the look-back
        // margin so a parent that started just before the window is found).
        var p = new DynamicParameters();
        p.Add("tenantId", TenantId);
        var child = "";
        var parent = "";
        long? from = null, to = null;
        if (startTime.HasValue)
        {
            var s = TimeConversion.DateTimeToUnixNano(startTime.Value);
            p.Add("start", s);
            p.Add("parentFrom", s - _anchorLookbackNanos);
            child += " AND c.start_time_unix_nano >= @start";
            parent += " AND start_time_unix_nano >= @parentFrom";
            from = s - _anchorLookbackNanos;
        }
        if (endTime.HasValue)
        {
            var e = TimeConversion.DateTimeToUnixNano(endTime.Value);
            p.Add("end", e);
            child += " AND c.start_time_unix_nano <= @end";
            parent += " AND start_time_unix_nano <= @end";
            to = e;
        }

        var sql = $"""
            SELECT pr.service_name AS ParentService, c.service_name AS ChildService, c.kind AS Kind,
                   count(*) AS CallCount, avg(c.duration_ns) AS AvgNs, CAST(min(c.duration_ns) AS BIGINT) AS MinNs,
                   CAST(max(c.duration_ns) AS BIGINT) AS MaxNs, count(*) FILTER (WHERE c.status_code = 'ERROR') AS ErrorCount
            FROM spans AS c
            INNER JOIN (
                SELECT trace_hi, trace_lo, span_id, any_value(service_name) AS service_name
                FROM spans
                WHERE tenant_id = @tenantId{parent}
                GROUP BY trace_hi, trace_lo, span_id
            ) AS pr ON c.trace_hi = pr.trace_hi AND c.trace_lo = pr.trace_lo AND c.parent_span_id = pr.span_id
            WHERE c.tenant_id = @tenantId{child} AND c.service_name != '' AND pr.service_name != '' AND c.service_name != pr.service_name
            GROUP BY pr.service_name, c.service_name, c.kind
            ORDER BY CallCount DESC
            """;
        await using var conn = await OpenAsync(cancellationToken, SpanScope(from, to));
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
    /// every span, and the percentiles are the rollup's approximate ones.
    /// </summary>
    private async Task<List<OperationRollupRow>> ReadOperationRollupAsync(string service, DateTime? start, DateTime? end, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("tenantId", TenantId);
        p.Add("service", service);
        var where = "";
        long? from = null, to = null;
        if (start.HasValue) { from = TimeConversion.DateTimeToUnixNano(start.Value); where += " AND bucket_start_unix_nano >= @start"; p.Add("start", from); }
        if (end.HasValue) { to = TimeConversion.DateTimeToUnixNano(end.Value); where += " AND bucket_start_unix_nano < @end"; p.Add("end", to); }
        var bands = string.Join(", ", Enumerable.Range(0, DurationBands.Count).Select(i => $"CAST(sum(h{i:00}) AS BIGINT) AS H{i:00}"));
        await using var conn = await OpenAsync(ct, RollupScope(from, to));
        return (await conn.QueryAsync<OperationRollupRow>(new CommandDefinition($"""
            SELECT operation AS Operation, CAST(sum(request_count) AS BIGINT) AS Requests, CAST(sum(error_count) AS BIGINT) AS Errors,
                   CAST(sum(sum_duration_nanos) AS BIGINT) AS SumNanos, CAST(max(max_duration_nanos) AS BIGINT) AS MaxNanos, {bands}
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
    /// (<c>GROUP BY</c> the trace with <c>arg_min</c>; export reads the whole window anyway) and streamed; every
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
        var f = new CandidateFilter("all", service, null, null, null, parsed);
        Bind(f, startNano, endNano);
        f.Parameters.Add("anchorFrom", startNano - _anchorLookbackNanos);
        f.Parameters.Add("start", startNano);
        f.Parameters.Add("end", endNano);

        var having = new StringBuilder("min(start_time_unix_nano) >= @start");
        if (!string.IsNullOrEmpty(query.Operation)) { having.Append($" AND arg_min(span_name, {TieBreak}) = @operation"); f.Parameters.Add("operation", query.Operation); }
        if (query.Mode == "errors") having.Append(" AND bool_or(status_code = 'ERROR')");
        if (query.Mode == "slow")
        {
            having.Append($" AND arg_min(duration_ns, {TieBreak}) >= @minDuration");
            f.Parameters.Add("minDuration", Math.Max(0, (long)((query.MinDurationMs ?? 500) * 1_000_000)));
            if (query.MaxDurationMs.HasValue)
            {
                having.Append($" AND arg_min(duration_ns, {TieBreak}) <= @maxDuration");
                f.Parameters.Add("maxDuration", Math.Max(0, (long)(query.MaxDurationMs.Value * 1_000_000)));
            }
        }
        var traceIdFilter = f.SpanPredicates.Contains("searchHi") ? " AND trace_hi = @searchHi AND trace_lo = @searchLo" : "";

        var sql = $"""
            SELECT trace_hi AS TraceHi, trace_lo AS TraceLo,
                   arg_min(span_id, {TieBreak}) AS AnchorSpanId,
                   CAST(min(start_time_unix_nano) AS BIGINT) AS AnchorStart,
                   arg_min(duration_ns, {TieBreak}) AS DurationNs,
                   arg_min(span_name, {TieBreak}) AS Name,
                   arg_min(kind, {TieBreak}) AS Kind,
                   arg_min(service_name, {TieBreak}) AS Service
            FROM spans
            WHERE tenant_id = @tenantId{(service is null ? "" : " AND service_name = @service")}
              AND start_time_unix_nano >= @anchorFrom AND start_time_unix_nano <= @end{traceIdFilter}{f.SearchSql}
            GROUP BY trace_hi, trace_lo
            HAVING {having}
            ORDER BY AnchorStart ASC, AnchorSpanId ASC
            """;

        var scopes = new[] { SpanScope(startNano - _anchorLookbackNanos, endNano), IndexScope(startNano - NanosPerDay - _anchorLookbackNanos, endNano + NanosPerDay) };
        await using var readConn = await OpenAsync(cancellationToken, scopes);
        await using var lookupConn = await OpenAsync(cancellationToken, scopes);
        await using var reader = await readConn.ExecuteReaderAsync(new CommandDefinition(sql, f.Parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<Anchor>();

        var chunk = new List<Anchor>(ExportChunkSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            chunk.Add(parse(reader));
            if (chunk.Count < ExportChunkSize) continue;
            foreach (var info in await CompleteAsync(lookupConn, chunk, service, cancellationToken)) yield return info;
            chunk.Clear();
        }
        if (chunk.Count > 0)
            foreach (var info in await CompleteAsync(lookupConn, chunk, service, cancellationToken)) yield return info;
    }

    private async Task<List<TraceInfo>> CompleteAsync(NamedParameterConnection conn, List<Anchor> chunk, string? service, CancellationToken ct)
    {
        var index = await TraceIndexAsync(conn, chunk.Select(c => c.Key).ToArray(), service, ct);
        foreach (var a in chunk)
        {
            if (index.TryGetValue(a.Key, out var row)) { a.TraceMin = row.TraceMin; a.TraceMax = row.TraceMax; a.HasError = row.ScopeError != 0; }
            else { a.TraceMin = a.AnchorStart; a.TraceMax = a.AnchorStart + a.DurationNs; a.HasError = a.HasErrorInt != 0; }
        }
        await FillSpanCountsAsync(conn, chunk, service, ct);
        return chunk.Select(ToInfo).ToList();
    }

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class IndexRow
    {
        public ulong TraceHi { get; set; }
        public ulong TraceLo { get; set; }
        public long ScopeMin { get; set; }
        public byte ScopeError { get; set; }
        public long TraceMin { get; set; }
        public long TraceMax { get; set; }
    }

    private sealed class CountRow
    {
        public ulong TraceHi { get; set; }
        public ulong TraceLo { get; set; }
        public long SpanCount { get; set; }
    }

    private sealed class BoundsRow
    {
        public long Cnt { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
    }

    private sealed class DependencyRow
    {
        public string ParentService { get; set; } = "";
        public string ChildService { get; set; } = "";
        public string Kind { get; set; } = "";
        public long CallCount { get; set; }
        public double AvgNs { get; set; }
        public long MinNs { get; set; }
        public long MaxNs { get; set; }
        public long ErrorCount { get; set; }
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
        public ulong TraceHi { get; set; }
        public ulong TraceLo { get; set; }
        public ulong SpanId { get; set; }
        public ulong ParentSpanId { get; set; }
        public string SpanName { get; set; } = "";
        public string Kind { get; set; } = "UNSPECIFIED";
        public long StartNano { get; set; }
        public long EndNano { get; set; }
        public ulong DroppedAttributes { get; set; }
        public ulong DroppedEvents { get; set; }
        public ulong DroppedLinks { get; set; }
        public string? TraceState { get; set; }
        public ulong SpanFlags { get; set; }
        public string StatusCode { get; set; } = "UNSET";
        public string? StatusMessage { get; set; }
        public string? SpanAttributes { get; set; }
        public string? EventsJson { get; set; }
        public string? LinksJson { get; set; }
        public string? ResourceAttributes { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public string? ScopeAttributes { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public ulong ResourceKey { get; set; }
        public ulong ScopeKey { get; set; }
    }
}
