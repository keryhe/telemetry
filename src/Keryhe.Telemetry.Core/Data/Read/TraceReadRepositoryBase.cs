using System.Text;
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

    private readonly long _pageSliceNanos = QueryOptions.DefaultPageSliceSeconds * NanosPerSecond;
    private readonly int _pageSliceGrowth = QueryOptions.DefaultPageSliceGrowth;
    private const long NanosPerSecond = 1_000_000_000L;

    /// <summary>How far either side of a trace time hint a hinted trace read looks.</summary>
    protected long TraceHintMarginNanos { get; } = QueryOptions.DefaultTraceHintMarginMinutes * NanosPerMinute;

    /// <summary>False ignores every trace time hint (<c>Telemetry:Query:TraceHintEnabled</c>): a switch for a deployment where bounding the read does not pay.</summary>
    private readonly bool _traceHintEnabled = true;

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
        var sliceSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:PageSliceSeconds"], out var slice) && slice >= 1
            ? slice
            : QueryOptions.DefaultPageSliceSeconds;
        _pageSliceNanos = sliceSeconds * NanosPerSecond;
        _pageSliceGrowth = int.TryParse(configuration[$"{QueryOptions.SectionName}:PageSliceGrowth"], out var growth) && growth >= 2
            ? growth
            : QueryOptions.DefaultPageSliceGrowth;
        _traceHintEnabled = !bool.TryParse(configuration[$"{QueryOptions.SectionName}:TraceHintEnabled"], out var hintEnabled) || hintEnabled;
        TraceHintMarginNanos = (int.TryParse(configuration[$"{QueryOptions.SectionName}:TraceHintMarginMinutes"], out var margin) && margin >= 0
            ? margin : QueryOptions.DefaultTraceHintMarginMinutes) * NanosPerMinute;
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

    /// <summary>
    /// Start-time bounds for a by-trace read derived from the caller's own hint (the trace's start and end), without asking the
    /// database (null: this provider does not use a hint). ClickHouse overrides it so it can skip the <c>trace_index</c> round trip.
    /// </summary>
    protected virtual (long Min, long Max)? HintedTraceTimeBounds(long startHintNano, long endHintNano) => null;

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

    public Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
        => GetTraceByIdAsync(traceIdHex, null, cancellationToken);

    public async Task<List<SpanModel>> GetTraceByIdAsync(string traceIdHex, TraceTimeHint? hint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var hinted = _traceHintEnabled && hint is { } h
            ? HintedTraceTimeBounds(TimeConversion.DateTimeToUnixNano(h.Start), TimeConversion.DateTimeToUnixNano(h.End)) : null;
        var spans = await LoadTraceSpansAsync(conn, traceIdHex,
            hinted ?? await ResolveTraceTimeBoundsAsync(conn, [traceIdHex], cancellationToken), cancellationToken);

        // A hint that finds nothing (a wrong start, or a trace whose first span is not the one the caller knew about) must not
        // turn an existing trace into a 404: read it again without the hint's bounds.
        if (spans.Count == 0 && hinted is not null)
            spans = await LoadTraceSpansAsync(conn, traceIdHex, await ResolveTraceTimeBoundsAsync(conn, [traceIdHex], cancellationToken), cancellationToken);

        // A re-delivered span batch is stored again (no unique key, schema-simplification decision 7);
        // the detail view shows each span once, keeping the first stored copy.
        return DistinctSpans(spans);
    }

    private async Task<List<SpanModel>> LoadTraceSpansAsync(
        System.Data.Common.DbConnection conn, string traceIdHex, (long Min, long Max)? bounds, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("traceId", IdParam(traceIdHex, 32));
        return await LoadFullSpansAsync(conn,
            "s.trace_id = @traceId" + BoundsClause("s", bounds, parameters), "ORDER BY s.start_time_unix_nano",
            parameters, ct);
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
        // A span's events and links come back as JSON columns on the span row itself. The resource and scope a span refers to are
        // shared by most spans of a trace, so their (large) attribute JSON is read ONCE per distinct resource/scope, not once per span.
        const string spanColumns = """
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
                s.resource_id               AS ResourceId,
                s.scope_id                  AS ScopeId
            """;

        if (!JoinsReferenceRows)
        {
            // ClickHouse: no join (its reference tables are ReplacingMergeTree, see ReferenceRowsSql); the resources and scopes are
            // read by id in their own queries.
            var plainSql = $"""
                SELECT
                {spanColumns}
                FROM spans s
                WHERE s.tenant_id = @tenantId AND {whereClause}
                {orderClause}
                """;
            var plainRows = (await conn.QueryAsync<FullSpanRow>(new CommandDefinition(plainSql, parameters, cancellationToken: ct))).ToList();
            if (plainRows.Count == 0) return new List<SpanModel>();
            var resourcesById = await LoadResourcesAsync(conn, plainRows.Select(r => r.ResourceId).Distinct().ToList(), ct);
            var scopesById = await LoadScopesAsync(conn, plainRows.Select(r => r.ScopeId).Distinct().ToList(), ct);
            return plainRows.Select(r => MapSpan(r, resourcesById, scopesById)).ToList();
        }

        // One statement, the same single join the read always used. Window functions to send each resource's attributes once
        // (ROW_NUMBER per resource and scope) and batched sub-selects were both measured: the windows sort every row (20,000 spans:
        // 551 ms on SQL Server, 392 on MySQL against about 190 for the plain join) and the sub-selects repeat the trace predicate (a
        // hypertable probes every chunk three times; SQL Server's plan degraded under concurrency, 32 -> 180 ms p95). So the join
        // stays plain, and the repeated attribute JSON is parsed once per distinct resource and scope below, not once per span.
        var sql = $"""
            SELECT
                {spanColumns},
                r.schema_url      AS ResourceSchemaUrl,
                r.attributes_json AS ResourceAttributesJson,
                sc.name           AS ScopeName,
                sc.version        AS ScopeVersion,
                sc.schema_url     AS ScopeSchemaUrl,
                sc.attributes_json AS ScopeAttributesJson
            FROM spans s
            JOIN {ResourcesTable} r ON s.resource_id = r.id
            JOIN {ScopesTable} sc ON s.scope_id = sc.id
            WHERE s.tenant_id = @tenantId AND {whereClause}
            {orderClause}
            """;
        var rows = (await conn.QueryAsync<FullSpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return new List<SpanModel>();

        var resources = new Dictionary<long, ResourceModel>();
        var scopes = new Dictionary<long, InstrumentationScopeModel>();
        foreach (var r in rows)
        {
            if (!resources.ContainsKey(r.ResourceId))
                resources[r.ResourceId] = new ResourceModel
                {
                    SchemaUrl = r.ResourceSchemaUrl,
                    Attributes = DeserializeAttributes(r.ResourceAttributesJson) ?? new Dictionary<string, object>()
                };
            if (!scopes.ContainsKey(r.ScopeId))
                scopes[r.ScopeId] = new InstrumentationScopeModel
                {
                    Name = r.ScopeName ?? "",
                    Version = r.ScopeVersion,
                    SchemaUrl = r.ScopeSchemaUrl,
                    Attributes = DeserializeAttributes(r.ScopeAttributesJson) ?? new Dictionary<string, object>()
                };
        }
        return rows.Select(r => MapSpan(r, resources, scopes)).ToList();
    }

    /// <summary>
    /// Whether trace detail reads the resource and scope rows with a join in the span query (every relational provider). ClickHouse
    /// reads them in their own queries by id (<see cref="ReferenceRowsSql"/>).
    /// </summary>
    protected virtual bool JoinsReferenceRows => true;

    /// <summary>
    /// The reference-table rows for <paramref name="ids"/>: <c>id</c> plus <paramref name="columns"/>. ClickHouse overrides it
    /// because its reference tables are <c>ReplacingMergeTree</c> (a not-yet-merged duplicate is collapsed with
    /// <c>LIMIT 1 BY id</c>, which must filter on the raw table, not a subquery of it).
    /// </summary>
    protected virtual string ReferenceRowsSql(bool resources, string columns, string idPredicate)
        => $"SELECT r.id AS Id, {columns} FROM {(resources ? ResourcesTable : ScopesTable)} r WHERE {idPredicate}";

    private async Task<Dictionary<long, ResourceModel>> LoadResourcesAsync(System.Data.Common.DbConnection conn, List<long> ids, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var sql = ReferenceRowsSql(resources: true, "r.schema_url AS SchemaUrl, r.attributes_json AS AttributesJson", LongInPredicate("r.id", "resId", ids, parameters));
        return ToResourceMap(await conn.QueryAsync<ReferenceRow>(new CommandDefinition(sql, parameters, cancellationToken: ct)));
    }

    // ToDictionaryFirst: a ClickHouse reference row can exist twice until a merge.
    private static Dictionary<long, ResourceModel> ToResourceMap(IEnumerable<ReferenceRow> rows) => ToDictionaryFirst(rows, r => r.Id, r => new ResourceModel
    {
        SchemaUrl = r.SchemaUrl,
        Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>()
    });

    private async Task<Dictionary<long, InstrumentationScopeModel>> LoadScopesAsync(System.Data.Common.DbConnection conn, List<long> ids, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var sql = ReferenceRowsSql(resources: false, "r.name AS Name, r.version AS Version, r.schema_url AS SchemaUrl, r.attributes_json AS AttributesJson",
            LongInPredicate("r.id", "scopeId", ids, parameters));
        return ToScopeMap(await conn.QueryAsync<ReferenceRow>(new CommandDefinition(sql, parameters, cancellationToken: ct)));
    }

    private static Dictionary<long, InstrumentationScopeModel> ToScopeMap(IEnumerable<ReferenceRow> rows) => ToDictionaryFirst(rows, r => r.Id, r => new InstrumentationScopeModel
    {
        Name = r.Name ?? "",
        Version = r.Version,
        SchemaUrl = r.SchemaUrl,
        Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>()
    });

    private static string LongInPredicate(string column, string prefix, IReadOnlyList<long> ids, DynamicParameters parameters)
    {
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            parameters.Add($"{prefix}{i}", ids[i]);
            names.Add($"@{prefix}{i}");
        }
        return $"{column} IN ({string.Join(",", names)})";
    }

    private static SpanModel MapSpan(FullSpanRow r, Dictionary<long, ResourceModel> resources, Dictionary<long, InstrumentationScopeModel> scopes) => new()
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
        // The same instance for every span of a resource (or scope), so a caller can tell they are shared.
        Resource = resources.TryGetValue(r.ResourceId, out var resource) ? resource : new ResourceModel { Attributes = new Dictionary<string, object>() },
        InstrumentationScope = scopes.TryGetValue(r.ScopeId, out var scope) ? scope : new InstrumentationScopeModel { Name = "", Attributes = new Dictionary<string, object>() }
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
    /// The anchors derived table, for a whole window: one row per trace, being the trace's earliest span in scope
    /// (ranked by <c>(start, id)</c>), with the in-scope error flag. Parameters: <c>@tenantId</c>, <c>@anchorFrom</c>
    /// (<c>@start</c> minus the look-back margin, <see cref="QueryOptions.AnchorLookbackMinutes"/>), <c>@start</c>,
    /// <c>@end</c>, plus <c>@service</c> when <paramref name="hasService"/> and <c>@asOf</c> when
    /// <paramref name="pinAsOf"/> (spans created after the pin are excluded BEFORE ranking, so a root that arrives late is
    /// not the anchor within a pinned query).
    ///
    /// Shaped as a hash aggregate joined back to the anchor span, not a <c>ROW_NUMBER()</c> window: one pass groups each
    /// trace's spans in <c>[@anchorFrom, @end]</c> for their earliest start and error flag (dropping traces that start before
    /// <c>@start</c>), then the earliest span is read back by <c>(trace_id, span_id)</c> and a trace with two spans starting
    /// at the same instant keeps the lowest <c>id</c>. That avoids sorting every span by trace id, which is what made the
    /// window function 2x (PostgreSQL, SQL Server) to 5x (MySQL) slower in the lab benchmark. With
    /// <paramref name="errorsOnly"/> the group is restricted to traces with an ERROR span in scope, found through the errors
    /// index, so errors mode never ranks the traces that cannot qualify. ClickHouse overrides this with its own
    /// <c>GROUP BY trace_id</c> form and ignores <paramref name="errorsOnly"/> (its callers still filter on
    /// <c>has_error</c>).
    /// </summary>
    protected virtual string AnchorsSql(bool hasService, bool pinAsOf, bool errorsOnly = false)
    {
        // A selected service narrows the scan to its own index range (<c>idx_spans_tenant_service_time</c>), small enough
        // that the window form below beat the hash aggregate in the lab benchmark (a service-scoped 6h summary: 203 vs 299 ms
        // on PostgreSQL, 599 vs 796 ms on MySQL); errors mode has no such narrowing and always uses the aggregate.
        if (hasService && !errorsOnly) return WindowAnchorsSql(pinAsOf);

        var service = hasService ? " AND service_name = @service" : "";
        var pin = pinAsOf ? " AND created_at <= @asOf" : "";
        var joinService = hasService ? " AND s.service_name = @service" : "";
        var joinPin = pinAsOf ? " AND s.created_at <= @asOf" : "";
        var errorTraces = errorsOnly
            ? $" AND trace_id IN (SELECT e.trace_id FROM spans e WHERE e.tenant_id = @tenantId AND e.status_code = 'ERROR' AND e.start_time_unix_nano >= @anchorFrom AND e.start_time_unix_nano <= @end{(hasService ? " AND e.service_name = @service" : "")}{(pinAsOf ? " AND e.created_at <= @asOf" : "")})"
            : "";
        return $"""
            (
                SELECT x.trace_id, x.anchor_span_pk, x.anchor_span_id, x.service_name, x.root_name, x.anchor_kind,
                       x.anchor_start, x.anchor_end, x.anchor_created_at, x.has_error
                FROM (
                    SELECT s.trace_id AS trace_id, s.id AS anchor_span_pk, s.span_id AS anchor_span_id,
                           s.service_name AS service_name, s.name AS root_name, s.kind AS anchor_kind,
                           s.start_time_unix_nano AS anchor_start, s.end_time_unix_nano AS anchor_end,
                           s.created_at AS anchor_created_at, g.has_error AS has_error,
                           ROW_NUMBER() OVER (PARTITION BY s.trace_id ORDER BY s.id) AS rn
                    FROM (
                        SELECT trace_id, MIN(start_time_unix_nano) AS min_start,
                               MAX(CASE WHEN status_code = 'ERROR' THEN 1 ELSE 0 END) AS has_error
                        FROM spans
                        WHERE tenant_id = @tenantId
                          AND start_time_unix_nano >= @anchorFrom AND start_time_unix_nano <= @end{service}{pin}{errorTraces}
                        GROUP BY trace_id
                        HAVING MIN(start_time_unix_nano) >= @start
                    ) g
                    JOIN spans s ON s.trace_id = g.trace_id AND s.start_time_unix_nano = g.min_start AND s.tenant_id = @tenantId{joinService}{joinPin}
                ) x
                WHERE x.rn = 1
            )
            """;
    }

    /// <summary>The window-function form of <see cref="AnchorsSql"/> for a selected service: rank each trace's in-scope spans and keep the first.</summary>
    private static string WindowAnchorsSql(bool pinAsOf)
    {
        var pin = pinAsOf ? " AND s.created_at <= @asOf" : "";
        return $"""
            (
                SELECT x.trace_id, x.anchor_span_pk, x.anchor_span_id, x.service_name, x.root_name, x.anchor_kind,
                       x.anchor_start, x.anchor_end, x.anchor_created_at, x.has_error
                FROM (
                    SELECT s.trace_id AS trace_id, s.id AS anchor_span_pk, s.span_id AS anchor_span_id,
                           s.service_name AS service_name, s.name AS root_name, s.kind AS anchor_kind,
                           s.start_time_unix_nano AS anchor_start, s.end_time_unix_nano AS anchor_end,
                           s.created_at AS anchor_created_at,
                           ROW_NUMBER() OVER (PARTITION BY s.trace_id ORDER BY s.start_time_unix_nano, s.id) AS rn,
                           MAX(CASE WHEN s.status_code = 'ERROR' THEN 1 ELSE 0 END) OVER (PARTITION BY s.trace_id) AS has_error
                    FROM spans s
                    WHERE s.tenant_id = @tenantId
                      AND s.start_time_unix_nano >= @anchorFrom AND s.start_time_unix_nano <= @end AND s.service_name = @service{pin}
                ) x
                WHERE x.rn = 1 AND x.anchor_start >= @start
            )
            """;
    }

    /// <summary>
    /// Whether this provider can verify an anchor with a correlated <c>NOT EXISTS</c> seek, which
    /// <see cref="SeekAnchorsSql"/> needs. ClickHouse has no <c>(trace_id)</c> seek, so it keeps reading whole-window anchors.
    /// </summary>
    protected virtual bool SupportsSeekAnchors => true;

    /// <summary>
    /// The anchors whose start lies in <c>[@rangeFrom, @rangeTo)</c>, found by the anchor's own definition rather than by
    /// ranking a window: a span is its trace's anchor when no span of the same trace in scope starts earlier (by
    /// <c>(start, id)</c>) at or after <c>@anchorFrom</c>. Each candidate is checked with one <c>(trace_id, span_id)</c>
    /// seek, so the cost follows the number of candidate spans in the range -- not the window -- which is what lets a page
    /// read only the slice it needs (<see cref="QueryOptions.PageSliceSeconds"/>). <paramref name="candidatePredicate"/>
    /// is extra <c>AND ...</c> terms on the candidate span <c>s</c> (the anchor's own name or duration), evaluated before
    /// the seek. The error flag is not computed here (it is <c>0</c>); the page reads it for just its own rows.
    /// Parameters: <c>@tenantId</c>, <c>@anchorFrom</c>, <c>@rangeFrom</c>, <c>@rangeTo</c>, plus <c>@service</c> and
    /// <c>@asOf</c> as for <see cref="AnchorsSql"/>.
    /// </summary>
    protected virtual string SeekAnchorsSql(bool hasService, bool pinAsOf, string candidatePredicate = "")
    {
        var service = hasService ? " AND s.service_name = @service" : "";
        var pin = pinAsOf ? " AND s.created_at <= @asOf" : "";
        var earlierService = hasService ? " AND p.service_name = @service" : "";
        var earlierPin = pinAsOf ? " AND p.created_at <= @asOf" : "";
        return $"""
            (
                SELECT s.trace_id AS trace_id, s.id AS anchor_span_pk, s.span_id AS anchor_span_id,
                       s.service_name AS service_name, s.name AS root_name, s.kind AS anchor_kind,
                       s.start_time_unix_nano AS anchor_start, s.end_time_unix_nano AS anchor_end,
                       s.created_at AS anchor_created_at, 0 AS has_error
                FROM spans s
                WHERE s.tenant_id = @tenantId
                  AND s.start_time_unix_nano >= @rangeFrom AND s.start_time_unix_nano < @rangeTo{service}{pin}{candidatePredicate}
                  AND NOT EXISTS (
                      SELECT 1 FROM spans p
                      WHERE p.trace_id = s.trace_id AND p.tenant_id = @tenantId
                        AND p.start_time_unix_nano >= @anchorFrom{earlierService}{earlierPin}
                        AND (p.start_time_unix_nano < s.start_time_unix_nano
                             OR (p.start_time_unix_nano = s.start_time_unix_nano AND p.id < s.id)))
            )
            """;
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
        // Whole-window anchors: errors mode (restricted to traces with an ERROR span, found through the errors index).
        // Every other page reads a slice of seek-verified anchors instead.
        var errorsMode = query.Mode == "errors";
        var anchors = AnchorsSql(hasService, pinAsOf: true, errorsOnly: errorsMode);
        var useSlices = SupportsSeekAnchors && !errorsMode;

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
                rows = useSlices
                    ? await FetchSlicedAnchorPageAsync(conn, query, hasService, clauses, parameters, requestedSize + 1, descending: true,
                        rangeFrom: startNano, rangeTo: Math.Min(endNano + 1, cursor.K + 1), cancellationToken)
                    : await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;

            case "prev":
                forward = false;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("a.anchor_start", "a.anchor_span_pk", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = useSlices
                    ? await FetchSlicedAnchorPageAsync(conn, query, hasService, clauses, parameters, requestedSize + 1, descending: false,
                        rangeFrom: Math.Max(startNano, cursor.K), rangeTo: endNano + 1, cancellationToken)
                    : await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            default: // "first"
                forward = true;
                requestedSize = size;
                rows = useSlices
                    ? await FetchSlicedAnchorPageAsync(conn, query, hasService, clauses, parameters, requestedSize + 1, descending: true,
                        rangeFrom: startNano, rangeTo: endNano + 1, cancellationToken)
                    : await FetchAnchorPageAsync(conn, anchors, clauses, parameters, requestedSize, descending: true, cancellationToken);
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
            // prev
            nextCursor = displayRows.Count > 0 ? Encode(displayRows[^1], filterHash) : null;
            prevCursor = hasExtra && displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null;
        }

        // Exact span counts and whole-trace bounds for just this page's anchors (bounded by `size`). A sliced page did not
        // compute the error flag per anchor, so it is read here, for these traces only.
        var items = await LoadPageTraceInfosAsync(conn, displayRows, query.Service, cancellationToken,
            errorScope: useSlices ? new ErrorScope(startNano - _anchorLookbackNanos, endNano, asOf) : null);

        return new TracePageResult { Items = items, NextCursor = nextCursor, PrevCursor = prevCursor, AsOf = asOf };
    }

    private static string Encode(AnchorRow row, string filterHash) => KeysetCursor.Encode(row.AnchorStart, row.AnchorSpanPk, filterHash);

    private const string AnchorSelectColumns = """
        a.trace_id AS TraceId, a.anchor_span_pk AS AnchorSpanPk, a.anchor_span_id AS AnchorSpanId, a.service_name AS ServiceName,
        a.root_name AS RootName, a.anchor_kind AS AnchorKind, a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd,
        a.has_error AS HasErrorInt
        """;

    /// <summary>
    /// Fills one page from slices of seek-verified anchors (<see cref="SeekAnchorsSql"/>) instead of ranking the whole
    /// window. Descending (first and next pages) walks back from <paramref name="rangeTo"/>; ascending (the previous page)
    /// walks forward from <paramref name="rangeFrom"/>. Each step reads one slice of trace start times, applies the same
    /// filters and keyset predicate as a whole-window read, and the slices are disjoint, so their rows concatenate in the
    /// final order. A slice starts at <see cref="QueryOptions.PageSliceSeconds"/> and grows by
    /// <see cref="QueryOptions.PageSliceGrowth"/>; once the next slice would cover at least half of what is left, it takes
    /// all of it, which bounds a sparse filter (a rare search or operation) at about one extra partial scan. Slow mode
    /// filters on the anchor's own duration, which is selective by construction (a few percent of spans), so it reads the
    /// whole range in one pass.
    /// </summary>
    private async Task<List<AnchorRow>> FetchSlicedAnchorPageAsync(
        System.Data.Common.DbConnection conn, TraceQuery query, bool hasService, List<string> clauses, DynamicParameters parameters,
        int need, bool descending, long rangeFrom, long rangeTo, CancellationToken ct)
    {
        var candidate = new StringBuilder();
        var selective = false;
        // The anchor's own name narrows the candidates before the seek, but still reads in slices: on SQL Server a
        // single whole-range pass ordered every matching candidate's seek before the top-N cut (6 ms to 600 ms), where a
        // slice only ever verifies the few hundred candidates it holds.
        if (!string.IsNullOrEmpty(query.Operation))
            candidate.Append(" AND s.name = @operation");
        if (query.Mode == "slow")
        {
            candidate.Append(" AND (s.end_time_unix_nano - s.start_time_unix_nano) >= @minDurationNano");
            if (query.MaxDurationMs.HasValue)
                candidate.Append(" AND (s.end_time_unix_nano - s.start_time_unix_nano) <= @maxDurationNano");
            selective = true;
        }

        var anchors = SeekAnchorsSql(hasService, pinAsOf: true, candidate.ToString());
        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses);
        var order = descending ? "DESC" : "ASC";
        var rows = new List<AnchorRow>(need);
        var width = selective ? Math.Max(1, rangeTo - rangeFrom) : _pageSliceNanos;
        var lo = rangeFrom;
        var hi = rangeTo;

        while (rows.Count < need && lo < hi)
        {
            long sliceFrom, sliceTo;
            if (descending) { sliceTo = hi; sliceFrom = Math.Max(lo, hi - width); }
            else { sliceFrom = lo; sliceTo = Math.Min(hi, lo + width); }

            var sql = $"""
                SELECT {AnchorSelectColumns}
                FROM {anchors} a
                WHERE {where}
                ORDER BY a.anchor_start {order}, a.anchor_span_pk {order}
                {PagingClause}
                """;
            var sliceParameters = new DynamicParameters(parameters);
            sliceParameters.Add("rangeFrom", sliceFrom);
            sliceParameters.Add("rangeTo", sliceTo);
            sliceParameters.Add("limit", need - rows.Count);
            sliceParameters.Add("offset", 0);
            rows.AddRange(await conn.QueryAsync<AnchorRow>(new CommandDefinition(sql, sliceParameters, cancellationToken: ct)));

            if (descending) hi = sliceFrom; else lo = sliceTo;
            var remaining = hi - lo;
            width = width > long.MaxValue / _pageSliceGrowth ? remaining : width * _pageSliceGrowth;
            if (width >= remaining / 2) width = Math.Max(1, remaining);
        }
        return rows;
    }

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

    /// <summary>
    /// Shapes the page's anchors into <see cref="TraceInfo"/> rows. Duration, error flag, service,
    /// operation and kind come straight from the anchor (decisions 10-11); the follow-up query supplies
    /// only the exact span count (decisions 14, 18) and the whole trace's start/end for the detail link.
    /// </summary>
    private async Task<List<TraceInfo>> LoadPageTraceInfosAsync(
        System.Data.Common.DbConnection conn, List<AnchorRow> anchors, string? service, CancellationToken ct, ErrorScope? errorScope = null)
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
        // A sliced page carries no error flag on its anchors; it is "any span in scope has ERROR", the same scope the
        // whole-window anchors use (service, the look-back range, the pin), computed here for this page's traces only.
        var errorFlag = "0";
        if (errorScope is { } scope)
        {
            parameters.Add("errFrom", scope.From);
            parameters.Add("errTo", scope.To);
            if (scope.AsOf is { } errAsOf) parameters.Add("errAsOf", errAsOf);
            errorFlag = "MAX(CASE WHEN fs.status_code = 'ERROR' AND fs.start_time_unix_nano >= @errFrom AND fs.start_time_unix_nano <= @errTo"
                        + (scoped ? " AND fs.service_name = @service" : "") + (scope.AsOf is null ? "" : " AND fs.created_at <= @errAsOf") + " THEN 1 ELSE 0 END)";
        }
        var aggSql = $"""
            SELECT fs.trace_id AS TraceId, {spanCount} AS SpanCount,
                   MIN(fs.start_time_unix_nano) AS MinStart, MAX(fs.end_time_unix_nano) AS MaxEnd, {errorFlag} AS HasError
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
                HasErrors = errorScope is null ? a.HasErrorInt != 0 : (agg?.HasError ?? 0) != 0,
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
            FROM {AnchorsSql(!string.IsNullOrEmpty(query.Service), pinAsOf: false, errorsOnly: query.Mode == "errors")} a
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

    public async Task<TraceSamplesResult> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default)
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
        string anchors, filter, order;
        if (query.Kind == "errors")
        {
            // Only traces with an ERROR span can qualify, so the anchors are derived for just those (errors index).
            anchors = AnchorsSql(hasService: false, pinAsOf: false, errorsOnly: true);
            filter = "a.has_error = 1";
            order = "a.anchor_start DESC, a.anchor_span_pk DESC";
        }
        else // "slowest"
        {
            // The anchor must itself be over the floor, so candidates are the long spans of the window, each checked for
            // being its trace's earliest by one seek, instead of ranking every trace to find them.
            const string floor = "(s.end_time_unix_nano - s.start_time_unix_nano) > 500000000";
            if (SupportsSeekAnchors)
            {
                anchors = SeekAnchorsSql(hasService: false, pinAsOf: false, $" AND {floor}");
                parameters.Add("rangeFrom", startNano);
                parameters.Add("rangeTo", endNano + 1);
            }
            else
            {
                anchors = AnchorsSql(hasService: false, pinAsOf: false);
            }
            filter = "(a.anchor_end - a.anchor_start) > 500000000";
            order = "(a.anchor_end - a.anchor_start) DESC, a.anchor_span_pk DESC";
        }

        var sql = $"""
            SELECT {AnchorSelectColumns}
            FROM {anchors} a
            WHERE {filter}
            ORDER BY {order}
            {PagingClause}
            """;
        // Bounded like the summaries: this derives anchors over the whole window too, and without a budget it ran into the
        // driver's 30 s default and answered 500 under load (3.0.1 ramp). On timeout the follow-up below is
        // skipped, so nothing else runs on the aborted connection (see TimedQuery).
        var (rows, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => (await conn.QueryAsync<AnchorRow>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList(),
            _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || rows == null) return new TraceSamplesResult { TimedOut = true };

        var seekRows = query.Kind != "errors" && SupportsSeekAnchors;
        return new TraceSamplesResult
        {
            Items = await LoadPageTraceInfosAsync(conn, rows, null, cancellationToken,
                errorScope: seekRows ? new ErrorScope(startNano - _anchorLookbackNanos, endNano, null) : null)
        };
    }

    /// <summary>
    /// Exact count of inbound spans (kind SERVER/CONSUMER) of the active tenant that started in
    /// <c>[start, end)</c> and ran at least <paramref name="minDurationMs"/>: the slow-request alert's one
    /// raw statement (plans/summary-rollups.md). Through <c>(tenant_id, service_name, start)</c> /
    /// <c>(tenant_id, start)</c>, under the summary timeout; on timeout the count is unknown.
    /// </summary>
    public async Task<SlowRequestCount> CountSlowInboundSpansAsync(
        DateTime start, DateTime end, string? service, double minDurationMs, CancellationToken cancellationToken = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", TimeConversion.DateTimeToUnixNano(start));
        parameters.Add("end", TimeConversion.DateTimeToUnixNano(end));
        parameters.Add("minNanos", (long)Math.Ceiling(minDurationMs * 1_000_000.0));
        var serviceFilter = "";
        if (service != null)
        {
            parameters.Add("service", service);
            serviceFilter = " AND service_name = @service";
        }

        var sql = $"""
            SELECT COUNT(*) FROM spans
            WHERE tenant_id = @tenantId AND start_time_unix_nano >= @start AND start_time_unix_nano < @end
              AND kind IN ('SERVER', 'CONSUMER') AND (end_time_unix_nano - start_time_unix_nano) >= @minNanos{serviceFilter}
            """;

        var (count, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) =>
            {
                await using var conn = await OpenConnectionAsync(ct);
                return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct));
            },
            _summaryTimeoutSeconds, cancellationToken);
        return new SlowRequestCount(timedOut ? 0 : count, timedOut);
    }

    // =========================================================================
    // SHARED FILTER COMPILATION / ANCHOR FETCH
    // =========================================================================

    /// <summary>
    /// Builds the WHERE clauses shared by the page, export and samples queries,
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
        public int HasError { get; set; }
    }

    /// <summary>The scope the error flag of a sliced page is read in: the look-back range, the window end and the pin (null: unpinned).</summary>
    private readonly record struct ErrorScope(long From, long To, DateTime? AsOf);

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
        public long ResourceId { get; set; }
        public long ScopeId { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }

    /// <summary>A resource or scope row: the columns not used by one of the two stay null.</summary>
    private sealed class ReferenceRow
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? SchemaUrl { get; set; }
        public string? AttributesJson { get; set; }
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
