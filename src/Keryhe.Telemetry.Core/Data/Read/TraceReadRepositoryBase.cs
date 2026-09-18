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
    protected TraceReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

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
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
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
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                HasErrors = g.Any(s => s.StatusCode == "ERROR"),
                RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First(),
                InvolvesService = g.Any(s => MatchesService(s, serviceName))
            })
            // Matched at the trace level: filtering the spans first would strip the root span of
            // any trace whose entry point lives in another service, dropping the trace outright.
            .Where(t => t.InvolvesService)
            .OrderByDescending(t => t.MinStartTimeNano)
            .ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(),
            cancellationToken);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Take(limit)
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, serviceName, t.RootSpan))
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
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
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
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
            .ToList();
    }

    // =========================================================================
    // PAGED TRACE QUERY (server-side filter + offset/limit + total)
    // =========================================================================

    public async Task<PagedResult<TraceInfo>> QueryTracesAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);

        var ordered = await ComputeTraceInfosAsync(query, cancellationToken);

        return new PagedResult<TraceInfo>
        {
            Items = ordered.Skip(offset).Take(limit).ToList(),
            Total = ordered.Count
        };
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

        // slim: buckets are built from timestamps, durations and the error flag only.
        var traces = await ComputeTraceInfosAsync(ToTraceQuery(query), cancellationToken, slim: true);
        return BuildVolumeBuckets(traces, query);
    }

    public async Task<TraceOverview> GetTraceOverviewAsync(HistogramQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        // One scan, grouped two ways — not two scans. `/histogram` (above) stays untouched and
        // just as cheap for its other caller (the traces list page), which doesn't need
        // per-service stats and shouldn't pay for them.
        // slim: all three groupings below read only trace-level aggregates and the service name.
        var traces = await ComputeTraceInfosAsync(ToTraceQuery(query), cancellationToken, slim: true);
        var services = BuildServiceStats(traces, query.Start, query.End);
        return new TraceOverview
        {
            Buckets = BuildVolumeBuckets(traces, query),
            Services = services,
            Summary = BuildWindowSummary(traces, services.Count),
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
        Tags = query.Tags
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
    /// Fetches the raw spans matching the query's mode/service/time predicates, groups them into the
    /// same trace-level shape the legacy <c>Get*TracesAsync</c> methods produce, filters out non-root
    /// traces, and returns the full ordered list (no offset/limit applied — the caller pages it).
    /// </summary>
    /// <param name="slim">
    /// Opt out of fetching either attributes column — see <see cref="FetchRawSpansSlimAsync"/>.
    /// Only safe for callers that read nothing but the trace-level aggregates (counts, timings,
    /// error flag, service name); the returned <see cref="TraceInfo.RootSpanAttributes"/> will be
    /// null. Silently ignored when the query carries tag predicates, which need span attributes.
    /// </param>
    protected async Task<List<TraceInfo>> ComputeTraceInfosAsync(
        TraceQuery query, CancellationToken ct, bool slim = false)
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

        var clauses = new List<string> { "s.start_time_unix_nano >= @start", "s.start_time_unix_nano <= @end" };
        // Narrow by *trace*, not by span: selecting only ERROR spans would leave each group
        // without its (non-erroring) root span, breaking root detection and the trace-level
        // aggregates below. The subquery keeps the DB doing the narrowing while the outer
        // query still returns every span of each matching trace.
        if (isErrors) clauses.Add(ErrorTracePredicate(" AND s2.start_time_unix_nano >= @start AND s2.start_time_unix_nano <= @end"));
        var where = string.Join(" AND ", clauses);

        var spanParams = new { tenantId = TenantId, start = startNano, end = endNano };
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
            // Service filter: the trace involves this service (matched anywhere in the trace,
            // not only at its root). Kept in C# — the ResourceServiceNameExpr SQL hook is only
            // overridden on the *log* repositories, so a SQL predicate here would emit
            // Postgres-only JSON syntax on SqlServer/ClickHouse/MySql.
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

        // Order: explicit sort key when supplied, otherwise the mode default (slow → worst
        // duration first; all/errors → most-recent first).
        var asc = string.Equals(query.Dir, "asc", StringComparison.OrdinalIgnoreCase);
        groups = ((query.Sort?.ToLowerInvariant()) switch
        {
            "duration"  => asc ? groups.OrderBy(t => t.DurationNano)      : groups.OrderByDescending(t => t.DurationNano),
            "spans"     => asc ? groups.OrderBy(t => t.SpanCount)         : groups.OrderByDescending(t => t.SpanCount),
            "time"      => asc ? groups.OrderBy(t => t.MinStartTimeNano)  : groups.OrderByDescending(t => t.MinStartTimeNano),
            "service"   => asc ? groups.OrderBy(t => t.ServiceName)       : groups.OrderByDescending(t => t.ServiceName),
            "operation" => asc ? groups.OrderBy(t => t.RootSpan.Name)     : groups.OrderByDescending(t => t.RootSpan.Name),
            _           => isSlow ? groups.OrderByDescending(t => t.DurationNano) : groups.OrderByDescending(t => t.MinStartTimeNano),
        }).ToList();

        var existingParentIds = await CheckSpanIdsExistAsync(
            groups.Where(t => t.RootSpan.ParentSpanId != null).Select(t => t.RootSpan.ParentSpanId!).Distinct(), ct);

        return groups
            .Where(t => t.RootSpan.ParentSpanId == null || !existingParentIds.Contains(t.RootSpan.ParentSpanId))
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.ServiceName, t.RootSpan))
            .ToList();
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

    private async Task<HashSet<string>> CheckSpanIdsExistAsync(IEnumerable<string> spanIds, CancellationToken ct)
    {
        var ids = spanIds.ToList();
        if (ids.Count == 0) return [];
        await using var conn = await OpenConnectionAsync(ct);
        var existing = await conn.QueryAsync<string>(
            $"""
            SELECT s.span_id
            FROM spans s
            JOIN resources r ON s.resource_id = r.id
            WHERE r.tenant_id = @tenantId
              AND {SpanIdInPredicate}
            """,
            new { tenantId = TenantId, ids });
        return [..existing];
    }

    private async Task<List<RawSpan>> FetchRawSpansAsync(string whereClause, object parameters, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                s.trace_id              AS TraceId,
                s.start_time_unix_nano  AS StartTimeUnixNano,
                s.end_time_unix_nano    AS EndTimeUnixNano,
                s.status_code           AS StatusCode,
                s.name                  AS Name,
                s.parent_span_id        AS ParentSpanId,
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
                StartTimeUnixNano = r.StartTimeUnixNano,
                EndTimeUnixNano = r.EndTimeUnixNano,
                StatusCode = r.StatusCode,
                Name = r.Name,
                ParentSpanId = r.ParentSpanId,
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
                s.start_time_unix_nano  AS StartTimeUnixNano,
                s.end_time_unix_nano    AS EndTimeUnixNano,
                s.status_code           AS StatusCode,
                s.name                  AS Name,
                s.parent_span_id        AS ParentSpanId,
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
            StartTimeUnixNano = r.StartTimeUnixNano,
            EndTimeUnixNano = r.EndTimeUnixNano,
            StatusCode = r.StatusCode,
            Name = r.Name,
            ParentSpanId = r.ParentSpanId,
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

    private static TraceInfo ToTraceInfo(string traceId, int spanCount, long minStartNano, long maxEndNano, bool hasErrors, string? serviceName, RawSpan rootSpan) => new()
    {
        TraceIdHex = traceId,
        SpanCount = spanCount,
        TraceStartTime = TimeConversion.UnixNanoToDateTime(minStartNano),
        TraceEndTime = TimeConversion.UnixNanoToDateTime(maxEndNano),
        HasErrors = hasErrors,
        ServiceName = serviceName,
        RootOperationName = rootSpan.Name,
        RootSpanAttributes = rootSpan.SpanAttributes,
        TraceDuration = TimeConversion.UnixNanoToDateTime(maxEndNano) - TimeConversion.UnixNanoToDateTime(minStartNano)
    };

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class RawSpan
    {
        public string TraceId { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }

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
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public string? SpanAttributesJson { get; set; }
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class SlimSpanRow
    {
        public string TraceId { get; set; } = null!;
        public long StartTimeUnixNano { get; set; }
        public long EndTimeUnixNano { get; set; }
        public string StatusCode { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? ParentSpanId { get; set; }
        public long ResourceId { get; set; }
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
