using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ITraceReadRepository"/>. Since schema 3.0.0 every hot read
/// filters on <c>spans.tenant_id</c>/<c>spans.service_name</c> directly (no join to
/// <c>resources</c>), joining <c>resources</c> only where its attributes are actually selected
/// (span detail, resource-attribute search).
///
/// The list page's summary/page/samples/export endpoints share one derived table, the
/// <b>anchors</b> (see <see cref="AnchorsSql"/>): one row per trace, being the trace's EARLIEST span
/// in scope -- the selected service's own earliest span when a service is selected, the whole
/// trace's otherwise (schema-simplification decision 10). That replaces the 2.x root-plus-orphan
/// anchors, the rollup tables and <c>orphan_roots</c>: a trace without a root, or whose root arrives
/// late, simply anchors on whatever its earliest span is at the time of the query.
/// </summary>
public abstract class TraceReadRepositoryBase : DapperReadRepository, ITraceReadRepository
{
    /// <summary>See <see cref="LogReadRepositoryBase"/>'s identical field for why this default exists.</summary>
    private readonly int _summaryTimeoutSeconds = 5;

    private readonly long _anchorLookbackNanos = QueryOptions.DefaultAnchorLookbackMinutes * NanosPerMinute;

    private const long NanosPerMinute = 60_000_000_000L;

    protected TraceReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    protected TraceReadRepositoryBase(ITenantContext tenantContext, IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
        var lookbackMinutes = int.TryParse(configuration[$"{QueryOptions.SectionName}:AnchorLookbackMinutes"], out var lookback) && lookback >= 0
            ? lookback
            : QueryOptions.DefaultAnchorLookbackMinutes;
        _anchorLookbackNanos = lookbackMinutes * NanosPerMinute;
    }

    // =========================================================================
    // DIALECT HOOKS
    // =========================================================================

    /// <summary>
    /// A predicate matching <paramref name="column"/> against <paramref name="ids"/>, binding each id
    /// through <see cref="IdParam"/> into <paramref name="parameters"/>. PostgreSQL overrides it to bind
    /// one native array (<c>= ANY</c>).
    /// </summary>
    protected virtual string IdInPredicate(string column, string paramPrefix, IReadOnlyList<string> ids, int length, DynamicParameters parameters)
    {
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"{paramPrefix}{i}";
            parameters.Add(name, IdParam(ids[i], length));
            names.Add($"@{name}");
        }
        return $"{column} IN ({string.Join(",", names)})";
    }

    /// <summary>
    /// Optional <c>[min, max]</c> start-time bounds of the given traces, used to narrow a by-trace-id
    /// read of <c>spans</c>. The relational providers seek <c>(trace_id, span_id)</c> and need none
    /// (null). ClickHouse, whose <c>spans</c> sort key is <c>(tenant_id, service_name, start_time)</c>
    /// and has no trace-id seek, overrides it to read <c>trace_index</c>, so a by-trace read touches
    /// only the partitions and granules that can hold the trace.
    /// </summary>
    protected virtual Task<(long Min, long Max)?> ResolveTraceTimeBoundsAsync(
        System.Data.Common.DbConnection conn, IReadOnlyList<string> traceIds, CancellationToken ct)
        => Task.FromResult<(long Min, long Max)?>(null);

    /// <summary>Appends <c>start_time_unix_nano</c> bounds (parameters <c>@boundMin</c>/<c>@boundMax</c>) to a by-trace-id WHERE when the provider supplied them.</summary>
    private static string BoundsClause(string alias, (long Min, long Max)? bounds, DynamicParameters parameters)
    {
        if (bounds is not { } b) return "";
        parameters.Add("boundMin", b.Min);
        parameters.Add("boundMax", b.Max);
        return $" AND {alias}.start_time_unix_nano >= @boundMin AND {alias}.start_time_unix_nano <= @boundMax";
    }

    // =========================================================================
    // FULL SPAN READS (span + resource + scope + events + links)
    // =========================================================================

    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var bounds = await ResolveTraceTimeBoundsAsync(conn, [traceIdHex], cancellationToken);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("traceId", IdParam(traceIdHex, 32));
        var spans = await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId" + BoundsClause("s", bounds, parameters), "ORDER BY s.start_time_unix_nano",
            parameters, cancellationToken);

        // A re-delivered span batch is stored again (no unique key, schema-simplification decision 7);
        // the detail view shows each span once, keeping the first stored copy.
        return DistinctSpans(spans);
    }

    public async Task<SpanModel?> GetSpanByIdAsync(string traceIdHex, string spanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(spanIdHex))
            throw new ArgumentException("Span ID cannot be null or empty", nameof(spanIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var bounds = await ResolveTraceTimeBoundsAsync(conn, [traceIdHex], cancellationToken);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("traceId", IdParam(traceIdHex, 32));
        parameters.Add("spanId", IdParam(spanIdHex, 16));
        var spans = await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId AND s.span_id = @spanId" + BoundsClause("s", bounds, parameters), null,
            parameters, cancellationToken);
        return spans.FirstOrDefault();
    }

    public async Task<List<SpanModel>> GetSpansByParentAsync(string traceIdHex, string parentSpanIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (string.IsNullOrEmpty(parentSpanIdHex))
            throw new ArgumentException("Parent Span ID cannot be null or empty", nameof(parentSpanIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var bounds = await ResolveTraceTimeBoundsAsync(conn, [traceIdHex], cancellationToken);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("traceId", IdParam(traceIdHex, 32));
        parameters.Add("parentSpanId", IdParam(parentSpanIdHex, 16));
        var spans = await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId AND s.parent_span_id = @parentSpanId" + BoundsClause("s", bounds, parameters), "ORDER BY s.start_time_unix_nano",
            parameters, cancellationToken);
        return DistinctSpans(spans);
    }

    private static List<SpanModel> DistinctSpans(List<SpanModel> spans)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return spans.Where(s => seen.Add(s.SpanIdHex)).ToList();
    }

    private async Task<List<SpanModel>> LoadFullSpansAsync(
        System.Data.Common.DbConnection conn, string whereClause, string? orderClause, DynamicParameters parameters, CancellationToken ct)
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
            JOIN {ResourcesTable} r               ON s.resource_id = r.id
            JOIN {ScopesTable} sc ON s.scope_id = sc.id
            WHERE s.tenant_id = @tenantId AND {whereClause}
            {orderClause}
            """;

        // One query: a span's events and links come back as JSON columns on the span row itself.
        var rows = (await conn.QueryAsync<FullSpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return new List<SpanModel>();

        return rows.Select(MapSpan).ToList();
    }

    private static SpanModel MapSpan(FullSpanRow r) => new()
    {
        TraceIdHex = r.TraceId,
        SpanIdHex = r.SpanId,
        // ClickHouse stores "no parent" / "no state" / "no message" as '' (non-Nullable columns);
        // the API model keeps them null.
        ParentSpanIdHex = string.IsNullOrEmpty(r.ParentSpanId) ? null : r.ParentSpanId,
        Name = r.Name,
        Kind = Enum.Parse<SpanKind>(r.Kind),
        StartTimeUnixNano = r.StartTimeUnixNano,
        EndTimeUnixNano = r.EndTimeUnixNano,
        DroppedAttributesCount = r.DroppedAttributesCount,
        DroppedEventsCount = r.DroppedEventsCount,
        DroppedLinksCount = r.DroppedLinksCount,
        TraceState = string.IsNullOrEmpty(r.TraceState) ? null : r.TraceState,
        Flags = r.Flags,
        StatusCode = Enum.Parse<SpanStatusCode>(r.StatusCode),
        StatusMessage = string.IsNullOrEmpty(r.StatusMessage) ? null : r.StatusMessage,
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
    // ANCHORS (schema-simplification plan, decisions 10-12, 15-18)
    //
    // Every summary/page/samples/export query below anchors on this derived table: one row per
    // trace, carrying the trace's earliest span in scope (its start, end, kind, name, service and
    // created_at) plus whether any span in scope has status ERROR. Duration everywhere is the
    // anchor's own end - start, so the row, the slow filter and the summary charts all agree.
    // =========================================================================

    /// <summary>
    /// The anchors derived table. One pass over the tenant's (or the selected service's) spans in
    /// <c>[@anchorFrom, @end]</c> -- <c>@anchorFrom</c> is <c>@start</c> minus the look-back margin
    /// (<see cref="QueryOptions.AnchorLookbackMinutes"/>), so a trace that began before the window is
    /// not anchored on a later span -- ranking each trace's spans by <c>(start, id)</c> and computing
    /// the in-scope error flag in the same pass; keeps rank 1 and drops anchors that start before
    /// <c>@start</c>. Parameters: <c>@tenantId</c>, <c>@anchorFrom</c>, <c>@start</c>, <c>@end</c>, plus
    /// <c>@service</c> when <paramref name="hasService"/> and <c>@asOf</c> when
    /// <paramref name="pinAsOf"/> (spans created after the pin are excluded BEFORE ranking, so a root
    /// that arrives late is not the anchor within a pinned query).
    /// <paramref name="includeFirstCreated"/> adds <c>first_created_at</c> (the trace's earliest
    /// <c>created_at</c> in scope), which the "new since the pin" count compares with the pin.
    /// ClickHouse overrides this with a <c>GROUP BY trace_id</c> form.
    /// </summary>
    protected virtual string AnchorsSql(bool hasService, bool pinAsOf, bool includeFirstCreated = false)
    {
        var service = hasService ? " AND s.service_name = @service" : "";
        var pin = pinAsOf ? " AND s.created_at <= @asOf" : "";
        var firstCreated = includeFirstCreated ? ", MIN(s.created_at) OVER (PARTITION BY s.trace_id) AS first_created_at" : "";
        var outerFirstCreated = includeFirstCreated ? ", x.first_created_at" : "";
        return $"""
            (
                SELECT x.trace_id, x.anchor_span_pk, x.anchor_span_id, x.service_name, x.root_name, x.anchor_kind,
                       x.anchor_start, x.anchor_end, x.anchor_created_at, x.has_error{outerFirstCreated}
                FROM (
                    SELECT s.trace_id AS trace_id, s.id AS anchor_span_pk, s.span_id AS anchor_span_id,
                           s.service_name AS service_name, s.name AS root_name, s.kind AS anchor_kind,
                           s.start_time_unix_nano AS anchor_start, s.end_time_unix_nano AS anchor_end,
                           s.created_at AS anchor_created_at,
                           ROW_NUMBER() OVER (PARTITION BY s.trace_id ORDER BY s.start_time_unix_nano, s.id) AS rn,
                           MAX(CASE WHEN s.status_code = 'ERROR' THEN 1 ELSE 0 END) OVER (PARTITION BY s.trace_id) AS has_error{firstCreated}
                    FROM spans s
                    WHERE s.tenant_id = @tenantId
                      AND s.start_time_unix_nano >= @anchorFrom AND s.start_time_unix_nano <= @end{service}{pin}
                ) x
                WHERE x.rn = 1 AND x.anchor_start >= @start
            )
            """;
    }

    // =========================================================================
    // SUMMARY
    // =========================================================================

    public async Task<TraceSummaryResult> GetTraceSummaryAsync(TraceSummaryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await ExecuteWithRetryAsync(() => GetTraceSummaryCoreAsync(query, cancellationToken));
    }

    private async Task<TraceSummaryResult> GetTraceSummaryCoreAsync(TraceSummaryQuery query, CancellationToken cancellationToken)
    {
        var parsed = SearchQueryParser.Parse(query.Search);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        // Always the raw path: there are no rollup tables since schema 3.0.0. The chart/cards come
        // from the anchor rows themselves; 3d/7d windows on a busy tenant may time out to "≥ N".
        var result = await GetRawSummaryAsync(conn, query, parsed, startNano, endNano, cancellationToken);

        // listTotal always counts every anchor (all kinds), independent of the cards' inbound-only population.
        var (listTotal, totalIsLowerBound) = await GetListTotalAsync(conn, query, parsed, startNano, endNano, asOf, cancellationToken);
        var newSince = await CountNewSinceAsOfAsync(conn, query, parsed, startNano, endNano, asOf, cancellationToken);

        return new TraceSummaryResult
        {
            Source = result.Source,
            Buckets = result.Buckets,
            Summary = result.Summary,
            Services = result.Services,
            LatencyBuckets = result.LatencyBuckets,
            RequestCount = result.RequestCount,
            ListTotal = listTotal,
            TotalIsLowerBound = result.TotalIsLowerBound || totalIsLowerBound,
            NewSinceAsOf = newSince,
            AsOf = asOf
        };
    }

    private async Task<TraceSummaryResult> GetRawSummaryAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, CancellationToken cancellationToken)
    {
        var (rows, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await FetchSummaryAnchorsAsync(
                conn, query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs,
                parsed, startNano, endNano, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);

        if (timedOut || rows == null)
        {
            return new TraceSummaryResult { Source = "raw", Buckets = [], TotalIsLowerBound = true };
        }

        var bucketCount = Math.Clamp(query.BucketCount, 1, 500);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var buckets = new List<TraceVolumeBucket>(bucketCount);
        var byIndex = new List<AnchorSummaryRow>[bucketCount];
        for (var i = 0; i < bucketCount; i++) byIndex[i] = [];
        foreach (var r in rows)
        {
            var idx = (int)((TimeConversion.UnixNanoToDateTime(r.AnchorStart).Ticks - startTicks) * bucketCount / rangeTicks);
            idx = Math.Clamp(idx, 0, bucketCount - 1);
            byIndex[idx].Add(r);
        }
        for (var i = 0; i < bucketCount; i++)
        {
            var group = byIndex[i];
            var durations = group.Select(DurationMs).OrderBy(x => x).ToList();
            buckets.Add(new TraceVolumeBucket
            {
                Timestamp = new DateTime(startTicks + (long)i * rangeTicks / bucketCount, query.Start.Kind),
                Count = group.Count,
                ErrorCount = group.Count(r => r.HasErrorInt != 0),
                SumDurationMs = durations.Sum(),
                P50Ms = Percentile(durations, 50),
                P95Ms = Percentile(durations, 95),
                P99Ms = Percentile(durations, 99),
            });
        }

        var allDurations = rows.Select(DurationMs).OrderBy(x => x).ToList();
        var windowSeconds = Math.Max((endNano - startNano) / 1_000_000_000.0, 1);
        var services = rows.GroupBy(r => string.IsNullOrEmpty(r.ServiceName) ? "(unknown)" : r.ServiceName)
            .Select(g =>
            {
                var durations = g.Select(DurationMs).OrderBy(x => x).ToList();
                var count = durations.Count;
                var errorCount = g.Count(r => r.HasErrorInt != 0);
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

        return new TraceSummaryResult
        {
            Source = "raw",
            Buckets = buckets,
            Summary = new TraceWindowSummary
            {
                Count = rows.Count,
                ErrorCount = rows.Count(r => r.HasErrorInt != 0),
                P50Ms = Percentile(allDurations, 50),
                P95Ms = Percentile(allDurations, 95),
                P99Ms = Percentile(allDurations, 99),
            },
            Services = services,
            LatencyBuckets = BuildLatencyBucketsFromRows(rows, query),
            RequestCount = rows.Count,
            TotalIsLowerBound = false,
        };
    }

    private static double DurationMs(AnchorSummaryRow r) => (r.AnchorEnd - r.AnchorStart) / 1_000_000.0;

    /// <summary>Latency heatmap: a proper time x duration grid over the fetched anchor rows.</summary>
    private static List<TraceLatencyBucket> BuildLatencyBucketsFromRows(List<AnchorSummaryRow> rows, TraceSummaryQuery query)
    {
        if (rows.Count == 0) return [];

        var timeCols = Math.Clamp(query.BucketCount, 1, 200);
        var durationRows = Math.Clamp(query.LatencyDurationRows, 1, 100);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var durationsMs = rows.Select(DurationMs).ToList();
        var yMin = Math.Max(1.0, durationsMs.Min());
        var yMax = Math.Max(yMin * 10, durationsMs.Max());
        var logMin = Math.Log(yMin);
        var logMax = Math.Log(yMax);
        var logStep = (logMax - logMin) / durationRows;

        var cells = new Dictionary<(int Col, int Row), (int Count, int ErrorCount, string FirstTraceIdHex)>();
        foreach (var r in rows)
        {
            var startTime = TimeConversion.UnixNanoToDateTime(r.AnchorStart);
            var col = Math.Clamp((int)((startTime.Ticks - startTicks) * timeCols / rangeTicks), 0, timeCols - 1);
            var durationMs = DurationMs(r);
            var row = durationMs <= yMin ? 0 : Math.Clamp((int)((Math.Log(durationMs) - logMin) / logStep), 0, durationRows - 1);
            var key = (col, row);
            cells[key] = cells.TryGetValue(key, out var existing)
                ? (existing.Count + 1, existing.ErrorCount + (r.HasErrorInt != 0 ? 1 : 0), existing.FirstTraceIdHex)
                : (1, r.HasErrorInt != 0 ? 1 : 0, r.TraceId);
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

    /// <summary><c>listTotal</c>: an exact (or capped, on timeout) count of every anchor matching the filter (all kinds), as of the pin.</summary>
    private async Task<(long Total, bool IsLowerBound)> GetListTotalAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, DateTime asOf, CancellationToken cancellationToken)
    {
        var hasService = !string.IsNullOrEmpty(query.Service);
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        parameters.Add("asOf", asOf);
        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        var anchors = AnchorsSql(hasService, pinAsOf: true);
        var sql = $"SELECT COUNT(*) FROM {anchors} a WHERE {where}";

        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);

        if (!timedOut) return (result, false);

        // Capped fallback: count a capped candidate set instead.
        var cappedSql = $"""
            SELECT COUNT(*) FROM (
                SELECT 1 AS x FROM {anchors} a
                WHERE {where}
                ORDER BY a.anchor_start DESC
                {PagingClause}
            ) capped
            """;
        var cappedParams = new DynamicParameters(parameters);
        cappedParams.Add("limit", 10_001);
        cappedParams.Add("offset", 0);
        var capped = await conn.ExecuteScalarAsync<long>(new CommandDefinition(cappedSql, cappedParams, cancellationToken: cancellationToken));
        return (capped, true);
    }

    /// <summary>
    /// The "new since the pin" count: traces matching the filter whose earliest in-scope span was
    /// created after <paramref name="asOf"/> -- i.e. traces the pinned view cannot contain at all.
    /// Computed over the unpinned span set.
    /// </summary>
    private async Task<long> CountNewSinceAsOfAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, DateTime asOf, CancellationToken cancellationToken)
    {
        var hasService = !string.IsNullOrEmpty(query.Service);
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        clauses.Add("a.first_created_at > @asOf");
        parameters.Add("asOf", asOf);
        var sql = $"SELECT COUNT(*) FROM {AnchorsSql(hasService, pinAsOf: false, includeFirstCreated: true)} a WHERE {string.Join(" AND ", clauses)}";
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    // =========================================================================
    // PAGE (keyset paging)
    // =========================================================================

    public async Task<TracePageResult> GetTracePageAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await ExecuteWithRetryAsync(() => GetTracePageCoreAsync(query, cancellationToken));
    }

    private async Task<TracePageResult> GetTracePageCoreAsync(TraceQuery query, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(query.Size, 1, 500);
        var parsed = SearchQueryParser.Parse(query.Search);
        var hasService = !string.IsNullOrEmpty(query.Service);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        parameters.Add("asOf", asOf);
        var anchors = AnchorsSql(hasService, pinAsOf: true);

        var filterHashText = $"{query.Start:O}|{query.End:O}|{asOf:O}|{query.Mode}|{query.Service}|{query.Operation}|{query.MinDurationMs}|{query.MaxDurationMs}|{query.Search}";
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        DecodedCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = KeysetCursor.Decode(query.Cursor);
            if (cursor == null || !KeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        List<AnchorRow> rows;
        bool forward;
        int requestedSize;

        switch (nav)
        {
            case "next":
                forward = true;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("a.anchor_start", "a.anchor_span_pk", "cursorK", "cursorId", descending: true));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;

            case "prev":
                forward = false;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("a.anchor_start", "a.anchor_span_pk", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            case "last":
                forward = false;
                var lastCount = await TryGetExactAnchorCountAsync(conn, anchors, clauses, parameters, cancellationToken);
                requestedSize = lastCount.HasValue ? KeysetCursor.LastPageRowCount(lastCount.Value, size) : size;
                rows = await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            default: // "first"
                forward = true;
                requestedSize = size;
                rows = await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;
        }

        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<AnchorRow> displayRows;
        string? nextCursor;
        string? prevCursor;

        if (forward)
        {
            displayRows = rows;
            nextCursor = hasExtra ? Encode(displayRows[^1], filterHash) : null;
            prevCursor = nav == "first" ? null : (displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null);
        }
        else
        {
            displayRows = [.. rows];
            displayRows.Reverse();
            if (nav == "last")
            {
                nextCursor = null;
                var reachedStart = !hasExtra;
                prevCursor = reachedStart || displayRows.Count == 0 ? null : Encode(displayRows[0], filterHash);
            }
            else // prev
            {
                nextCursor = displayRows.Count > 0 ? Encode(displayRows[^1], filterHash) : null;
                prevCursor = hasExtra && displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null;
            }
        }

        // Exact span counts and whole-trace bounds for just this page's anchors (bounded by `size`).
        var items = await LoadPageTraceInfosAsync(conn, displayRows, query.Service, cancellationToken);

        return new TracePageResult { Items = items, NextCursor = nextCursor, PrevCursor = prevCursor, AsOf = asOf };
    }

    private static string Encode(AnchorRow row, string filterHash) => KeysetCursor.Encode(row.AnchorStart, row.AnchorSpanPk, filterHash);

    private const string AnchorSelectColumns = """
        a.trace_id AS TraceId, a.anchor_span_pk AS AnchorSpanPk, a.anchor_span_id AS AnchorSpanId, a.service_name AS ServiceName,
        a.root_name AS RootName, a.anchor_kind AS AnchorKind, a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd,
        a.has_error AS HasErrorInt
        """;

    private async Task<List<AnchorRow>> FetchAnchorPageAsync(
        System.Data.Common.DbConnection conn, string anchors, List<string> clauses, DynamicParameters parameters,
        int size, bool descending, CancellationToken cancellationToken)
    {
        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT {AnchorSelectColumns}
            FROM {anchors} a
            WHERE {where}
            ORDER BY a.anchor_start {order}, a.anchor_span_pk {order}
            {PagingClause}
            """;
        var clone = new DynamicParameters(parameters);
        clone.Add("limit", size + 1);
        clone.Add("offset", 0);
        var rows = await conn.QueryAsync<AnchorRow>(new CommandDefinition(sql, clone, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<long?> TryGetExactAnchorCountAsync(
        System.Data.Common.DbConnection conn, string anchors, List<string> clauses, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        var sql = $"SELECT COUNT(*) FROM {anchors} a WHERE {where}";
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return timedOut ? null : result;
    }

    /// <summary>
    /// Shapes the page's anchors into <see cref="TraceInfo"/> rows. Duration, error flag, service,
    /// operation and kind come straight from the anchor (decisions 10-11); the follow-up query supplies
    /// only the exact span count (decisions 14, 18) and the whole trace's start/end for the detail link.
    /// </summary>
    private async Task<List<TraceInfo>> LoadPageTraceInfosAsync(
        System.Data.Common.DbConnection conn, List<AnchorRow> anchors, string? service, CancellationToken ct)
    {
        if (anchors.Count == 0) return [];

        var traceIds = anchors.Select(a => a.TraceId).Distinct().ToList();
        var bounds = await ResolveTraceTimeBoundsAsync(conn, traceIds, ct);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        var inList = IdInPredicate("fs.trace_id", "traceId", traceIds, 32, parameters);
        var scoped = !string.IsNullOrEmpty(service);
        if (scoped) parameters.Add("service", service);

        // COUNT(DISTINCT span_id): a re-delivered span is stored twice and must count once. With a
        // service selected the count is that service's spans in the trace; otherwise the whole trace.
        var spanCount = scoped
            ? "COUNT(DISTINCT CASE WHEN fs.service_name = @service THEN fs.span_id END)"
            : "COUNT(DISTINCT fs.span_id)";
        var aggSql = $"""
            SELECT fs.trace_id AS TraceId, {spanCount} AS SpanCount,
                   MIN(fs.start_time_unix_nano) AS MinStart, MAX(fs.end_time_unix_nano) AS MaxEnd
            FROM spans fs
            WHERE fs.tenant_id = @tenantId AND {inList}{BoundsClause("fs", bounds, parameters)}
            GROUP BY fs.trace_id
            """;
        var aggRows = (await conn.QueryAsync<TraceAggRow>(new CommandDefinition(
            aggSql, parameters, cancellationToken: ct))).ToDictionary(r => r.TraceId);

        var items = new List<TraceInfo>(anchors.Count);
        foreach (var a in anchors)
        {
            aggRows.TryGetValue(a.TraceId, out var agg);
            var minStart = agg?.MinStart ?? a.AnchorStart;
            var maxEnd = agg?.MaxEnd ?? a.AnchorEnd;
            items.Add(new TraceInfo
            {
                TraceIdHex = a.TraceId,
                SpanCount = agg?.SpanCount ?? 1,
                TraceStartTime = TimeConversion.UnixNanoToDateTime(minStart),
                TraceEndTime = TimeConversion.UnixNanoToDateTime(maxEnd),
                TraceDuration = TimeSpan.FromTicks((a.AnchorEnd - a.AnchorStart) / 100),
                ServiceName = string.IsNullOrEmpty(a.ServiceName) ? null : a.ServiceName,
                RootOperationName = a.RootName,
                AnchorKind = a.AnchorKind,
                HasErrors = a.HasErrorInt != 0,
                DisplaySpanIdHex = a.AnchorSpanId,
            });
        }
        return items;
    }

    // =========================================================================
    // EXPORT
    // =========================================================================

    private const int ExportChunkSize = 1000;

    /// <summary>
    /// Streams one <see cref="TraceInfo"/> row per matching trace, oldest first, with no row cap.
    /// Reuses <see cref="BuildAnchorFilterClauses"/> -- the same filter compilation the summary and
    /// page endpoints use -- so export can never see a different population than the list.
    ///
    /// The anchors are derived ONCE and streamed through a data reader (ordered by anchor start);
    /// every <see cref="ExportChunkSize"/> anchors a second connection fetches that chunk's exact span
    /// counts and bounds. Re-running the anchor query per chunk with a keyset cursor would repeat the
    /// whole window scan for every chunk. Memory stays bounded to one chunk; the cancellation token
    /// reaches the reader's command, so a client disconnect cancels the database work too.
    /// </summary>
    public async IAsyncEnumerable<TraceInfo> ExportTracesAsync(
        TraceExportQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var parsed = SearchQueryParser.Parse(query.Search);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildAnchorFilterClauses(
            query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        var sql = $"""
            SELECT {AnchorSelectColumns}
            FROM {AnchorsSql(!string.IsNullOrEmpty(query.Service), pinAsOf: false)} a
            WHERE {where}
            ORDER BY a.anchor_start ASC, a.anchor_span_pk ASC
            """;

        await using var readConn = await OpenConnectionAsync(cancellationToken);
        await using var lookupConn = await OpenConnectionAsync(cancellationToken);
        await using var reader = await readConn.ExecuteReaderAsync(new CommandDefinition(
            sql, parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<AnchorRow>();

        var chunk = new List<AnchorRow>(ExportChunkSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            chunk.Add(parse(reader));
            if (chunk.Count < ExportChunkSize) continue;

            foreach (var info in await LoadPageTraceInfosAsync(lookupConn, chunk, query.Service, cancellationToken))
                yield return info;
            chunk.Clear();
        }

        if (chunk.Count > 0)
            foreach (var info in await LoadPageTraceInfosAsync(lookupConn, chunk, query.Service, cancellationToken))
                yield return info;
    }

    // =========================================================================
    // SAMPLES -- dashboard widgets
    // =========================================================================

    public async Task<List<TraceInfo>> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Clamp(query.Limit, 1, 50);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);
        parameters.Add("anchorFrom", startNano - _anchorLookbackNanos);
        parameters.Add("limit", limit);
        parameters.Add("offset", 0);

        // Both widgets read the unpinned, unscoped anchors of the window: the newest traces with an
        // error span, and the slowest anchors (by the anchor's own duration, above a 500 ms floor).
        string filter, order;
        if (query.Kind == "errors")
        {
            filter = "a.has_error = 1";
            order = "a.anchor_start DESC, a.anchor_span_pk DESC";
        }
        else // "slowest"
        {
            filter = "(a.anchor_end - a.anchor_start) > 500000000";
            order = "(a.anchor_end - a.anchor_start) DESC, a.anchor_span_pk DESC";
        }

        var sql = $"""
            SELECT {AnchorSelectColumns}
            FROM {AnchorsSql(hasService: false, pinAsOf: false)} a
            WHERE {filter}
            ORDER BY {order}
            {PagingClause}
            """;
        var rows = (await conn.QueryAsync<AnchorRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();
        return await LoadPageTraceInfosAsync(conn, rows, null, cancellationToken);
    }

    // =========================================================================
    // SHARED FILTER COMPILATION / ANCHOR FETCH
    // =========================================================================

    /// <summary>
    /// Builds the WHERE clauses shared by the summary, listTotal, page, export and samples queries,
    /// all scoped to the <see cref="AnchorsSql"/> alias <c>a</c>, plus the parameters the anchors
    /// derived table and the clauses reference (<c>@tenantId</c>, <c>@start</c>, <c>@end</c>,
    /// <c>@anchorFrom</c>, <c>@service</c>). The time/service/pin scoping itself lives inside the
    /// anchors derived table; what is left here is what applies to an anchor row:
    /// <list type="bullet">
    /// <item>operation filter = the anchor span's own name (decision 15), no per-trace subquery;</item>
    /// <item><c>errors</c> mode = the in-scope error flag (decision 11);</item>
    /// <item><c>slow</c> mode = the anchor's own duration (decision 11);</item>
    /// <item>search = ANY span in the whole trace matches, regardless of the service filter (decision
    /// 16), as an <c>EXISTS</c> through <c>(trace_id, span_id)</c> per candidate anchor, within the
    /// search window.</item>
    /// </list>
    /// </summary>
    private (List<string> Clauses, DynamicParameters Parameters) BuildAnchorFilterClauses(
        string mode, string? service, string? operation, double? minDurationMs, double? maxDurationMs,
        ParsedSearchQuery parsed, long startNano, long endNano)
    {
        var clauses = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);
        parameters.Add("anchorFrom", startNano - _anchorLookbackNanos);

        if (!string.IsNullOrEmpty(service))
            parameters.Add("service", service);

        const string innerTime = " AND s2.tenant_id = @tenantId AND s2.start_time_unix_nano >= @anchorFrom AND s2.start_time_unix_nano <= @end";

        if (!string.IsNullOrEmpty(operation))
        {
            clauses.Add("a.root_name = @operation");
            parameters.Add("operation", operation);
        }

        if (mode == "errors")
        {
            clauses.Add("a.has_error = 1");
        }
        else if (mode == "slow")
        {
            var minNano = (long)((minDurationMs ?? 500) * 1_000_000);
            clauses.Add("(a.anchor_end - a.anchor_start) >= @minDurationNano");
            parameters.Add("minDurationNano", minNano);
            if (maxDurationMs.HasValue)
            {
                clauses.Add("(a.anchor_end - a.anchor_start) <= @maxDurationNano");
                parameters.Add("maxDurationNano", (long)(maxDurationMs.Value * 1_000_000));
            }
        }

        if (parsed.IsTraceIdSearch)
        {
            clauses.Add("a.trace_id = @traceIdSearch");
            parameters.Add("traceIdSearch", IdParam(parsed.TraceId, 32));
        }
        else if (parsed.Terms.Count > 0)
        {
            var i = 0;
            foreach (var term in parsed.Terms)
            {
                string innerPredicate;
                if (term.IsAttributeFilter)
                {
                    var keyParam = $"searchKey{i}";
                    var valueParam = $"searchVal{i}";
                    parameters.Add(keyParam, AttributeKeyParamValue(term.Key ?? ""));
                    parameters.Add(valueParam, term.Value ?? "");
                    // Attribute filters match against either the span's own attributes or its
                    // resource's (a common case: filtering by a resource attribute like a k8s pod label).
                    var spanPred = AttributePredicate("s2.attributes_json", $"@{keyParam}", $"@{valueParam}", term.Negate);
                    var resPred = AttributePredicate("rs2.attributes_json", $"@{keyParam}", $"@{valueParam}", term.Negate);
                    innerPredicate = $"({spanPred} OR {resPred})";
                }
                else
                {
                    var valueParam = $"searchText{i}";
                    parameters.Add(valueParam, $"%{EscapeLike(term.FreeText ?? "")}%");
                    // Free text matches span name or status_message.
                    var pred = $"({FreeTextPredicate("s2.name", $"@{valueParam}")} OR {FreeTextPredicate("s2.status_message", $"@{valueParam}")})";
                    innerPredicate = term.Negate ? $"NOT ({pred})" : pred;
                }
                clauses.Add(SpanLevelMatchPredicateWithResource("a.trace_id", innerTime, innerPredicate));
                i++;
            }
        }

        return (clauses, parameters);
    }

    /// <summary>
    /// <see cref="DapperReadRepository.SpanLevelMatchPredicate"/> joined to the matching span's own
    /// resource (aliased <c>rs2</c>), for a search term whose <c>innerPredicate</c> references a
    /// resource-attribute check as well as a span-attribute one. The relational providers use a
    /// correlated <c>EXISTS</c> with an extra join; ClickHouse overrides with its uncorrelated form.
    /// </summary>
    protected virtual string SpanLevelMatchPredicateWithResource(string traceIdColumn, string innerTimeClause, string innerPredicate)
        => $"EXISTS (SELECT 1 FROM spans s2 JOIN resources rs2 ON rs2.id = s2.resource_id WHERE s2.trace_id = {traceIdColumn}{innerTimeClause} AND {innerPredicate})";

    /// <summary>
    /// Fetches the anchor rows the summary is computed from: inbound anchors only (kind
    /// <c>SERVER</c>/<c>CONSUMER</c>, decision 12), unpinned, bounded by the window's anchors rather
    /// than by spans.
    /// </summary>
    private async Task<List<AnchorSummaryRow>> FetchSummaryAnchorsAsync(
        System.Data.Common.DbConnection conn, string mode, string? service, string? operation,
        double? minDurationMs, double? maxDurationMs, ParsedSearchQuery parsed,
        long startNano, long endNano, int? commandTimeoutSeconds, CancellationToken ct)
    {
        var (clauses, parameters) = BuildAnchorFilterClauses(mode, service, operation, minDurationMs, maxDurationMs, parsed, startNano, endNano);
        clauses.Add("a.anchor_kind IN ('SERVER', 'CONSUMER')");
        var where = string.Join(" AND ", clauses);

        var sql = $"""
            SELECT a.trace_id AS TraceId, a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd,
                   a.service_name AS ServiceName, a.has_error AS HasErrorInt
            FROM {AnchorsSql(!string.IsNullOrEmpty(service), pinAsOf: false)} a
            WHERE {where}
            """;

        var rows = await conn.QueryAsync<AnchorSummaryRow>(new CommandDefinition(
            sql, parameters, commandTimeout: commandTimeoutSeconds, cancellationToken: ct));
        return rows.ToList();
    }

    // =========================================================================
    // ANALYSIS READS
    // =========================================================================

    public async Task<List<ServiceDependency>> GetServiceDependenciesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        // A child span's parent is found through (trace_id, span_id); the child side is a
        // (tenant_id, start_time) range. Service names come straight off the span rows.
        var sql = """
            SELECT
                parent.service_name AS ParentService,
                child.service_name  AS ChildService,
                child.kind          AS Kind,
                child.status_code   AS StatusCode,
                (child.end_time_unix_nano - child.start_time_unix_nano) AS DurationNano
            FROM spans child
            JOIN spans parent ON child.parent_span_id = parent.span_id AND child.trace_id = parent.trace_id
            WHERE child.tenant_id = @tenantId AND parent.tenant_id = @tenantId
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
                x.ParentService,
                x.ChildService,
                Kind = Enum.Parse<SpanKind>(x.Kind),
                x.StatusCode,
                x.DurationNano
            })
            .Where(x => !string.IsNullOrEmpty(x.ParentService) && !string.IsNullOrEmpty(x.ChildService) && x.ParentService != x.ChildService)
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

        var (where, p) = BuildServiceTimeFilter(serviceName, startTime, endTime);
        var sql = $"""
            SELECT s.name AS Name
            FROM spans s
            WHERE {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<NameRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        return rows.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<Dictionary<string, double>> GetAverageLatenciesAsync(string serviceName, DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildServiceTimeFilter(serviceName, startTime, endTime);
        var sql = $"""
            SELECT s.name AS Name, s.start_time_unix_nano AS StartTimeUnixNano, s.end_time_unix_nano AS EndTimeUnixNano
            FROM spans s
            WHERE {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LatencyRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        return rows.GroupBy(s => s.Name)
            .ToDictionary(g => g.Key, g => g.Average(s => s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0);
    }

    public async Task<List<OperationStats>> GetOperationStatsAsync(string serviceName, DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        var (where, p) = BuildServiceTimeFilter(serviceName, startTime, endTime);
        var sql = $"""
            SELECT s.name AS Name, s.start_time_unix_nano AS StartTimeUnixNano, s.end_time_unix_nano AS EndTimeUnixNano, s.status_code AS StatusCode
            FROM spans s
            WHERE {where}
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<StatsRow>(new CommandDefinition(sql, p, cancellationToken: cancellationToken));

        var windowSeconds = Math.Max((endTime - startTime).TotalSeconds, 1);

        return rows
            .Select(s => new
            {
                s.Name,
                s.StatusCode,
                DurationMs = (s.EndTimeUnixNano - s.StartTimeUnixNano) / 1_000_000.0
            })
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

    /// <summary>Tenant + service + optional time bounds on <c>spans s</c>, through <c>idx_spans_tenant_service_time</c>.</summary>
    private (string where, DynamicParameters parameters) BuildServiceTimeFilter(string serviceName, DateTime? startTime, DateTime? endTime)
    {
        var clauses = new List<string> { "s.tenant_id = @tenantId", "s.service_name = @service" };
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("service", serviceName);
        if (startTime.HasValue)
        {
            clauses.Add("s.start_time_unix_nano >= @start");
            parameters.Add("start", TimeConversion.DateTimeToUnixNano(startTime.Value));
        }
        if (endTime.HasValue)
        {
            clauses.Add("s.end_time_unix_nano <= @end");
            parameters.Add("end", TimeConversion.DateTimeToUnixNano(endTime.Value));
        }
        return (string.Join(" AND ", clauses), parameters);
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

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class AnchorSummaryRow
    {
        public string TraceId { get; set; } = null!;
        public long AnchorStart { get; set; }
        public long AnchorEnd { get; set; }
        public string? ServiceName { get; set; }
        public int HasErrorInt { get; set; }
    }

    private sealed class AnchorRow
    {
        public string TraceId { get; set; } = null!;
        public long AnchorSpanPk { get; set; }
        public string AnchorSpanId { get; set; } = null!;
        public string? ServiceName { get; set; }
        public string RootName { get; set; } = null!;
        public string AnchorKind { get; set; } = "UNSPECIFIED";
        public long AnchorStart { get; set; }
        public long AnchorEnd { get; set; }
        public int HasErrorInt { get; set; }
    }

    private sealed class TraceAggRow
    {
        public string TraceId { get; set; } = null!;
        public int SpanCount { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
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
        public string? ParentService { get; set; }
        public string? ChildService { get; set; }
        public string Kind { get; set; } = null!;
        public string StatusCode { get; set; } = null!;
        public long DurationNano { get; set; }
    }

    private sealed class NameRow
    {
        public string Name { get; set; } = null!;
    }

    private sealed class LatencyRow
    {
        public string Name { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
    }

    private sealed class StatsRow
    {
        public string Name { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
    }
}
