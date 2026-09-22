using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ITraceReadRepository"/>. Row-level predicates
/// (tenant, time range, status, parent/trace ids) are pushed into SQL via a join to
/// <c>resources</c>; the trace-level grouping/aggregation and service-name extraction are
/// performed in memory exactly as the former EF repository did, preserving output parity.
/// </summary>
public abstract class TraceReadRepositoryBase : DapperReadRepository, ITraceReadRepository
{
    private readonly TraceQueryCache _traceQueryCache;

    protected TraceReadRepositoryBase(ITenantContext tenantContext, TraceQueryCache traceQueryCache) : base(tenantContext)
    {
        _traceQueryCache = traceQueryCache;
    }

    // =========================================================================
    // FULL SPAN READS (span + resource + scope + events + links)
    // =========================================================================

    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var spans = await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId", "ORDER BY s.start_time_unix_nano",
            new { tenantId = TenantId, traceId = traceIdHex }, cancellationToken);
        return spans;
    }

    public async Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex))
            throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var spans = await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId AND s.span_id = @spanId", null,
            new { tenantId = TenantId, traceId = traceIdHex, spanId = spanIdHex }, cancellationToken);
        return spans.FirstOrDefault();
    }

    public async Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(parentSpanIdHex))
            throw new ArgumentException("Parent Span ID cannot be null or empty", nameof(parentSpanIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        return await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId AND s.parent_span_id = @parentSpanId", "ORDER BY s.start_time_unix_nano",
            new { tenantId = TenantId, traceId = traceIdHex, parentSpanId = parentSpanIdHex }, cancellationToken);
    }

    private async Task<List<SpanModel>> LoadFullSpansAsync(
        System.Data.Common.DbConnection conn, string whereClause, string? orderClause, object parameters, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                s.id                        AS Id,
                s.trace_id                  AS TraceId,
                s.span_id                   AS SpanId,
                s.parent_span_id            AS ParentSpanId,
                s.name                      AS Name,
                s.kind                      AS Kind,
                s.start_time_unix_nano      AS StartTimeUnixNano,
                s.end_time_unix_nano        AS EndTimeUnixNano,
                s.dropped_attributes_count  AS DroppedAttributesCount,
                s.dropped_events_count      AS DroppedEventsCount,
                s.dropped_links_count       AS DroppedLinksCount,
                s.trace_state               AS TraceState,
                s.flags                     AS Flags,
                s.status_code               AS StatusCode,
                s.status_message            AS StatusMessage,
                s.attributes_json           AS AttributesJson,
                s.events_json               AS EventsJson,
                s.links_json                AS LinksJson,
                r.schema_url                AS ResourceSchemaUrl,
                r.attributes_json           AS ResourceAttributesJson,
                sc.name                     AS ScopeName,
                sc.version                  AS ScopeVersion,
                sc.schema_url               AS ScopeSchemaUrl,
                sc.attributes_json          AS ScopeAttributesJson
            FROM spans s
            JOIN resources r               ON s.resource_id = r.id
            JOIN instrumentation_scopes sc ON s.scope_id = sc.id
            WHERE r.tenant_id = @tenantId AND {whereClause}
            {orderClause}
            """;

        // One query. Since schema 2.11.0 a span's events and links come back as JSON columns on
        // the span row itself, so the two extra batched SELECTs against span_events/span_links
        // this method used to issue are gone -- as are the tables.
        var rows = (await conn.QueryAsync<FullSpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return new List<SpanModel>();

        return rows.Select(MapSpan).ToList();
    }

    private static SpanModel MapSpan(FullSpanRow r) => new()
    {
        TraceIdHex = r.TraceId,
        SpanIdHex = r.SpanId,
        ParentSpanIdHex = r.ParentSpanId,
        Name = r.Name,
        Kind = Enum.Parse<SpanKind>(r.Kind),
        StartTimeUnixNano = r.StartTimeUnixNano,
        EndTimeUnixNano = r.EndTimeUnixNano,
        DroppedAttributesCount = r.DroppedAttributesCount,
        DroppedEventsCount = r.DroppedEventsCount,
        DroppedLinksCount = r.DroppedLinksCount,
        TraceState = r.TraceState,
        Flags = r.Flags,
        StatusCode = Enum.Parse<SpanStatusCode>(r.StatusCode),
        StatusMessage = r.StatusMessage,
        Attributes = DeserializeAttributes(r.AttributesJson),
        Events = DeserializeList<SpanEventModel>(r.EventsJson),
        Links = DeserializeList<SpanLinkModel>(r.LinksJson),
        Resource = new ResourceModel
        {
            SchemaUrl = r.ResourceSchemaUrl,
            Attributes = DeserializeAttributes(r.ResourceAttributesJson) ?? new Dictionary<string, object>()
        },
        InstrumentationScope = new InstrumentationScopeModel
        {
            Name = r.ScopeName,
            Version = r.ScopeVersion,
            SchemaUrl = r.ScopeSchemaUrl,
            Attributes = DeserializeAttributes(r.ScopeAttributesJson) ?? new Dictionary<string, object>()
        }
    };

    // =========================================================================
    // TRACE-INFO READS (raw span fetch + in-memory grouping)
    // =========================================================================

    public async Task<List<TraceInfo>> GetTracesByTimeRangeAsync(DateTime startTime, DateTime endTime, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano = TimeConversion.DateTimeToUnixNano(endTime);

        var raw = await FetchRawSpansAsync(
            "s.start_time_unix_nano >= @start AND s.start_time_unix_nano <= @end",
            new { tenantId = TenantId, start = startNano, end = endNano }, cancellationToken);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                HasErrors = g.Any(s => s.StatusCode == "ERROR"),
                RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano).FirstOrDefault()?.ResourceAttributes)
            })
            .OrderByDescending(t => t.MinStartTimeNano)
            .ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(),
            cancellationToken);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Take(limit)
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
            .ToList();
    }

    public async Task<List<TraceInfo>> GetTracesByServiceAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.start_time_unix_nano");
        var raw = await FetchRawSpansAsync(where, p, cancellationToken);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g =>
            {
                var spans = g.ToList();
                // Every span belonging to the filtered service — guaranteed non-empty by the
                // InvolvesService filter below. The row represents this service's own
                // involvement in the trace (aggregated over just these spans), anchored on the
                // earliest one for its name/id, not the trace's true root.
                var serviceSpans = spans.Where(s => MatchesService(s, serviceName)).ToList();
                return new
                {
                    TraceIdHex = g.Key,
                    SpanCount = spans.Count,
                    MinStartTimeNano = spans.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = spans.Max(s => s.EndTimeUnixNano),
                    RootSpan = spans.FirstOrDefault(s => s.ParentSpanId == null) ?? spans.OrderBy(s => s.StartTimeUnixNano).First(),
                    ServiceSpans = serviceSpans,
                };
            })
            // Matched at the trace level: filtering the spans first would strip the root span of
            // any trace whose entry point lives in another service, dropping the trace outright.
            .Where(t => t.ServiceSpans.Count > 0)
            .OrderByDescending(t => t.MinStartTimeNano)
            .ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(),
            cancellationToken);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Take(limit)
            .Select(t =>
            {
                var anchorSpan = t.ServiceSpans.OrderBy(s => s.StartTimeUnixNano).First();
                var hasErrors = t.ServiceSpans.Any(s => s.StatusCode == "ERROR");
                var displayStartNano = t.ServiceSpans.Min(s => s.StartTimeUnixNano);
                var displayEndNano = t.ServiceSpans.Max(s => s.EndTimeUnixNano);
                return ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano,
                    displayStartNano, displayEndNano, hasErrors, serviceName, anchorSpan);
            })
            .ToList();
    }

    public async Task<List<TraceInfo>> GetErrorTracesAsync(DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        // Same bounds the outer query uses, re-expressed against the subquery's own alias.
        var innerTime = new System.Text.StringBuilder();
        if (startTime.HasValue) innerTime.Append(" AND s2.start_time_unix_nano >= @start");
        if (endTime.HasValue) innerTime.Append(" AND s2.start_time_unix_nano <= @end");

        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.start_time_unix_nano",
            extra: ErrorTracePredicate(innerTime.ToString()));
        var raw = await FetchRawSpansAsync(where, p, cancellationToken);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                HasErrors = g.Any(s => s.StatusCode == "ERROR"),
                RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano).FirstOrDefault()?.ResourceAttributes)
            })
            .OrderByDescending(t => t.MinStartTimeNano)
            .ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(),
            cancellationToken);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Take(limit)
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
            .ToList();
    }

    public async Task<List<TraceInfo>> GetSlowTracesAsync(TimeSpan minDuration, DateTime? startTime = null, DateTime? endTime = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var minDurationNano = (long)(minDuration.TotalMilliseconds * 1_000_000);
        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.start_time_unix_nano");
        var raw = await FetchRawSpansAsync(where, p, cancellationToken);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                HasErrors = g.Any(s => s.StatusCode == "ERROR"),
                DurationNano = g.Max(s => s.EndTimeUnixNano) - g.Min(s => s.StartTimeUnixNano),
                RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                ServiceName = ExtractServiceName(g.OrderBy(s => s.StartTimeUnixNano).FirstOrDefault()?.ResourceAttributes)
            })
            .Where(t => t.DurationNano >= minDurationNano)
            .OrderByDescending(t => t.DurationNano)
            .ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(),
            cancellationToken);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Take(limit)
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
            .ToList();
    }

    // =========================================================================
    // PAGED TRACE QUERY (server-side filter + offset/limit + total)
    // =========================================================================

    public async Task<PagedResult<TraceInfo>> QueryTracesAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await QueryTracePageAsync(query, cancellationToken);
    }

    /// <summary>
    /// True when <see cref="QueryTracePageFastAsync"/> can serve this query — the structural fix
    /// (list-page-scale plan, Phase 5): SQL does the trace-level aggregation, ordering and paging
    /// directly, so cost tracks the page and the window's distinct traces, not every span in the
    /// window. Deliberately narrow, not "every query": three complications are carved out to the
    /// existing <see cref="ComputeTraceInfosAsync"/> path instead of being reproduced in SQL,
    /// because each would need machinery this phase doesn't build —
    /// <list type="bullet">
    /// <item><description><b>A service filter</b> changes several output fields (ServiceName,
    /// HasErrors, duration, the anchor span) to that service's own involvement in the trace rather
    /// than the trace's true root — see <c>ComputeTraceInfoRowsUncachedAsync</c>'s "display"
    /// projection. Reproducing that in SQL needs a per-trace, per-service anchor-span lookup
    /// (window functions), which is exactly the "operation"/"service" sort problem below, just
    /// unconditional.</description></item>
    /// <item><description><b>Tags</b> are only safely narrowed to a coarse SQL
    /// key-existence pre-filter (<see cref="TagKeyExistsPredicate"/>); the authoritative value
    /// match runs in C# over full span attributes. Applying that re-check *after* SQL has already
    /// committed to a page would make the page come back short (candidates the coarse filter
    /// admitted but the value check rejects), which breaks Total/OFFSET consistency.</description></item>
    /// <item><description><b>Sorting by "operation" or "service"</b> needs a resolved root/anchor
    /// span's name *before* paging can happen (it's the ORDER BY key), which — see the service
    /// bullet — this phase's SQL doesn't compute.</description></item>
    /// </list>
    /// Every one of these is exactly the scenario <see cref="TraceQueryCache"/> (Phase 3) already
    /// makes cheap on a second request, so falling back to it here is a real, not just safe,
    /// answer — not merely "unoptimized".
    /// </summary>
    private static bool IsFastPagingEligible(TraceQuery query) =>
        string.IsNullOrEmpty(query.Service)
        && query.Tags.Count == 0
        && query.Sort?.ToLowerInvariant() is null or "" or "duration" or "spans" or "time";

    /// <summary>
    /// Items/Total for both <see cref="QueryTracesAsync"/> and <see cref="GetTraceOverviewAsync"/>:
    /// the fast, SQL-paged path when <see cref="IsFastPagingEligible"/>, else the existing
    /// <see cref="TraceQueryCache"/>-backed full scan, sliced in memory exactly as before Phase 5.
    /// </summary>
    private async Task<PagedResult<TraceInfo>> QueryTracePageAsync(TraceQuery query, CancellationToken ct)
    {
        if (IsFastPagingEligible(query)) return await QueryTracePageFastAsync(query, ct);

        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);

        // slim: neither this fallback nor the fast path ever needs RootSpanAttributes on Items —
        // see the doc comment this replaced on the old QueryTracesAsync body. Matching
        // GetTraceOverviewAsync's slim:true is also what lets this land on the *same*
        // TraceQueryCache entry as the overview load that preceded it (Phase 3).
        var ordered = await ComputeTraceInfosAsync(query, ct, slim: true);
        return new PagedResult<TraceInfo>
        {
            Items = ordered.Skip(offset).Take(limit).Select(t => t.Info).ToList(),
            Total = ordered.Count
        };
    }

    /// <summary>
    /// The WHERE/HAVING clauses and bind parameters shared by every fast-path query (Query 1 of
    /// both <see cref="QueryTracePageFastAsync"/> and <see cref="ComputeTraceSummaryRowsAsync"/>) —
    /// mode/operation/error/duration narrowing, built once so the two callers can't drift.
    /// </summary>
    private (string whereClause, string havingClause, DynamicParameters parameters) BuildFastPathPredicates(TraceQuery query)
    {
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var isSlow = query.Mode == "slow";
        var isErrors = query.Mode == "errors";
        var minDurationNano = isSlow ? (long)((query.MinDurationMs ?? 500) * 1_000_000) : 0;
        var maxDurationNano = isSlow && query.MaxDurationMs.HasValue
            ? (long)(query.MaxDurationMs.Value * 1_000_000)
            : long.MaxValue;
        var operation = string.IsNullOrWhiteSpace(query.Operation) ? null : query.Operation;

        const string innerTime = " AND s2.start_time_unix_nano >= @start AND s2.start_time_unix_nano <= @end";
        var clauses = new List<string> { "s.start_time_unix_nano >= @start", "s.start_time_unix_nano <= @end" };
        if (isErrors) clauses.Add(ErrorTracePredicate(innerTime));

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);
        if (operation != null)
        {
            clauses.Add(OperationTracePredicate(innerTime));
            parameters.Add("operation", operation);
        }
        var havingClause = "";
        if (isSlow)
        {
            havingClause = "HAVING (MAX(s.end_time_unix_nano) - MIN(s.start_time_unix_nano)) BETWEEN @minDurationNano AND @maxDurationNano";
            parameters.Add("minDurationNano", minDurationNano);
            parameters.Add("maxDurationNano", maxDurationNano);
        }
        return (string.Join(" AND ", clauses), havingClause, parameters);
    }

    /// <summary>
    /// Root detection shared by every fast-path query: a trace whose earliest in-window span has
    /// a parent that DOES exist (just outside the window) is a fragment of a larger trace whose
    /// true root isn't in view, and is excluded — same rule
    /// <see cref="ComputeTraceInfoRowsUncachedAsync"/> applies, reusing §7.3's chunked existence
    /// check over one candidate id per distinct trace instead of one per span.
    /// </summary>
    private async Task<List<TraceGroupSummaryRow>> FilterOrphanRootsAsync(List<TraceGroupSummaryRow> summaries, CancellationToken ct)
    {
        var candidateParentIds = summaries.Where(s => s.EarliestParentSpanId != null).Select(s => s.EarliestParentSpanId!).Distinct();
        var existingParentIds = await CheckSpanIdsExistAsync(candidateParentIds, ct);
        return summaries.Where(s => s.EarliestParentSpanId == null || !existingParentIds.Contains(s.EarliestParentSpanId)).ToList();
    }

    /// <summary>
    /// True when <see cref="ComputeTraceSummaryRowsAsync"/> can serve
    /// <see cref="GetTraceHistogramAsync"/>/<see cref="GetTraceOverviewAsync"/> from
    /// <see cref="FetchTraceGroupSummariesAsync"/> instead of the full
    /// <see cref="ComputeTraceInfoRowsUncachedAsync"/> scan (list-page-scale plan, Phase 5, Part
    /// 2). Same two carve-outs as <see cref="IsFastPagingEligible"/> and for the same reasons — a
    /// service filter needs the per-service "display" anchor-span projection, and tags need a C#
    /// value re-check this fast path has no way to run — except sort/page never apply to an
    /// aggregation, so there's no third carve-out here.
    /// </summary>
    private static bool IsFastAggregationEligible(TraceQuery query) =>
        string.IsNullOrEmpty(query.Service) && query.Tags.Count == 0;

    /// <summary>
    /// <see cref="GetTraceHistogramAsync"/>/<see cref="GetTraceOverviewAsync"/>'s trace population
    /// (list-page-scale plan, Phase 5, Part 2): the same <see cref="TraceInfoRow"/> shape
    /// <see cref="ComputeTraceInfosAsync"/> returns, so callers need no changes beyond calling this
    /// instead — via <see cref="FetchTraceGroupSummariesAsync"/> when
    /// <see cref="IsFastAggregationEligible"/> (one row per distinct trace, no span JSON fetched),
    /// else the existing <see cref="TraceQueryCache"/>-backed full scan. Percentile/bucket/grid
    /// computation (<see cref="BuildVolumeBuckets"/>, <see cref="BuildServiceStats"/>,
    /// <see cref="BuildWindowSummary"/>, <see cref="BuildLatencyBuckets"/>) stays in C# either way
    /// — deliberately not ported to SQL, since the five providers disagree on percentile SQL
    /// (`percentile_cont` vs `quantileExact` vs none on MySQL) and this phase's other three fast
    /// paths already avoid every dialect-risky construct (window functions, correlated
    /// subqueries); the win here is shrinking the *input* to those C# functions from O(spans in
    /// window) to O(distinct traces in window), not eliminating the C# step.
    /// </summary>
    private async Task<List<TraceInfoRow>> ComputeTraceSummaryRowsAsync(TraceQuery query, CancellationToken ct)
    {
        if (!IsFastAggregationEligible(query)) return await ComputeTraceInfosAsync(query, ct, slim: true);

        var (whereClause, havingClause, parameters) = BuildFastPathPredicates(query);
        var summaries = await FetchTraceGroupSummariesAsync(whereClause, havingClause, parameters, includeRootDetails: true, ct: ct);
        var eligible = await FilterOrphanRootsAsync(summaries, ct);
        return ToTraceInfoRows(eligible);
    }

    /// <summary>Maps <see cref="FetchTraceGroupSummariesAsync"/>'s (<c>includeRootDetails: true</c>) rows to the wire shape — split out of <see cref="ComputeTraceSummaryRowsAsync"/> so <see cref="GetTraceOverviewAsync"/> can reuse one already-fetched summary list for both the chart aggregates and Items/Total.</summary>
    private static List<TraceInfoRow> ToTraceInfoRows(List<TraceGroupSummaryRow> summaries) =>
        summaries.Select(s =>
        {
            var info = new TraceInfo
            {
                TraceIdHex = s.TraceId,
                SpanCount = s.SpanCount,
                TraceStartTime = TimeConversion.UnixNanoToDateTime(s.MinStart),
                TraceEndTime = TimeConversion.UnixNanoToDateTime(s.MaxEnd),
                HasErrors = s.HasErrorsInt != 0,
                ServiceName = s.ServiceName,
                RootOperationName = s.RootName,
                // Same nanosecond-to-ticks computation ToTraceInfo uses, not two DateTime
                // subtractions — see that method's own doc comment for why.
                TraceDuration = TimeSpan.FromTicks((s.MaxEnd - s.MinStart) / 100),
                DisplaySpanIdHex = s.RootSpanId,
            };
            return new TraceInfoRow(info, s.RootKind ?? "UNSPECIFIED");
        }).ToList();

    /// <summary>
    /// The structural fix itself (list-page-scale plan, Phase 5, Part 1): two queries instead of
    /// an unbounded span scan.
    ///
    /// <b>Query 1</b> (<see cref="FetchTraceGroupSummariesAsync"/>) aggregates in SQL — one row
    /// per <em>distinct trace</em> in the window (not one row per span), with the four fields
    /// needed to sort/page by "time"/"duration"/"spans"/the mode default, plus enough to run the
    /// existing root-detection rule (see below). This is already the complexity win: O(distinct
    /// traces in the window), not O(spans in the window) — no span JSON is fetched or
    /// deserialized at this step at all.
    ///
    /// Root detection (was <c>CheckSpanIdsExistAsync</c>'s job in
    /// <see cref="ComputeTraceInfoRowsUncachedAsync"/>) has to happen *before* paging can be
    /// decided — a trace whose earliest in-window span turns out not to be a true root must be
    /// excluded, or Total and OFFSET both drift. It reuses the same chunked existence check
    /// (§7.3), now over one candidate id per distinct trace instead of one per span — still
    /// bounded well below the old per-span cost.
    ///
    /// Sorting and paging then run in memory, but over that same trace-count-bounded list, not the
    /// window's spans — cheap regardless of window width.
    ///
    /// <b>Query 2</b> (<see cref="FetchRawSpansSlimAsync"/>) fetches only the resulting page's
    /// spans, keyed on its ~<c>limit</c> trace ids via <see cref="TraceIdInPredicate"/> — this is
    /// where the existing per-trace grouping/root-span-resolution logic still runs, just over a
    /// page's spans instead of the window's.
    /// </summary>
    private async Task<PagedResult<TraceInfo>> QueryTracePageFastAsync(TraceQuery query, CancellationToken ct)
    {
        var (whereClause, havingClause, parameters) = BuildFastPathPredicates(query);
        var summaries = await FetchTraceGroupSummariesAsync(whereClause, havingClause, parameters, includeRootDetails: false, ct: ct);
        var eligible = await FilterOrphanRootsAsync(summaries, ct);
        return await BuildTracePageFromSummariesAsync(query, eligible, ct);
    }

    /// <summary>
    /// Sort/page/Query 2 half of <see cref="QueryTracePageFastAsync"/>, split out so
    /// <see cref="GetTraceOverviewAsync"/> can reuse an already-fetched, already-orphan-filtered
    /// summary list for Items/Total instead of running Query 1 a second time (list-page-scale
    /// plan, Phase 5, Part 2) — the two query shapes turned out to duplicate the exact same GROUP
    /// BY when both were eligible on the same request, which cost more than Phase 3's single old
    /// scan did. <paramref name="eligibleSummaries"/> must already be orphan-filtered.
    /// </summary>
    private async Task<PagedResult<TraceInfo>> BuildTracePageFromSummariesAsync(
        TraceQuery query, List<TraceGroupSummaryRow> eligible, CancellationToken ct)
    {
        var isSlow = query.Mode == "slow";
        var asc = string.Equals(query.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        IOrderedEnumerable<TraceGroupSummaryRow> ordered = (query.Sort?.ToLowerInvariant()) switch
        {
            "duration" => asc ? eligible.OrderBy(s => s.MaxEnd - s.MinStart)   : eligible.OrderByDescending(s => s.MaxEnd - s.MinStart),
            "spans"    => asc ? eligible.OrderBy(s => s.SpanCount)            : eligible.OrderByDescending(s => s.SpanCount),
            "time"     => asc ? eligible.OrderBy(s => s.MinStart)             : eligible.OrderByDescending(s => s.MinStart),
            _          => isSlow ? eligible.OrderByDescending(s => s.MaxEnd - s.MinStart) : eligible.OrderByDescending(s => s.MinStart),
        };
        // Deterministic tiebreaker so OFFSET paging is stable across requests (list-page-scale
        // plan §8, point 2) — the sort keys above can tie exactly (e.g. two traces starting the
        // same nanosecond), and LINQ's OrderBy is stable but only over the DB's own (unspecified)
        // row order, which isn't guaranteed stable across two separate queries.
        var sortedIds = ordered.ThenBy(s => s.TraceId).Select(s => s.TraceId).ToList();

        var total = sortedIds.Count;
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);
        var pageIds = sortedIds.Skip(offset).Take(limit).ToList();
        if (pageIds.Count == 0) return new PagedResult<TraceInfo> { Items = [], Total = total };

        var spanParams = new DynamicParameters();
        spanParams.Add("tenantId", TenantId);
        spanParams.Add("start", TimeConversion.DateTimeToUnixNano(query.Start));
        spanParams.Add("end", TimeConversion.DateTimeToUnixNano(query.End));
        spanParams.Add("traceIds", pageIds);
        var spanWhere = $"s.start_time_unix_nano >= @start AND s.start_time_unix_nano <= @end AND {TraceIdInPredicate}";
        var raw = await FetchRawSpansSlimAsync(spanWhere, spanParams, ct);

        var byTraceId = raw.GroupBy(s => s.TraceId).ToDictionary(g => g.Key, g =>
        {
            var spans = g.ToList();
            var first = spans.OrderBy(s => s.StartTimeUnixNano).First();
            var rootSpan = spans.FirstOrDefault(s => s.ParentSpanId == null) ?? first;
            var minStart = spans.Min(s => s.StartTimeUnixNano);
            var maxEnd = spans.Max(s => s.EndTimeUnixNano);
            return ToTraceInfo(g.Key, spans.Count, minStart, maxEnd, minStart, maxEnd,
                spans.Any(s => s.StatusCode == "ERROR"), first.ServiceName, rootSpan);
        });

        // Preserve Query 1's sort/page order — Query 2's own row order (an IN-clause fetch) isn't
        // guaranteed to match it. A missing id (row deleted between the two queries) is silently
        // dropped rather than surfaced as an error.
        var items = pageIds.Where(byTraceId.ContainsKey).Select(id => byTraceId[id]).ToList();
        return new PagedResult<TraceInfo> { Items = items, Total = total };
    }

    /// <summary>
    /// True volume histogram over the same filter set as <see cref="QueryTracesAsync"/>, bucketed
    /// in memory over the already-computed full trace list — no new SQL, since the underlying
    /// span fetch is already unbounded. Buckets are evenly spaced across <c>[Start,End)</c>,
    /// matching the bucket math the Angular client used to compute client-side.
    /// </summary>
    public async Task<List<TraceVolumeBucket>> GetTraceHistogramAsync(HistogramQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        // The fast summary path when eligible, else the full scan (Phase 5, Part 2) — see
        // ComputeTraceSummaryRowsAsync. Restricted to inbound-request roots — this is a
        // latency/RED aggregation, see the doc comment on ComputeTraceInfosAsync's return type.
        var rows = await ComputeTraceSummaryRowsAsync(ToTraceQuery(query), cancellationToken);
        var traces = rows.Where(r => IsInboundRoot(r.RootSpanKind)).Select(r => r.Info).ToList();
        return BuildVolumeBuckets(traces, query);
    }

    public async Task<TraceOverview> GetTraceOverviewAsync(HistogramQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var traceQuery = ToTraceQuery(query);

        // Buckets/Services/Summary/LatencyBuckets/RecentErrors/SlowestTraces are window-wide — they
        // cannot be derived from a page — while Items/Total is a page. When both this request's
        // aggregation fast path (Phase 5 Part 2) *and* its paging fast path (Part 1) apply — the
        // common case: no service filter, no tags, and a page-compatible sort — they'd otherwise
        // run the exact same GROUP BY (Query 1) twice, which cost more than Phase 3's one old
        // scan did. So: fetch the summary list once, share it for both when both apply.
        List<TraceInfoRow> rows;
        PagedResult<TraceInfo> page;
        if (IsFastAggregationEligible(traceQuery))
        {
            var (whereClause, havingClause, parameters) = BuildFastPathPredicates(traceQuery);
            var summaries = await FetchTraceGroupSummariesAsync(whereClause, havingClause, parameters, includeRootDetails: true, ct: cancellationToken);
            var eligibleSummaries = await FilterOrphanRootsAsync(summaries, cancellationToken);
            rows = ToTraceInfoRows(eligibleSummaries);
            page = IsFastPagingEligible(traceQuery)
                ? await BuildTracePageFromSummariesAsync(traceQuery, eligibleSummaries, cancellationToken)
                : await QueryTracePageAsync(traceQuery, cancellationToken);
        }
        else
        {
            // Neither fast path applies (service filter or tags active) — one full scan serves both,
            // exactly as before Phase 5 (Phase 3's TraceQueryCache still applies here).
            rows = await ComputeTraceInfosAsync(traceQuery, cancellationToken, slim: true);
            page = await QueryTracePageAsync(traceQuery, cancellationToken);
        }

        // Buckets/Services/Summary/LatencyBuckets are latency/RED aggregations — restricted to
        // inbound-request roots, see the doc comment on ComputeTraceInfosAsync's return type.
        var inbound = rows.Where(r => IsInboundRoot(r.RootSpanKind)).Select(r => r.Info).ToList();
        var services = BuildServiceStats(inbound, query.Start, query.End);

        // RecentErrors/SlowestTraces want the *unfiltered* population instead (list-page-scale
        // plan, Phase 2): a slow or erroring internal-rooted trace is still worth surfacing.
        var all = rows.Select(r => r.Info).ToList();
        var sampleSize = Math.Clamp(query.SampleSize, 1, 50);

        return new TraceOverview
        {
            Buckets = BuildVolumeBuckets(inbound, query),
            Services = services,
            Summary = BuildWindowSummary(inbound, services.Count),
            LatencyBuckets = BuildLatencyBuckets(inbound, query),
            Items = page.Items.ToList(),
            Total = page.Total,
            // Own sort order (not Items' — a service filter or sort column shouldn't change which
            // rows these consider), off the same already-materialized list, so no extra query.
            // The >500ms floor matches the dashboard widget's own title ("Slowest Traces
            // (>500ms)") — this used to be a client-side filter over the old limit:500 fetch.
            RecentErrors = all.Where(t => t.HasErrors).OrderByDescending(t => t.TraceStartTime).Take(sampleSize).ToList(),
            SlowestTraces = all.Where(t => t.TraceDuration.TotalMilliseconds > 500)
                .OrderByDescending(t => t.TraceDuration).Take(sampleSize).ToList(),
        };
    }

    /// <summary>
    /// Window-wide aggregates over the same trace list the other two groupings use — a third
    /// view of one scan, not a third query. The percentiles here are computed over every trace
    /// in the range because a window percentile cannot be derived from the per-bucket ones.
    /// </summary>
    private static TraceWindowSummary BuildWindowSummary(List<TraceInfo> traces, int serviceCount)
    {
        if (traces.Count == 0) return new TraceWindowSummary();

        var durations = traces.Select(t => t.TraceDuration.TotalMilliseconds).ToList();
        durations.Sort();

        return new TraceWindowSummary
        {
            Count = traces.Count,
            ErrorCount = traces.Count(t => t.HasErrors),
            P50Ms = Percentile(durations, 50),
            P95Ms = Percentile(durations, 95),
            P99Ms = Percentile(durations, 99),
            ServiceCount = serviceCount,
            LastTraceStartTime = traces.Max(t => t.TraceStartTime),
        };
    }

    private static TraceQuery ToTraceQuery(HistogramQuery query) => new()
    {
        Start = query.Start,
        End = query.End,
        Mode = query.Mode,
        Service = query.Service,
        Operation = query.Operation,
        MinDurationMs = query.MinDurationMs,
        MaxDurationMs = query.MaxDurationMs,
        Tags = query.Tags,
        // Sort/Limit/Offset are only meaningful to GetTraceOverviewAsync's Items (via
        // QueryTracePageAsync) — GetTraceHistogramAsync ignores all three (its buckets are
        // grouped by time, not by row order or page).
        Sort = query.Sort,
        Dir = query.Dir,
        Limit = query.Limit,
        Offset = query.Offset,
    };

    private static List<TraceVolumeBucket> BuildVolumeBuckets(List<TraceInfo> traces, HistogramQuery query)
    {
        var bucketCount = Math.Clamp(query.BucketCount, 1, 500);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var counts = new int[bucketCount];
        var errorCounts = new int[bucketCount];
        var sumDurationMs = new double[bucketCount];
        // Per-bucket durations, kept alongside the running sum, so percentiles cost only a sort
        // per bucket at the end — no second pass over `traces` and no extra query.
        var durationsByBucket = new List<double>[bucketCount];
        for (var i = 0; i < bucketCount; i++) durationsByBucket[i] = [];
        foreach (var t in traces)
        {
            var idx = (int)((t.TraceStartTime.Ticks - startTicks) * bucketCount / rangeTicks);
            idx = Math.Clamp(idx, 0, bucketCount - 1);
            var durationMs = t.TraceDuration.TotalMilliseconds;
            counts[idx]++;
            if (t.HasErrors) errorCounts[idx]++;
            sumDurationMs[idx] += durationMs;
            durationsByBucket[idx].Add(durationMs);
        }

        var result = new List<TraceVolumeBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var durations = durationsByBucket[i];
            durations.Sort();
            result.Add(new TraceVolumeBucket
            {
                Timestamp = new DateTime(startTicks + i * rangeTicks / bucketCount, query.Start.Kind),
                Count = counts[i],
                ErrorCount = errorCounts[i],
                SumDurationMs = sumDurationMs[i],
                P50Ms = Percentile(durations, 50),
                P95Ms = Percentile(durations, 95),
                P99Ms = Percentile(durations, 99),
            });
        }
        return result;
    }

    /// <summary>
    /// Bins the same trace list onto a time × log-duration grid for the traces list page's latency
    /// bubble chart (trace-latency-p50 plan, Phase 3) — ports the client-side grid math that used
    /// to run in <c>binLatencyPoints</c> (chart.utils.ts) over a 1000-row capped page, which meant
    /// the chart only ever covered the most recent few minutes of any wide time range. Time
    /// columns are evenly spaced across <c>[Start, End)</c>; duration rows are log-spaced across
    /// the observed [min, max] duration, since durations are right-skewed. Empty cells are
    /// omitted.
    /// </summary>
    private static List<TraceLatencyBucket> BuildLatencyBuckets(List<TraceInfo> traces, HistogramQuery query)
    {
        if (traces.Count == 0) return new List<TraceLatencyBucket>();

        var timeCols = Math.Clamp(query.LatencyTimeCols, 1, 200);
        var durationRows = Math.Clamp(query.LatencyDurationRows, 1, 100);

        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var durationsMs = traces.Select(t => t.TraceDuration.TotalMilliseconds).ToList();
        var yMin = Math.Max(1.0, durationsMs.Min());
        var yMax = Math.Max(yMin * 10, durationsMs.Max());
        var logMin = Math.Log(yMin);
        var logMax = Math.Log(yMax);
        var logStep = (logMax - logMin) / durationRows;

        int ColIndexFor(long startTicksOfTrace)
        {
            var idx = (int)((startTicksOfTrace - startTicks) * timeCols / rangeTicks);
            return Math.Clamp(idx, 0, timeCols - 1);
        }

        int RowIndexFor(double durationMs)
        {
            if (durationMs <= yMin) return 0;
            var idx = (int)((Math.Log(durationMs) - logMin) / logStep);
            return Math.Clamp(idx, 0, durationRows - 1);
        }

        var cells = new Dictionary<(int Col, int Row), (int Count, int ErrorCount, string FirstTraceIdHex)>();
        foreach (var t in traces)
        {
            var key = (ColIndexFor(t.TraceStartTime.Ticks), RowIndexFor(t.TraceDuration.TotalMilliseconds));
            cells[key] = cells.TryGetValue(key, out var existing)
                ? (existing.Count + 1, existing.ErrorCount + (t.HasErrors ? 1 : 0), existing.FirstTraceIdHex)
                : (1, t.HasErrors ? 1 : 0, t.TraceIdHex);
        }

        var result = new List<TraceLatencyBucket>(cells.Count);
        foreach (var ((col, row), (count, errorCount, firstTraceIdHex)) in cells)
        {
            result.Add(new TraceLatencyBucket
            {
                XStart = new DateTime(startTicks + col * rangeTicks / timeCols, query.Start.Kind),
                XEnd = new DateTime(startTicks + (col + 1) * rangeTicks / timeCols, query.Start.Kind),
                YStartMs = Math.Exp(logMin + row * logStep),
                YEndMs = Math.Exp(logMin + (row + 1) * logStep),
                Count = count,
                ErrorCount = errorCount,
                SampleTraceIdHex = count == 1 ? firstTraceIdHex : null,
            });
        }
        return result;
    }

    /// <summary>
    /// Groups the same trace list by root-span service (falling back to "(unknown)" for a trace
    /// with none, so every trace is counted exactly once — Σ per-service counts must equal the
    /// bucketed total). <see cref="TraceInfo.Services"/> (the full participant list) is
    /// deliberately not used here — it would double-count a trace across every service it
    /// touches, which is the wrong RED semantic ("traces originating in service X").
    /// </summary>
    private static List<ServiceStats> BuildServiceStats(List<TraceInfo> traces, DateTime start, DateTime end)
    {
        var windowSeconds = Math.Max((end - start).TotalSeconds, 1);
        return traces
            .GroupBy(t => t.ServiceName ?? "(unknown)")
            .Select(g =>
            {
                var durations = g.Select(t => t.TraceDuration.TotalMilliseconds).OrderBy(x => x).ToList();
                var count = durations.Count;
                var errorCount = g.Count(t => t.HasErrors);
                return new ServiceStats
                {
                    Service = g.Key,
                    Count = count,
                    ErrorCount = errorCount,
                    ErrorRate = count > 0 ? errorCount / (double)count * 100 : 0,
                    RatePerSecond = count / windowSeconds,
                    AvgMs = count > 0 ? durations.Average() : 0,
                    P95Ms = Percentile(durations, 95),
                };
            })
            .OrderByDescending(s => s.Count)
            .ToList();
    }

    /// <summary>
    /// A computed trace paired with its true root span's <c>SpanKind</c> (<c>"SERVER"</c>,
    /// <c>"INTERNAL"</c>, …) — <see cref="ComputeTraceInfosAsync"/>'s return type. Kept internal
    /// to the repository rather than added to the wire <see cref="TraceInfo"/> model: it exists
    /// only so callers can apply the inbound-root restriction (see
    /// <see cref="IsInboundRoot(string)"/>) themselves, each over their own view of the same
    /// already-materialized list, instead of the list being computed once per required filtering
    /// (list-page-scale plan, Phase 2 — this is what lets <see cref="GetTraceOverviewAsync"/> get
    /// its RED aggregations *and* the traces list page's unfiltered table rows from one scan).
    /// </summary>
    protected readonly record struct TraceInfoRow(TraceInfo Info, string RootSpanKind);

    /// <summary>
    /// True for a trace's conventional inbound-request entry points (trace-latency-p50 plan,
    /// Phase 2). Most root spans in a real system are not requests (internal work, client calls,
    /// or — in this codebase's own test generator — parentless activities opened only to carry a
    /// trace id for a log line or metric exemplar), so an unfiltered population makes "trace
    /// latency" arithmetically correct but semantically meaningless: it reads close to zero
    /// because non-request roots dominate and are typically instantaneous. Applied by the
    /// latency/RED aggregations (<see cref="GetTraceHistogramAsync"/>,
    /// <see cref="GetTraceOverviewAsync"/>'s Buckets/Services/Summary/LatencyBuckets);
    /// deliberately not applied to <see cref="QueryTracesAsync"/> or <see cref="GetTraceOverviewAsync"/>'s
    /// own Items/RecentErrors/SlowestTraces — those are exploration surfaces where a user must
    /// still be able to find a specific internal- or client-rooted trace.
    /// </summary>
    private static bool IsInboundRoot(string rootSpanKind) => rootSpanKind is "SERVER" or "CONSUMER";

    /// <summary>
    /// <see cref="ComputeTraceInfosAsync"/>'s actual scan — fetches the raw spans matching the
    /// query's mode/service/time predicates, groups them into the same trace-level shape the
    /// legacy <c>Get*TracesAsync</c> methods produce, filters out non-root traces, and returns
    /// the full <em>unordered</em> list (no sort, no offset/limit — <see cref="ComputeTraceInfosAsync"/>
    /// applies sort after the cache lookup, and every caller pages it). Every caller gets the
    /// same unfiltered-by-root population; apply <see cref="IsInboundRoot(string)"/> over the
    /// result's <see cref="TraceInfoRow.RootSpanKind"/> when a caller needs it restricted.
    /// </summary>
    /// <param name="slim">
    /// Opt out of fetching either attributes column — see <see cref="FetchRawSpansSlimAsync"/>.
    /// Only safe for callers that read nothing but the trace-level aggregates (counts, timings,
    /// error flag, service name); the returned <see cref="TraceInfo.RootSpanAttributes"/> will be
    /// null. Silently ignored when the query carries tag predicates, which need span attributes.
    /// </param>
    private async Task<List<TraceInfoRow>> ComputeTraceInfoRowsUncachedAsync(
        TraceQuery query, CancellationToken ct, bool slim)
    {
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var isSlow = query.Mode == "slow";
        var minDurationNano = isSlow ? (long)((query.MinDurationMs ?? 500) * 1_000_000) : 0;
        var maxDurationNano = isSlow && query.MaxDurationMs.HasValue
            ? (long)(query.MaxDurationMs.Value * 1_000_000)
            : long.MaxValue;
        var operation = string.IsNullOrWhiteSpace(query.Operation) ? null : query.Operation;
        var service = string.IsNullOrEmpty(query.Service) ? null : query.Service;
        var isErrors = query.Mode == "errors";
        var tags = query.Tags;

        const string innerTime = " AND s2.start_time_unix_nano >= @start AND s2.start_time_unix_nano <= @end";
        var clauses = new List<string> { "s.start_time_unix_nano >= @start", "s.start_time_unix_nano <= @end" };
        // Narrow by *trace*, not by span: selecting only ERROR spans would leave each group
        // without its (non-erroring) root span, breaking root detection and the trace-level
        // aggregates below. The subquery keeps the DB doing the narrowing while the outer
        // query still returns every span of each matching trace. Same discipline for the
        // service/operation/tag predicates added below (list-page-scale plan, Phase 4).
        if (isErrors) clauses.Add(ErrorTracePredicate(innerTime));

        var spanParams = new DynamicParameters();
        spanParams.Add("tenantId", TenantId);
        spanParams.Add("start", startNano);
        spanParams.Add("end", endNano);
        if (service != null)
        {
            clauses.Add(ServiceTracePredicate(innerTime));
            spanParams.Add("service", service);
        }
        if (operation != null)
        {
            clauses.Add(OperationTracePredicate(innerTime));
            spanParams.Add("operation", operation);
        }
        // Only non-negated tags narrow the SQL fetch — see TagKeyExistsPredicate's doc comment
        // for why a negated tag can't be soundly reduced to a key-existence check.
        var positiveTags = tags.Where(t => !t.Negate).ToList();
        for (var i = 0; i < positiveTags.Count; i++)
        {
            var paramName = $"tagKey{i}";
            clauses.Add(TagKeyExistsPredicate($"@{paramName}", innerTime));
            spanParams.Add(paramName, positiveTags[i].Key);
        }
        var where = string.Join(" AND ", clauses);

        var raw = slim && tags.Count == 0
            ? await FetchRawSpansSlimAsync(where, spanParams, ct)
            : await FetchRawSpansAsync(where, spanParams, ct);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g =>
            {
                var spans = g.ToList();
                var first = spans.OrderBy(s => s.StartTimeUnixNano).First();
                return new
                {
                    TraceIdHex = g.Key,
                    SpanCount = spans.Count,
                    MinStartTimeNano = spans.Min(s => s.StartTimeUnixNano),
                    MaxEndTimeNano = spans.Max(s => s.EndTimeUnixNano),
                    HasErrors = spans.Any(s => s.StatusCode == "ERROR"),
                    RootSpan = spans.FirstOrDefault(s => s.ParentSpanId == null) ?? first,
                    ServiceName = first.ServiceName,
                    Spans = spans,
                };
            })
            .Select(t => new { t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.RootSpan, t.ServiceName, t.Spans, DurationNano = t.MaxEndTimeNano - t.MinStartTimeNano })
            .Where(t => !isSlow || (t.DurationNano >= minDurationNano && t.DurationNano <= maxDurationNano))
            // Errors filter: the trace contains an error span (HasErrors is computed over the
            // trace's full span set, so this holds even when the root span itself is OK).
            .Where(t => !isErrors || t.HasErrors)
            // Service/operation/positive-tag-key predicates above already narrowed which traces
            // got fetched at all (list-page-scale plan, Phase 4); these re-checks are now a
            // redundant-but-free correctness safety net over that already-small set, and they're
            // the only thing enforcing tag *value* matching and negated-tag exclusion at all — see
            // TagKeyExistsPredicate for why negated tags aren't narrowed in SQL.
            .Where(t => service == null || t.Spans.Any(s => MatchesService(s, service)))
            // Operation filter: the trace contains a span with this name.
            .Where(t => operation == null || t.Spans.Any(s => s.Name == operation))
            // All-span tag search: every predicate must be satisfied by some span in the trace.
            // A negated predicate instead requires that *no* span satisfies it.
            .Where(t => tags.Count == 0 || tags.All(tag =>
                tag.Negate
                    ? !t.Spans.Any(s => MatchesTag(s, tag))
                    : t.Spans.Any(s => MatchesTag(s, tag))))
            .ToList();

        // Per-service display values: when a service filter is active, the row represents that
        // service's own involvement in the trace — aggregated over just its own spans (error/
        // duration) and anchored on its earliest-started span (name/id) — rather than the
        // trace's true root. The preceding service `.Where` guarantees at least one match exists
        // whenever `service != null`, so `ServiceSpans` is never empty here. `RootSpan` (the true
        // root, or the trace's earliest span as fallback) is carried through unchanged for the
        // root-detection filter below, which must keep working off the real root.
        var display = groups.Select(t =>
        {
            var serviceSpans = service == null ? null : t.Spans.Where(s => MatchesService(s, service)).ToList();
            var anchorSpan = serviceSpans?.OrderBy(s => s.StartTimeUnixNano).First() ?? t.RootSpan;
            return new
            {
                t.TraceIdHex,
                t.SpanCount,
                t.MinStartTimeNano,
                t.MaxEndTimeNano,
                t.RootSpan,
                AnchorSpan = anchorSpan,
                DisplayServiceName = serviceSpans != null ? anchorSpan.ServiceName : t.ServiceName,
                DisplayHasErrors = serviceSpans?.Any(s => s.StatusCode == "ERROR") ?? t.HasErrors,
                DisplayStartNano = serviceSpans != null ? serviceSpans.Min(s => s.StartTimeUnixNano) : t.MinStartTimeNano,
                DisplayEndNano = serviceSpans != null ? serviceSpans.Max(s => s.EndTimeUnixNano) : t.MaxEndTimeNano,
            };
        }).ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            display.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(), ct);

        return display
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Select(t => new TraceInfoRow(
                ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano,
                    t.DisplayStartNano, t.DisplayEndNano, t.DisplayHasErrors, t.DisplayServiceName, t.AnchorSpan),
                t.RootSpan.Kind))
            .ToList();
    }

    /// <summary>
    /// Applies <see cref="TraceQuery.Sort"/>/<see cref="TraceQuery.Dir"/> (mode default when
    /// unset) to an already-computed row list. Deliberately reads only <see cref="TraceInfo"/>
    /// fields — every sort key <see cref="ComputeTraceInfoRowsUncachedAsync"/> used to compute
    /// off its richer internal shape has a same-value field on the mapped <see cref="TraceInfo"/>
    /// ("duration" ↔ <see cref="TraceInfo.TraceDuration"/>, "service" ↔
    /// <see cref="TraceInfo.ServiceName"/>, "operation" ↔ <see cref="TraceInfo.RootOperationName"/>,
    /// "spans"/"time" trace-wide as before) — so sorting can run after a cache hit, over rows that
    /// carry nothing but the wire shape (list-page-scale plan, Phase 3).
    /// </summary>
    private static List<TraceInfoRow> SortRows(List<TraceInfoRow> rows, TraceQuery query)
    {
        var asc = string.Equals(query.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        var isSlow = query.Mode == "slow";
        IOrderedEnumerable<TraceInfoRow> ordered = (query.Sort?.ToLowerInvariant()) switch
        {
            "duration"  => asc ? rows.OrderBy(r => r.Info.TraceDuration)     : rows.OrderByDescending(r => r.Info.TraceDuration),
            "spans"     => asc ? rows.OrderBy(r => r.Info.SpanCount)         : rows.OrderByDescending(r => r.Info.SpanCount),
            "time"      => asc ? rows.OrderBy(r => r.Info.TraceStartTime)    : rows.OrderByDescending(r => r.Info.TraceStartTime),
            "service"   => asc ? rows.OrderBy(r => r.Info.ServiceName)       : rows.OrderByDescending(r => r.Info.ServiceName),
            "operation" => asc ? rows.OrderBy(r => r.Info.RootOperationName) : rows.OrderByDescending(r => r.Info.RootOperationName),
            _           => isSlow ? rows.OrderByDescending(r => r.Info.TraceDuration) : rows.OrderByDescending(r => r.Info.TraceStartTime),
        };
        return ordered.ToList();
    }

    /// <summary>
    /// Fetches, groups and filters the query's trace population — via <see cref="TraceQueryCache"/>
    /// when an identical (tenant, filter, window, slim) scan is already cached, otherwise a fresh
    /// <see cref="ComputeTraceInfoRowsUncachedAsync"/> call — then applies the requested sort.
    /// Sort/page/offset are excluded from the cache key on purpose: they're views of the same
    /// list, not part of what the scan computed, so changing the sort column or paging past the
    /// traces list page's overview cap reuses the cached scan instead of re-running it
    /// (list-page-scale plan, Phase 3 — see <see cref="TraceQueryCache"/>'s own doc comment for
    /// the TTL-vs-auto-refresh reasoning).
    /// </summary>
    /// <param name="slim">See <see cref="ComputeTraceInfoRowsUncachedAsync"/>.</param>
    protected async Task<List<TraceInfoRow>> ComputeTraceInfosAsync(
        TraceQuery query, CancellationToken ct, bool slim = false)
    {
        var cacheKey = TraceQueryCacheKey(query, slim);
        if (!_traceQueryCache.TryGet(cacheKey, out List<TraceInfoRow> rows))
        {
            rows = await ComputeTraceInfoRowsUncachedAsync(query, ct, slim);
            _traceQueryCache.Set(cacheKey, rows);
        }
        return SortRows(rows, query);
    }

    /// <summary>
    /// Cache key for <see cref="TraceQueryCache"/>: tenant + everything <see cref="ComputeTraceInfoRowsUncachedAsync"/>
    /// reads to decide which rows come back — mode/service/operation/duration bounds/tags/window
    /// + the caller-supplied <paramref name="slim"/> flag (two callers can request the same
    /// filter/window with different <c>slim</c> and must not share a cached result — one would
    /// carry <see cref="TraceInfo.RootSpanAttributes"/> and the other wouldn't). Deliberately
    /// excludes <see cref="TraceQuery.Sort"/>/<see cref="TraceQuery.Dir"/>/<see cref="TraceQuery.Limit"/>/
    /// <see cref="TraceQuery.Offset"/> — see <see cref="ComputeTraceInfosAsync"/>. Field separator
    /// is ASCII US (0x1F): a printable delimiter could occur inside a service/operation/tag value
    /// and let two distinct filter sets collide into one key.
    /// </summary>
    private string TraceQueryCacheKey(TraceQuery query, bool slim)
    {
        const char sep = '';
        var tags = string.Join(sep, query.Tags.Select(t => $"{(t.Negate ? "-" : "")}{t.Key}{(t.Exact ? "=" : ":")}{t.Value}"));
        return string.Join(sep,
            "trace-query", TenantId, slim,
            query.Mode, query.Service ?? "", query.Operation ?? "",
            query.MinDurationMs, query.MaxDurationMs,
            TimeConversion.DateTimeToUnixNano(query.Start), TimeConversion.DateTimeToUnixNano(query.End),
            tags);
    }

    // =========================================================================
    // ANALYSIS READS
    // =========================================================================

    public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT
                pr.attributes_json AS ParentResourceAttributesJson,
                cr.attributes_json AS ChildResourceAttributesJson,
                child.kind         AS Kind,
                child.status_code  AS StatusCode,
                (child.end_time_unix_nano - child.start_time_unix_nano) AS DurationNano
            FROM spans child
            JOIN spans parent    ON child.parent_span_id = parent.span_id AND child.trace_id = parent.trace_id
            JOIN resources pr    ON parent.resource_id = pr.id
            JOIN resources cr    ON child.resource_id = cr.id
            WHERE cr.tenant_id = @tenantId AND pr.tenant_id = @tenantId
            """;
        if (startTime.HasValue) sql += " AND child.start_time_unix_nano >= @start";
        if (endTime.HasValue) sql += " AND child.start_time_unix_nano <= @end";

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<DependencyRow>(new CommandDefinition(sql, new
        {
            tenantId = TenantId,
            start = startTime.HasValue ? TimeConversion.DateTimeToUnixNano(startTime.Value) : (long?)null,
            end = endTime.HasValue ? TimeConversion.DateTimeToUnixNano(endTime.Value) : (long?)null
        }, cancellationToken: cancellationToken));

        var dependencies = rows
            .Select(x => new
            {
                ParentService = ServiceNameOf(x.ParentResourceAttributesJson),
                ChildService = ServiceNameOf(x.ChildResourceAttributesJson),
                Kind = Enum.Parse<SpanKind>(x.Kind),
                x.StatusCode,
                x.DurationNano
            })
            .Where(x => x.ParentService != null && x.ChildService != null && x.ParentService != x.ChildService)
            .ToList();

        return dependencies
            .GroupBy(x => new { x.ParentService, x.ChildService, x.Kind })
            .Select(g => new ServiceDependency
            {
                ParentService = g.Key.ParentService!,
                ChildService = g.Key.ChildService!,
                SpanKind = g.Key.Kind,
                CallCount = g.Count(),
                AvgDurationMs = g.Average(x => x.DurationNano) / 1_000_000.0,
                MinDurationMs = g.Min(x => x.DurationNano) / 1_000_000.0,
                MaxDurationMs = g.Max(x => x.DurationNano) / 1_000_000.0,
                ErrorCount = g.Count(x => x.StatusCode == "ERROR"),
                ErrorRate = (g.Count(x => x.StatusCode == "ERROR") / (double)g.Count()) * 100
            })
            .OrderByDescending(x => x.CallCount)
            .ToList();
    }

    public async Task<Dictionary<string, int>> GetOperationCountsAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.end_time_unix_nano");
        var sql = $"""
            SELECT s.name AS Name, r.attributes_json AS ResourceAttributesJson
            FROM spans s JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<NameAttrRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        return rows
            .Select(s => new { s.Name, ResourceAttributes = DeserializeAttributes(s.ResourceAttributesJson) })
            .Where(s => s.ResourceAttributes != null &&
                        s.ResourceAttributes.ContainsKey("service.name") &&
                        s.ResourceAttributes["service.name"]?.ToString() == serviceName)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.end_time_unix_nano");
        var sql = $"""
            SELECT s.name AS Name, s.start_time_unix_nano AS StartTimeUnixNano, s.end_time_unix_nano AS EndTimeUnixNano, r.attributes_json AS ResourceAttributesJson
            FROM spans s JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LatencyRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        return rows
            .Select(s => new { s.Name, s.StartTimeUnixNano, s.EndTimeUnixNano, ResourceAttributes = DeserializeAttributes(s.ResourceAttributesJson) })
            .Where(s => s.ResourceAttributes != null &&
                        s.ResourceAttributes.ContainsKey("service.name") &&
                        s.ResourceAttributes["service.name"]?.ToString() == serviceName)
            .GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.Average(s => s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0);
    }

    public async Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.end_time_unix_nano");
        var sql = $"""
            SELECT s.name AS Name, s.start_time_unix_nano AS StartTimeUnixNano, s.end_time_unix_nano AS EndTimeUnixNano, s.status_code AS StatusCode, r.attributes_json AS ResourceAttributesJson
            FROM spans s JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<StatsRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        // Rate is calls over the queried window; percentiles are computed in memory from the
        // per-span durations (portable across providers — no percentile_cont/approx SQL needed).
        var windowSeconds = Math.Max((endTime - startTime).TotalSeconds, 1);

        return rows
            .Select(s => new
            {
                s.Name,
                s.StatusCode,
                DurationMs = (s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0,
                ResourceAttributes = DeserializeAttributes(s.ResourceAttributesJson)
            })
            .Where(s => s.ResourceAttributes != null &&
                        s.ResourceAttributes.ContainsKey("service.name") &&
                        s.ResourceAttributes["service.name"]?.ToString() == serviceName)
            .GroupBy(s => s.Name)
            .Select(g =>
            {
                var durations = g.Select(x => x.DurationMs).OrderBy(x => x).ToList();
                var count = durations.Count;
                var errorCount = g.Count(x => x.StatusCode == "ERROR");
                return new OperationStats
                {
                    Operation = g.Key,
                    Count = count,
                    ErrorCount = errorCount,
                    ErrorRate = count > 0 ? errorCount / (double)count * 100 : 0,
                    RatePerSecond = count / windowSeconds,
                    AvgMs = count > 0 ? durations.Average() : 0,
                    P50Ms = Percentile(durations, 50),
                    P95Ms = Percentile(durations, 95),
                    P99Ms = Percentile(durations, 99),
                };
            })
            .OrderByDescending(o => o.Count)
            .ToList();
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>True when the span's resource carries this exact <c>service.name</c>.</summary>
    // Reads the resolved ServiceName rather than re-reading the attribute map, so it behaves
    // identically on the full and slim fetch paths (the slim one has no attribute map at all).
    private static bool MatchesService(RawSpan span, string service)
        => span.ServiceName == service;

    /// <summary>
    /// Predicate restricting the outer span query to traces containing at least one ERROR span.
    /// Filtering the spans themselves would strip each trace's root span whenever the error sits
    /// on a child, which then trips the root-span check and drops the trace entirely — the cause
    /// of a ~34% undercount in the errors view. Plain columns and a literal only (no JSON or
    /// dialect functions, no parameter list), so it is portable across every provider.
    /// <paramref name="innerTimeClause"/> is the optional time filter on the inner spans, already
    /// prefixed with <c> AND </c> (the caller owns which bounds apply).
    /// </summary>
    private static string ErrorTracePredicate(string innerTimeClause) => $"""
        s.trace_id IN (
            SELECT s2.trace_id
            FROM spans s2
            JOIN resources r2 ON s2.resource_id = r2.id
            WHERE r2.tenant_id = @tenantId{innerTimeClause} AND s2.status_code = 'ERROR'
        )
        """;

    /// <summary>
    /// Predicate restricting the outer span query to traces with at least one span belonging to
    /// this service (list-page-scale plan, Phase 4). Same trace-not-span narrowing discipline as
    /// <see cref="ErrorTracePredicate"/> — an exact match via the per-provider
    /// <see cref="DapperReadRepository.ResourceServiceNameExpr"/> hook, so this is safe to run
    /// without the C# <c>MatchesService</c> re-check that follows it (kept anyway as a free
    /// safety net, not because this predicate is known to under- or over-match).
    /// </summary>
    private string ServiceTracePredicate(string innerTimeClause) => $"""
        s.trace_id IN (
            SELECT s2.trace_id
            FROM spans s2
            JOIN resources r2 ON s2.resource_id = r2.id
            WHERE r2.tenant_id = @tenantId{innerTimeClause} AND {ResourceServiceNameExpr("r2")} = @service
        )
        """;

    /// <summary>Same shape as <see cref="ServiceTracePredicate"/>, for the operation (span name) filter — no dialect hook needed, it's a plain column.</summary>
    private static string OperationTracePredicate(string innerTimeClause) => $"""
        s.trace_id IN (
            SELECT s2.trace_id
            FROM spans s2
            JOIN resources r2 ON s2.resource_id = r2.id
            WHERE r2.tenant_id = @tenantId{innerTimeClause} AND s2.name = @operation
        )
        """;

    /// <summary>
    /// Predicate restricting the outer span query to traces with at least one span whose own or
    /// resource attributes contain this key (list-page-scale plan, Phase 4) — a coarse,
    /// deliberately over-inclusive pre-filter for a non-negated tag predicate; the C# <c>MatchesTag</c>
    /// re-check that follows is what actually enforces the value match (contains/exact, case
    /// folding, per-type string conversion), which is intractable to reproduce exactly in
    /// portable SQL (see <see cref="ComputeTraceInfoRowsUncachedAsync"/>'s tag `.Where`).
    ///
    /// Deliberately not used for a negated (<c>-key:value</c>) tag: the true predicate there is
    /// "no span satisfies key+value", and "the key doesn't exist anywhere in the trace" is a
    /// strictly stronger, wrong condition — it would wrongly exclude a trace where the key exists
    /// with a different, non-matching value. Negated tags are left entirely to the C# re-check,
    /// which still runs correctly because the outer query still returns every span of every trace
    /// that passed the (unrelated) predicates that did get pushed to SQL.
    /// </summary>
    private string TagKeyExistsPredicate(string keyParam, string innerTimeClause) => $"""
        s.trace_id IN (
            SELECT s2.trace_id
            FROM spans s2
            JOIN resources r2 ON s2.resource_id = r2.id
            WHERE r2.tenant_id = @tenantId{innerTimeClause}
              AND ({JsonHasKeyExpr("s2.attributes_json", keyParam)} OR {JsonHasKeyExpr("r2.attributes_json", keyParam)})
        )
        """;

    /// <summary>True when a span's own or resource attributes satisfy the tag predicate (case-insensitive).</summary>
    private static bool MatchesTag(RawSpan span, TagFilter tag)
    {
        foreach (var bag in new[] { span.SpanAttributes, span.ResourceAttributes })
        {
            if (bag != null && bag.TryGetValue(tag.Key, out var raw))
            {
                var value = raw == null ? "" : ConvertAttributeValueToString(raw);
                if (tag.Exact
                        ? string.Equals(value, tag.Value, StringComparison.OrdinalIgnoreCase)
                        : value.Contains(tag.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Nearest-rank percentile over an ascending-sorted list of values.</summary>
    private static double Percentile(IReadOnlyList<double> sortedAsc, double percentile)
    {
        if (sortedAsc.Count == 0) return 0;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedAsc.Count);
        var index = Math.Clamp(rank - 1, 0, sortedAsc.Count - 1);
        return sortedAsc[index];
    }

    /// <summary>
    /// Predicate matching <c>s.span_id</c> against the <c>@ids</c> list parameter. The correct
    /// form is provider-specific because Dapper handles list parameters differently per client:
    /// SqlServer (default) expands <c>IN @ids</c> into <c>IN (@ids1, @ids2, …)</c>, whereas Npgsql
    /// binds the list as a single native array parameter — so Postgres/Timescale override this
    /// to use <c>= ANY(@ids)</c>.
    /// </summary>
    protected virtual string SpanIdInPredicate => "s.span_id IN @ids";

    /// <summary>
    /// Batch size for the chunked existence check below (list-page-scale plan, Phase 4). SQL
    /// Server hard-caps a command at 2100 parameters total, and <see cref="SpanIdInPredicate"/>'s
    /// default form (<c>IN @ids</c>) is exactly the one Dapper expands into one placeholder per
    /// id there — so before chunking, a time window with enough pseudo-root candidate traces to
    /// exceed that cap didn't just get slow, it threw outright. Postgres/Timescale's
    /// <c>= ANY(@ids)</c> override binds the whole list as one array parameter and never hit this,
    /// but chunking applies uniformly rather than special-casing providers by their binding form.
    /// </summary>
    private const int SpanIdExistenceCheckChunkSize = 2000;

    private async Task<HashSet<string>> CheckSpanIdsExistAsync(IEnumerable<string> spanIds, CancellationToken ct)
    {
        var ids = spanIds.ToList();
        if (ids.Count == 0) return [];

        var result = new HashSet<string>();
        await using var conn = await OpenConnectionAsync(ct);
        foreach (var chunk in ids.Chunk(SpanIdExistenceCheckChunkSize))
        {
            var existing = await conn.QueryAsync<string>(
                $"""
                SELECT s.span_id
                FROM spans s
                JOIN resources r ON s.resource_id = r.id
                WHERE r.tenant_id = @tenantId
                  AND {SpanIdInPredicate}
                """,
                new { tenantId = TenantId, ids = chunk });
            result.UnionWith(existing);
        }
        return result;
    }

    /// <summary>
    /// Same per-provider binding shape as <see cref="SpanIdInPredicate"/>, for
    /// <c>s.trace_id</c> — used by <see cref="QueryTracePageFastAsync"/>'s Query 2 to fetch a
    /// page's spans (list-page-scale plan, Phase 5). A page is clamped to at most 1000 trace ids,
    /// so this doesn't need <see cref="SpanIdExistenceCheckChunkSize"/>'s chunking.
    /// </summary>
    protected virtual string TraceIdInPredicate => "s.trace_id IN @traceIds";

    /// <summary>
    /// One row per distinct trace in the window matching <paramref name="whereClause"/> — the
    /// complexity win behind both <see cref="QueryTracePageFastAsync"/> (Query 1) and
    /// <see cref="ComputeInboundTraceSummariesAsync"/> (list-page-scale plan, Phase 5 Parts 1 and
    /// 2): O(distinct traces), not O(spans), and no span JSON is fetched at this step. Enough to
    /// sort/page by "time"/"duration"/"spans"/the mode default, to run root detection, and — for
    /// Part 2 — to know each trace's root span kind, service name and error flag without a second
    /// query shape. Deliberately not a full per-trace projection — see
    /// <see cref="IsFastPagingEligible"/>/<see cref="IsFastAggregationEligible"/> for what still
    /// needs the old full scan.
    ///
    /// <see cref="TraceGroupSummaryRow.EarliestParentSpanId"/> and (for a trace with no in-window
    /// null-parent span) <see cref="TraceGroupSummaryRow.RootKind"/>/
    /// <see cref="TraceGroupSummaryRow.ServiceName"/> are resolved via a join back to
    /// <c>spans</c>/<c>resources</c> on <c>(trace_id, start_time_unix_nano) = (trace_id,
    /// MIN(start_time_unix_nano))</c> — i.e. the earliest in-window span, matching
    /// <c>ComputeTraceInfoRowsUncachedAsync</c>'s own <c>RootSpan = FirstOrDefault(ParentSpanId ==
    /// null) ?? first</c> fallback — rather than a window function or a correlated subquery:
    /// window-function support is uneven enough across the five providers that betting root
    /// detection on it felt riskier than a plain GROUP BY + JOIN, and a correlated subquery is the
    /// exact pattern Phase 4 avoided for ClickHouse portability (see that phase's note on
    /// <c>CheckSpanIdsExistAsync</c>). When a null-parent span *does* exist in-window, its kind is
    /// picked via <c>MAX(CASE WHEN parent_span_id IS NULL THEN kind END)</c> instead — arbitrarily,
    /// if more than one such span exists in the trace, same as the C# fallback's own
    /// <c>FirstOrDefault</c> is order-dependent (and therefore already arbitrary) in that case.
    ///
    /// The known cost: if two spans in the same trace share the *exact* same
    /// start_time_unix_nano, the self-join fans out to more than one row for that trace —
    /// astronomically unlikely for real nanosecond-precision timestamps, and guarded defensively
    /// by deduplicating on trace id (keeping an arbitrary one) rather than trusting the join to be
    /// 1:1.
    /// </summary>
    /// <param name="includeRootDetails">
    /// Part 1's paging (<see cref="QueryTracePageFastAsync"/>) only ever reads
    /// <see cref="TraceGroupSummaryRow.EarliestParentSpanId"/> off this row — root kind/name/span
    /// id and the resolved service name are Part 2-only (<see cref="ComputeTraceSummaryRowsAsync"/>).
    /// Computing them anyway cost Part 1 a measurable, needless slice of every request (an extra
    /// <see cref="DapperReadRepository.ResourceServiceNameExpr(string)"/> JSON extraction plus two
    /// more conditional aggregates, per group) once both paths were made to share this one query —
    /// this flag keeps that cost opt-in instead of baked into every paging request.
    /// </param>
    private async Task<List<TraceGroupSummaryRow>> FetchTraceGroupSummariesAsync(
        string whereClause, string havingClause, object parameters, bool includeRootDetails, CancellationToken ct)
    {
        var rootDetailColumns = includeRootDetails
            ? $"""
              ,
                      CASE WHEN g.has_null_parent_root = 1 THEN g.null_parent_kind    ELSE es.kind    END AS RootKind,
                      CASE WHEN g.has_null_parent_root = 1 THEN g.null_parent_name    ELSE es.name    END AS RootName,
                      CASE WHEN g.has_null_parent_root = 1 THEN g.null_parent_span_id ELSE es.span_id END AS RootSpanId,
                      {ResourceServiceNameExpr("er")} AS ServiceName
              """
            : "";
        var rootDetailAggregates = includeRootDetails
            ? """
              ,
                             MAX(CASE WHEN s.parent_span_id IS NULL THEN s.kind END) AS null_parent_kind,
                             MAX(CASE WHEN s.parent_span_id IS NULL THEN s.name END) AS null_parent_name,
                             MAX(CASE WHEN s.parent_span_id IS NULL THEN s.span_id END) AS null_parent_span_id
              """
            : "";
        var sql = $"""
            SELECT g.trace_id AS TraceId,
                   g.min_start AS MinStart,
                   g.max_end AS MaxEnd,
                   g.span_count AS SpanCount,
                   g.has_errors AS HasErrorsInt,
                   CASE WHEN g.has_null_parent_root = 1 THEN NULL ELSE es.parent_span_id END AS EarliestParentSpanId{rootDetailColumns}
            FROM (
                SELECT s.trace_id,
                       MIN(s.start_time_unix_nano) AS min_start,
                       MAX(s.end_time_unix_nano) AS max_end,
                       COUNT(*) AS span_count,
                       MAX(CASE WHEN s.status_code = 'ERROR' THEN 1 ELSE 0 END) AS has_errors,
                       MAX(CASE WHEN s.parent_span_id IS NULL THEN 1 ELSE 0 END) AS has_null_parent_root{rootDetailAggregates}
                FROM spans s
                JOIN resources r ON s.resource_id = r.id
                WHERE r.tenant_id = @tenantId AND {whereClause}
                GROUP BY s.trace_id
                {havingClause}
            ) g
            LEFT JOIN spans es ON es.trace_id = g.trace_id AND es.start_time_unix_nano = g.min_start
            LEFT JOIN resources er ON es.resource_id = er.id AND er.tenant_id = @tenantId
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<TraceGroupSummaryRow>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        // Defensive dedup — see this method's doc comment on the (extremely unlikely) join fan-out.
        return rows.GroupBy(r => r.TraceId).Select(g => g.First()).ToList();
    }

    private async Task<List<RawSpan>> FetchRawSpansAsync(string whereClause, object parameters, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                s.trace_id              AS TraceId,
                s.span_id               AS SpanId,
                s.start_time_unix_nano  AS StartTimeUnixNano,
                s.end_time_unix_nano    AS EndTimeUnixNano,
                s.status_code           AS StatusCode,
                s.name                  AS Name,
                s.parent_span_id        AS ParentSpanId,
                s.kind                  AS Kind,
                s.attributes_json       AS SpanAttributesJson,
                r.attributes_json       AS ResourceAttributesJson
            FROM spans s
            JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {whereClause}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<RawSpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.Select(r =>
        {
            var resourceAttributes = DeserializeAttributes(r.ResourceAttributesJson);
            return new RawSpan
            {
                TraceId = r.TraceId,
                SpanId = r.SpanId,
                StartTimeUnixNano = r.StartTimeUnixNano,
                EndTimeUnixNano = r.EndTimeUnixNano,
                StatusCode = r.StatusCode,
                Name = r.Name,
                ParentSpanId = r.ParentSpanId,
                Kind = r.Kind,
                ServiceName = ExtractServiceName(resourceAttributes),
                SpanAttributes = DeserializeAttributes(r.SpanAttributesJson),
                ResourceAttributes = resourceAttributes
            };
        }).ToList();
    }

    /// <summary>
    /// The same span set as <see cref="FetchRawSpansAsync"/> without either attributes column.
    ///
    /// The full fetch ships two JSONB blobs per span and parses both. Resource attributes are the
    /// expensive half and the most wasteful: they are per-<em>resource</em> data fetched
    /// per-<em>span</em>, so a window with 100k spans over 30 resources detoasts, transfers and
    /// deserializes the same 30 documents 100k times, to read one string out of each. Here the
    /// span query carries <c>resource_id</c> instead, and a second query resolves the tenant's
    /// resources once — 30 parses, not 100,000.
    ///
    /// The cost is that <c>SpanAttributes</c> and <c>ResourceAttributes</c> come back null, so
    /// this path cannot serve tag predicates or <see cref="TraceInfo.RootSpanAttributes"/>.
    /// <see cref="ComputeTraceInfosAsync"/> owns that guard; do not call this directly.
    /// </summary>
    private async Task<List<RawSpan>> FetchRawSpansSlimAsync(string whereClause, object parameters, CancellationToken ct)
    {
        var spanSql = $"""
            SELECT
                s.trace_id              AS TraceId,
                s.span_id               AS SpanId,
                s.start_time_unix_nano  AS StartTimeUnixNano,
                s.end_time_unix_nano    AS EndTimeUnixNano,
                s.status_code           AS StatusCode,
                s.name                  AS Name,
                s.parent_span_id        AS ParentSpanId,
                s.kind                  AS Kind,
                s.resource_id           AS ResourceId
            FROM spans s
            JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {whereClause}
            """;

        // Scoped to the tenant rather than to the window's spans: bounded by distinct resource
        // attribute sets (tens, typically), and narrowing it further would cost a second scan of
        // `spans` to collect the referenced ids — more than the rows it would save.
        const string resourceSql = """
            SELECT id AS Id, attributes_json AS AttributesJson
            FROM resources
            WHERE tenant_id = @tenantId
            """;

        await using var conn = await OpenConnectionAsync(ct);

        var resourceRows = await conn.QueryAsync<ResourceServiceRow>(new CommandDefinition(
            resourceSql, new { tenantId = TenantId }, cancellationToken: ct));
        var serviceByResourceId = resourceRows.ToDictionary(r => r.Id, r => ServiceNameOf(r.AttributesJson));

        var rows = await conn.QueryAsync<SlimSpanRow>(new CommandDefinition(spanSql, parameters, cancellationToken: ct));
        return rows.Select(r => new RawSpan
        {
            TraceId = r.TraceId,
            SpanId = r.SpanId,
            StartTimeUnixNano = r.StartTimeUnixNano,
            EndTimeUnixNano = r.EndTimeUnixNano,
            StatusCode = r.StatusCode,
            Name = r.Name,
            ParentSpanId = r.ParentSpanId,
            Kind = r.Kind,
            ServiceName = serviceByResourceId.GetValueOrDefault(r.ResourceId),
        }).ToList();
    }

    private (string where, object parameters) BuildTimeFilter(DateTime? startTime, DateTime? endTime, string startBound, string endBound, string? extra = null)
    {
        var clauses = new List<string>();
        if (extra != null) clauses.Add(extra);
        if (startTime.HasValue) clauses.Add($"{startBound} >= @start");
        if (endTime.HasValue) clauses.Add($"{endBound} <= @end");
        var where = clauses.Count > 0 ? string.Join(" AND ", clauses) : "1 = 1";
        return (where, new
        {
            tenantId = TenantId,
            start = startTime.HasValue ? TimeConversion.DateTimeToUnixNano(startTime.Value) : (long?)null,
            end = endTime.HasValue ? TimeConversion.DateTimeToUnixNano(endTime.Value) : (long?)null
        });
    }

    private static string? ServiceNameOf(string? attributesJson)
        => ExtractServiceName(DeserializeAttributes(attributesJson));

    /// <summary>
    /// Maps a computed trace group to its API shape. <paramref name="anchorSpan"/>/
    /// <paramref name="serviceName"/>/<paramref name="hasErrors"/>/<paramref name="displayStartNano"/>/
    /// <paramref name="displayEndNano"/> reflect the trace's true root and full span set when no
    /// service filter produced this row, or the filtered service's own anchor span (its
    /// earliest-started span) and the aggregate of just that service's spans when one did — see
    /// the callers' "Display*"/"Anchor*" locals. <paramref name="minStartNano"/>/
    /// <paramref name="maxEndNano"/> always describe the whole trace, regardless of filter.
    /// </summary>
    private static TraceInfo ToTraceInfo(
        string traceId, int spanCount, long minStartNano, long maxEndNano,
        long displayStartNano, long displayEndNano, bool hasErrors, string? serviceName, RawSpan anchorSpan) => new()
    {
        TraceIdHex = traceId,
        SpanCount = spanCount,
        TraceStartTime = TimeConversion.UnixNanoToDateTime(minStartNano),
        TraceEndTime = TimeConversion.UnixNanoToDateTime(maxEndNano),
        HasErrors = hasErrors,
        ServiceName = serviceName,
        RootOperationName = anchorSpan.Name,
        RootSpanAttributes = anchorSpan.SpanAttributes,
        // Computed from the raw nanosecond values, not by subtracting two already-converted
        // DateTimes (which truncates twice) — matches GetOperationStatsAsync's precedent and
        // fixes the trace-latency-p50 plan's Cause B (p50 pinned at exactly 0µs).
        TraceDuration = TimeSpan.FromTicks((displayEndNano - displayStartNano) / 100),
        DisplaySpanIdHex = anchorSpan.SpanId,
    };

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class RawSpan
    {
        public string TraceId { get; set; } = null!;
        public string SpanId { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public string Kind { get; set; } = "UNSPECIFIED";

        /// <summary>
        /// Resolved once at fetch time, by both the full and slim paths, so callers never have to
        /// know which one produced the span. On the slim path it comes from a per-resource lookup
        /// rather than this span's own (unfetched) resource attributes.
        /// </summary>
        public string? ServiceName { get; set; }

        /// <summary>Null on the slim path — see <see cref="FetchRawSpansSlimAsync"/>.</summary>
        public Dictionary<string, object>? SpanAttributes { get; set; }

        /// <summary>Null on the slim path — see <see cref="FetchRawSpansSlimAsync"/>.</summary>
        public Dictionary<string, object>? ResourceAttributes { get; set; }
    }

    private sealed class RawSpanRow
    {
        public string TraceId { get; set; } = null!;
        public string SpanId { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public string Kind { get; set; } = "UNSPECIFIED";
        public string? SpanAttributesJson { get; set; }
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class SlimSpanRow
    {
        public string TraceId { get; set; } = null!;
        public string SpanId { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public string Kind { get; set; } = "UNSPECIFIED";
        public long ResourceId { get; set; }
    }

    /// <summary>Row shape for <see cref="FetchTraceGroupSummariesAsync"/> — see its doc comment.</summary>
    private sealed class TraceGroupSummaryRow
    {
        public string TraceId { get; set; } = null!;
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
        public int SpanCount { get; set; }
        public int HasErrorsInt { get; set; }
        public string? EarliestParentSpanId { get; set; }
        /// <summary>Used only by <see cref="ComputeTraceSummaryRowsAsync"/> (Part 2); unused by Part 1's paging.</summary>
        public string? RootKind { get; set; }
        /// <summary>Used only by <see cref="ComputeTraceSummaryRowsAsync"/> (Part 2); unused by Part 1's paging.</summary>
        public string? RootName { get; set; }
        /// <summary>Used only by <see cref="ComputeTraceSummaryRowsAsync"/> (Part 2); unused by Part 1's paging.</summary>
        public string? RootSpanId { get; set; }
        /// <summary>Used only by <see cref="ComputeTraceSummaryRowsAsync"/> (Part 2); unused by Part 1's paging.</summary>
        public string? ServiceName { get; set; }
    }

    private sealed class ResourceServiceRow
    {
        public long Id { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class FullSpanRow
    {
        public long Id { get; set; }
        public string TraceId { get; set; } = null!;
        public string SpanId { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public string Name { get; set; } = null!;
        public string Kind { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int DroppedEventsCount { get; set; }
        public int DroppedLinksCount { get; set; }
        public string? TraceState { get; set; }
        public int Flags { get; set; }
        public string StatusCode { get; set; } = null!;
        public string? StatusMessage { get; set; }
        public string? AttributesJson { get; set; }
        public string? EventsJson { get; set; }
        public string? LinksJson { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string ScopeName { get; set; } = null!;
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }

    private sealed class DependencyRow
    {
        public string? ParentResourceAttributesJson { get; set; }
        public string? ChildResourceAttributesJson { get; set; }
        public string Kind { get; set; } = null!;
        public string StatusCode { get; set; } = null!;
        public long DurationNano { get; set; }
    }

    private sealed class NameAttrRow
    {
        public string Name { get; set; } = null!;
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class LatencyRow
    {
        public string Name { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class StatsRow
    {
        public string Name { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string? ResourceAttributesJson { get; set; }
    }
}
