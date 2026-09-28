using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ITraceReadRepository"/>. Row-level predicates
/// (tenant, time range, status, parent/trace ids) are pushed into SQL via a join to
/// <c>resources</c>; span detail reads (<see cref="GetTraceByIdAsync"/> and friends) and the
/// service-dependency/operation-analytics reads keep their original shape. The list page's
/// summary/page/samples endpoints (list-pages-server-side plan, Phase 3) are built fresh against
/// the anchor-based schema (root spans plus <c>orphan_roots</c>, decision 41) and the trace
/// rollup tables (decisions 37-38) — see the SUMMARY/PAGE/SAMPLES region below.
/// </summary>
public abstract class TraceReadRepositoryBase : DapperReadRepository, ITraceReadRepository
{
    /// <summary>See <see cref="LogReadRepositoryBase"/>'s identical field for why this default exists.</summary>
    private readonly int _summaryTimeoutSeconds = 5;

    private const long NanosPerMinute = 60_000_000_000L;
    private const long NanosPerHour = 3_600_000_000_000L;

    protected TraceReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    protected TraceReadRepositoryBase(ITenantContext tenantContext, Microsoft.Extensions.Configuration.IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
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
    // ANCHORS (list-pages-server-side plan, Phase 3, decision 41)
    //
    // Every summary/page/samples query below anchors on this same derived table: null-parent
    // root spans plus orphan_roots (a trace whose real root never arrived — decision 41), each
    // carrying its own start/end/kind/name/created_at so mode=slow's duration filter and the
    // asOf pin both apply directly to the anchor row, never to the whole trace.
    // =========================================================================

    /// <summary>
    /// Default (PostgreSQL/Timescale/SqlServer/MySql) anchors subquery: a plain <c>UNION ALL</c>
    /// with a correlated <c>NOT EXISTS</c> re-check that a trace's orphan row is dropped the
    /// moment its real root arrives (decision 41's "read-time" safety net, on top of the rollup
    /// worker's own 15-minute re-roll). ClickHouse overrides this with an uncorrelated form and
    /// <c>LIMIT 1 BY trace_id, span_id</c> dedup (decision 34) — see
    /// <c>ClickHouseTraceReadRepository</c>.
    /// </summary>
    protected virtual string AnchorsSql => """
        (
            SELECT s.trace_id AS trace_id, s.id AS anchor_span_pk, s.span_id AS anchor_span_id, s.resource_id AS resource_id,
                   s.name AS root_name, s.kind AS anchor_kind,
                   s.start_time_unix_nano AS anchor_start, s.end_time_unix_nano AS anchor_end,
                   s.created_at AS anchor_created_at
            FROM spans s
            WHERE s.parent_span_id IS NULL
            UNION ALL
            SELECT o.trace_id, sp.id, sp.span_id, o.resource_id, sp.name, sp.kind,
                   o.start_time_unix_nano, o.end_time_unix_nano, o.detected_at
            FROM orphan_roots o
            JOIN spans sp ON sp.trace_id = o.trace_id AND sp.span_id = o.span_id
            WHERE NOT EXISTS (SELECT 1 FROM spans np WHERE np.trace_id = o.trace_id AND np.parent_span_id IS NULL)
        )
        """;

    // =========================================================================
    // SUMMARY (list-pages-server-side plan, Phase 3)
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
        var hasRawSearchFilter = parsed.IsTraceIdSearch || parsed.Terms.Count > 0;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        // Rollup eligibility (decision 37): time/service filters only. An operation filter always
        // falls back to raw -- the rollup's root_name is the ANCHOR's own name, whereas the page's
        // operation filter matches any span in the trace (decision 9); reusing the rollup for that
        // would silently narrow to a different, stricter population. mode=slow's duration filter
        // has no rollup equivalent either (the rollup keeps only fixed latency-bucket counts, not
        // per-trace durations), so it also forces raw.
        var rollupEligible = !hasRawSearchFilter && string.IsNullOrEmpty(query.Operation) && query.Mode != "slow";

        TraceSummaryResult? rollupResult = null;
        if (rollupEligible)
            rollupResult = await TryGetRollupSummaryAsync(conn, query, startNano, endNano, cancellationToken);

        var result = rollupResult ?? await GetRawSummaryAsync(conn, query, parsed, startNano, endNano, cancellationToken);

        // listTotal always comes from the raw path (Target API), independent of which source
        // answered the buckets/services/summary above.
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

    /// <summary>
    /// Attempts the rollup-table source (decisions 37-38), the same minute/hour-plus-raw-tail
    /// merge as <c>LogReadRepositoryBase.TryGetRollupSummaryAsync</c>. Restricted to
    /// <c>inbound = 1</c> rows throughout (decision 13: cards/chart count inbound-request anchors
    /// only). Returns null when there is no coverage of this window at all.
    /// </summary>
    private async Task<TraceSummaryResult?> TryGetRollupSummaryAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, long startNano, long endNano, CancellationToken cancellationToken)
    {
        var granularity = (endNano - startNano) > 24 * NanosPerHour ? "hour" : "minute";
        var table = granularity == "minute" ? "trace_rollup_minute" : "trace_rollup_hour";

        var state = await conn.QuerySingleOrDefaultAsync<RollupStateRow>(new CommandDefinition(
            "SELECT coverage_start_unix_nano AS CoverageStartUnixNano, rolled_until_unix_nano AS RolledUntilUnixNano " +
            "FROM rollup_state WHERE signal_name = 'traces' AND granularity = @granularity",
            new { granularity }, cancellationToken: cancellationToken));

        if (state?.CoverageStartUnixNano is not { } coverageStart || startNano < coverageStart)
            return null;

        var rolledUntil = state.RolledUntilUnixNano;
        var rollupEnd = Math.Min(endNano, rolledUntil);
        if (rollupEnd < startNano) rollupEnd = startNano;

        var serviceClause = string.IsNullOrEmpty(query.Service) ? "" : $" AND {ResourceServiceNameExpr()} = @service";
        var lbColumns = string.Join(",\n                   ", LatencyBucketSql.ColumnNames().Select((c, i) => $"SUM(trm.{c}) AS Lb{i:D2}"));
        var rollupSql = $"""
            SELECT trm.bucket_unix_nano AS BucketUnixNano,
                   SUM(trm.trace_count) AS TraceCount,
                   SUM(trm.error_count) AS ErrorCount,
                   SUM(trm.duration_sum_ms) AS DurationSumMs,
                   MAX(trm.duration_max_ms) AS DurationMaxMs,
                   {lbColumns}
            FROM {table} AS trm{RollupFinalHint}
            JOIN resources r ON trm.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND trm.inbound = 1
              AND trm.bucket_unix_nano >= @start AND trm.bucket_unix_nano < @rollupEnd
              {serviceClause}
            GROUP BY trm.bucket_unix_nano
            """;

        var rollupRows = (await conn.QueryAsync<TraceBucketAggRow>(new CommandDefinition(rollupSql, new
        {
            tenantId = TenantId,
            start = startNano,
            rollupEnd,
            service = query.Service
        }, cancellationToken: cancellationToken))).ToList();

        var byBucket = rollupRows.ToDictionary(r => r.BucketUnixNano);

        // Raw tail: [rollupEnd, endNano), aligned to the same bucket size, using the same anchor
        // scan the raw path uses (bounded by inbound anchors in the tail window, not spans).
        if (rollupEnd < endNano)
        {
            var bucketSizeNano = granularity == "minute" ? NanosPerMinute : NanosPerHour;
            var tailRows = await FetchAnchorAggregatesAsync(conn, "all", query.Service, null, null, null,
                new ParsedSearchQuery(), rollupEnd, endNano, null, inboundOnly: true, cancellationToken);
            foreach (var group in tailRows.GroupBy(a => AlignDown(a.AnchorStart, bucketSizeNano)))
            {
                var bucketStart = group.Key;
                var durations = group.Select(a => (double)(a.MaxEnd - a.MinStart) / 1_000_000.0).ToList();
                var lb = new long[LatencyBucketSql.BucketCount];
                foreach (var a in group)
                {
                    var idx = Array.FindLastIndex(LatencyBucketSql.LowerBoundsNano, b => (a.MaxEnd - a.MinStart) >= b);
                    if (idx < 0) idx = 0;
                    lb[idx]++;
                }
                var agg = new TraceBucketAggRow
                {
                    BucketUnixNano = bucketStart,
                    TraceCount = group.Count(),
                    ErrorCount = group.Count(a => a.HasErrorInt != 0),
                    DurationSumMs = durations.Sum(),
                    DurationMaxMs = durations.Count > 0 ? durations.Max() : 0,
                };
                agg.SetBuckets(lb);
                if (byBucket.TryGetValue(bucketStart, out var existing))
                    byBucket[bucketStart] = existing.MergeWith(agg);
                else
                    byBucket[bucketStart] = agg;
            }
        }

        var orderedBuckets = byBucket.Values.OrderBy(b => b.BucketUnixNano).ToList();
        var buckets = orderedBuckets.Select(b => new TraceVolumeBucket
        {
            Timestamp = TimeConversion.UnixNanoToDateTime(b.BucketUnixNano),
            Count = (int)b.TraceCount,
            ErrorCount = (int)b.ErrorCount,
            SumDurationMs = b.DurationSumMs,
            P50Ms = LatencyBucketSql.EstimatePercentile(b.Buckets(), 50),
            P95Ms = LatencyBucketSql.EstimatePercentile(b.Buckets(), 95),
            P99Ms = LatencyBucketSql.EstimatePercentile(b.Buckets(), 99),
        }).ToList();

        var totalCount = orderedBuckets.Sum(b => b.TraceCount);
        var totalErrors = orderedBuckets.Sum(b => b.ErrorCount);
        var mergedLb = new long[LatencyBucketSql.BucketCount];
        foreach (var b in orderedBuckets)
        {
            var bb = b.Buckets();
            for (var i = 0; i < mergedLb.Length; i++) mergedLb[i] += bb[i];
        }

        var services = await BuildServiceStatsFromRollupAsync(conn, query, startNano, rollupEnd, endNano, cancellationToken);

        var lastStart = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            $"""
            SELECT MAX(a.anchor_start) FROM {AnchorsSql} a
            JOIN resources r ON r.id = a.resource_id
            WHERE r.tenant_id = @tenantId AND a.anchor_kind IN ('SERVER', 'CONSUMER')
              AND a.anchor_start >= @start AND a.anchor_start <= @end
              {(string.IsNullOrEmpty(query.Service) ? "" : $"AND {ResourceServiceNameExpr()} = @service")}
            """,
            new { tenantId = TenantId, start = startNano, end = endNano, service = query.Service }, cancellationToken: cancellationToken));

        return new TraceSummaryResult
        {
            Source = "rollup",
            Buckets = buckets,
            Summary = new TraceWindowSummary
            {
                Count = (int)totalCount,
                ErrorCount = (int)totalErrors,
                P50Ms = LatencyBucketSql.EstimatePercentile(mergedLb, 50),
                P95Ms = LatencyBucketSql.EstimatePercentile(mergedLb, 95),
                P99Ms = LatencyBucketSql.EstimatePercentile(mergedLb, 99),
                ServiceCount = services.Count,
                LastTraceStartTime = lastStart.HasValue ? TimeConversion.UnixNanoToDateTime(lastStart.Value) : null,
            },
            Services = services,
            LatencyBuckets = BuildLatencyBucketsFromCounts(mergedLb, query),
            RequestCount = totalCount,
            TotalIsLowerBound = false,
        };
    }

    private async Task<List<ServiceStats>> BuildServiceStatsFromRollupAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, long startNano, long rollupEnd, long endNano, CancellationToken ct)
    {
        // Per-service stats read the raw anchor set for the whole window -- resource cardinality
        // is small (services, not traces), so this stays cheap even though it revisits the window
        // rather than reusing the rollup's own per-bucket rows (which don't carry service identity
        // once summed for the chart).
        var rows = await FetchAnchorAggregatesAsync(conn, "all", query.Service, null, null, null,
            new ParsedSearchQuery(), startNano, endNano, null, inboundOnly: true, ct);
        var windowSeconds = Math.Max((endNano - startNano) / 1_000_000_000.0, 1);
        return rows.GroupBy(r => r.ServiceName ?? "(unknown)")
            .Select(g =>
            {
                var durations = g.Select(r => (r.MaxEnd - r.MinStart) / 1_000_000.0).OrderBy(x => x).ToList();
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
    }

    private async Task<TraceSummaryResult> GetRawSummaryAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, CancellationToken cancellationToken)
    {
        var (rows, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await FetchAnchorAggregatesAsync(
                conn, query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs,
                parsed, startNano, endNano, timeoutSeconds, inboundOnly: true, ct),
            _summaryTimeoutSeconds, cancellationToken);

        if (timedOut || rows == null)
        {
            return new TraceSummaryResult { Source = "raw", Buckets = [], TotalIsLowerBound = true };
        }

        var bucketCount = Math.Clamp(query.BucketCount, 1, 500);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var buckets = new List<TraceVolumeBucket>(bucketCount);
        var byIndex = new List<AnchorAggregateRow>[bucketCount];
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
            var durations = group.Select(r => (r.MaxEnd - r.MinStart) / 1_000_000.0).OrderBy(x => x).ToList();
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

        var allDurations = rows.Select(r => (r.MaxEnd - r.MinStart) / 1_000_000.0).OrderBy(x => x).ToList();
        var services = rows.GroupBy(r => r.ServiceName ?? "(unknown)")
            .Select(g =>
            {
                var durations = g.Select(r => (r.MaxEnd - r.MinStart) / 1_000_000.0).OrderBy(x => x).ToList();
                var count = durations.Count;
                var errorCount = g.Count(r => r.HasErrorInt != 0);
                var windowSeconds = Math.Max((endNano - startNano) / 1_000_000_000.0, 1);
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
                ServiceCount = services.Count,
                LastTraceStartTime = rows.Count > 0 ? TimeConversion.UnixNanoToDateTime(rows.Max(r => r.AnchorStart)) : null,
            },
            Services = services,
            LatencyBuckets = BuildLatencyBucketsFromRows(rows, query),
            RequestCount = rows.Count,
            TotalIsLowerBound = false,
        };
    }

    /// <summary>Merges the fixed 40 latency-bucket counts pairwise down to the requested row count, clipped to the non-empty range (decision 12/Target API's latency heatmap note).</summary>
    private static List<TraceLatencyBucket> BuildLatencyBucketsFromCounts(long[] counts, TraceSummaryQuery query)
    {
        var rows = Math.Clamp(query.LatencyDurationRows, 1, LatencyBucketSql.BucketCount);
        var result = new List<TraceLatencyBucket>();
        var groupSize = (double)LatencyBucketSql.BucketCount / rows;
        for (var r = 0; r < rows; r++)
        {
            var lo = (int)(r * groupSize);
            var hi = (int)Math.Min(LatencyBucketSql.BucketCount, (r + 1) * groupSize);
            long sum = 0;
            for (var i = lo; i < hi; i++) sum += counts[i];
            if (sum == 0) continue;
            result.Add(new TraceLatencyBucket
            {
                XStart = default,
                XEnd = default,
                YStartMs = LatencyBucketSql.LowerBoundsMs[lo],
                YEndMs = hi < LatencyBucketSql.BucketCount ? LatencyBucketSql.LowerBoundsMs[hi] : LatencyBucketSql.LowerBoundsMs[^1] * 2,
                Count = (int)sum,
            });
        }
        return result;
    }

    /// <summary>Raw-path latency heatmap: a proper time x duration grid over the fetched anchor rows.</summary>
    private static List<TraceLatencyBucket> BuildLatencyBucketsFromRows(List<AnchorAggregateRow> rows, TraceSummaryQuery query)
    {
        if (rows.Count == 0) return [];

        var timeCols = Math.Clamp(query.BucketCount, 1, 200);
        var durationRows = Math.Clamp(query.LatencyDurationRows, 1, 100);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var durationsMs = rows.Select(r => (r.MaxEnd - r.MinStart) / 1_000_000.0).ToList();
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
            var durationMs = (r.MaxEnd - r.MinStart) / 1_000_000.0;
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

    /// <summary><c>listTotal</c>: an exact (or capped, on timeout) count of every anchor matching the filter, always on the raw path (Target API).</summary>
    private async Task<(long Total, bool IsLowerBound)> GetListTotalAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, DateTime asOf, CancellationToken cancellationToken)
    {
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        clauses.Add("a.anchor_created_at <= @asOf");
        parameters.Add("asOf", asOf);
        var where = string.Join(" AND ", clauses);
        var sql = $"SELECT COUNT(*) FROM {AnchorsSql} a JOIN resources r ON r.id = a.resource_id WHERE r.tenant_id = @tenantId AND {where}";

        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);

        if (!timedOut) return (result, false);

        // Capped fallback (decision 11): count a capped candidate set instead.
        var cappedSql = $"""
            SELECT COUNT(*) FROM (
                SELECT 1 FROM {AnchorsSql} a
                JOIN resources r ON r.id = a.resource_id
                WHERE r.tenant_id = @tenantId AND {where}
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

    private async Task<long> CountNewSinceAsOfAsync(
        System.Data.Common.DbConnection conn, TraceSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, DateTime asOf, CancellationToken cancellationToken)
    {
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        clauses.Add("a.anchor_created_at > @asOf");
        parameters.Add("asOf", asOf);
        var where = string.Join(" AND ", clauses);
        var sql = $"SELECT COUNT(*) FROM {AnchorsSql} a JOIN resources r ON r.id = a.resource_id WHERE r.tenant_id = @tenantId AND {where}";
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    // =========================================================================
    // PAGE (list-pages-server-side plan, Phase 3, keyset paging)
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

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildAnchorFilterClauses(query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);
        clauses.Add("a.anchor_created_at <= @asOf");
        parameters.Add("asOf", asOf);

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
                rows = await FetchAnchorPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;

            case "prev":
                forward = false;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("a.anchor_start", "a.anchor_span_pk", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchAnchorPageAsync(conn, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            case "last":
                forward = false;
                var lastCount = await TryGetExactAnchorCountAsync(conn, clauses, parameters, cancellationToken);
                requestedSize = lastCount.HasValue ? KeysetCursor.LastPageRowCount(lastCount.Value, size) : size;
                rows = await FetchAnchorPageAsync(conn, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            default: // "first"
                forward = true;
                requestedSize = size;
                rows = await FetchAnchorPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
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

        // Whole-trace aggregation for just this page's anchors (bounded by `size`), matching
        // TraceInfo's existing contract (error flag / duration over the FULL span set, decision 37's
        // "which duration" note) rather than the anchor's own narrower span.
        var items = await LoadPageTraceInfosAsync(conn, displayRows, cancellationToken);

        return new TracePageResult { Items = items, NextCursor = nextCursor, PrevCursor = prevCursor, AsOf = asOf };
    }

    private static string Encode(AnchorRow row, string filterHash) => KeysetCursor.Encode(row.AnchorStart, row.AnchorSpanPk, filterHash);

    private async Task<List<AnchorRow>> FetchAnchorPageAsync(
        System.Data.Common.DbConnection conn, List<string> clauses, DynamicParameters parameters,
        int size, bool descending, CancellationToken cancellationToken)
    {
        var where = string.Join(" AND ", clauses);
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT a.trace_id AS TraceId, a.anchor_span_pk AS AnchorSpanPk, a.anchor_span_id AS AnchorSpanId, a.resource_id AS ResourceId,
                   a.root_name AS RootName, a.anchor_kind AS AnchorKind,
                   a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd
            FROM {AnchorsSql} a
            JOIN resources r ON r.id = a.resource_id
            WHERE r.tenant_id = @tenantId AND {where}
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
        System.Data.Common.DbConnection conn, List<string> clauses, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var where = string.Join(" AND ", clauses);
        var sql = $"SELECT COUNT(*) FROM {AnchorsSql} a JOIN resources r ON r.id = a.resource_id WHERE r.tenant_id = @tenantId AND {where}";
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return timedOut ? null : result;
    }

    private async Task<List<TraceInfo>> LoadPageTraceInfosAsync(
        System.Data.Common.DbConnection conn, List<AnchorRow> anchors, CancellationToken ct)
    {
        if (anchors.Count == 0) return [];

        var traceIds = anchors.Select(a => a.TraceId).Distinct().ToArray();
        var resourceIds = anchors.Select(a => a.ResourceId).Distinct().ToArray();

        var aggSql = $"""
            SELECT fs.trace_id AS TraceId, COUNT(*) AS SpanCount,
                   MIN(fs.start_time_unix_nano) AS MinStart, MAX(fs.end_time_unix_nano) AS MaxEnd,
                   MAX(CASE WHEN fs.status_code = 'ERROR' THEN 1 ELSE 0 END) AS HasErrorInt
            FROM spans fs
            WHERE {TraceIdInPredicate("fs")}
            GROUP BY fs.trace_id
            """;
        var aggRows = (await conn.QueryAsync<TraceAggRow>(new CommandDefinition(
            aggSql, new { traceIds }, cancellationToken: ct))).ToDictionary(r => r.TraceId);

        var resourceRows = (await conn.QueryAsync<ResourceLookupRow>(new CommandDefinition(
            $"SELECT id AS Id, attributes_json AS AttributesJson FROM resources WHERE {ResourceIdInPredicate}",
            new { resourceIds }, cancellationToken: ct))).ToDictionary(r => r.Id);

        var items = new List<TraceInfo>(anchors.Count);
        foreach (var a in anchors)
        {
            aggRows.TryGetValue(a.TraceId, out var agg);
            resourceRows.TryGetValue(a.ResourceId, out var resource);
            var serviceName = ExtractServiceName(DeserializeAttributes(resource?.AttributesJson));
            var minStart = agg?.MinStart ?? a.AnchorStart;
            var maxEnd = agg?.MaxEnd ?? a.AnchorEnd;
            items.Add(new TraceInfo
            {
                TraceIdHex = a.TraceId,
                SpanCount = agg?.SpanCount ?? 1,
                TraceStartTime = TimeConversion.UnixNanoToDateTime(minStart),
                TraceEndTime = TimeConversion.UnixNanoToDateTime(maxEnd),
                TraceDuration = TimeSpan.FromTicks((maxEnd - minStart) / 100),
                ServiceName = serviceName,
                RootOperationName = a.RootName,
                HasErrors = (agg?.HasErrorInt ?? 0) != 0,
                DisplaySpanIdHex = a.AnchorSpanId,
            });
        }
        return items;
    }

    // =========================================================================
    // EXPORT (list-pages-server-side plan, Phase 8, decision 17)
    // =========================================================================

    /// <summary>
    /// Streams one <see cref="TraceInfo"/> row per matching trace, oldest first, with no row cap.
    /// Reuses <see cref="BuildAnchorFilterClauses"/> — the same time/mode/service/operation/
    /// duration/search compilation the summary and page endpoints use — plus the existing
    /// <see cref="FetchAnchorPageAsync"/>/<see cref="LoadPageTraceInfosAsync"/> pair (unchanged from
    /// Phase 3) to fetch and shape each chunk.
    ///
    /// Deliberately chunked (<see cref="ExportChunkSize"/> anchors per round trip) rather than a
    /// single Dapper unbuffered query like <see cref="LogReadRepositoryBase.ExportLogsAsync"/>:
    /// a trace row isn't produced by one flat SELECT — <see cref="LoadPageTraceInfosAsync"/> needs
    /// each chunk's whole trace-id set up front to run its own batched aggregate query (span count/
    /// min-start/max-end/has-error across the FULL span set of the matching traces, not just their
    /// anchors) and its own resource lookup, and that aggregate cannot be pushed into a single
    /// per-row streaming query without either an un-portable LATERAL/CROSS APPLY join (SqlServer/
    /// Postgres only) or a correlated subquery per row (which ClickHouse does not reliably support —
    /// see <see cref="DapperReadRepository.SpanLevelMatchPredicate"/>'s own doc comment on exactly
    /// that limitation). Chunking keeps memory bounded to one chunk's anchors/aggregates at a time
    /// (not the whole matching population) while staying correct on every provider; see this
    /// project's Phase 8 CLAUDE.md notes for the full reasoning behind this deviation from a literal
    /// unbuffered read.
    /// </summary>
    private const int ExportChunkSize = 1000;

    public async IAsyncEnumerable<TraceInfo> ExportTracesAsync(
        TraceExportQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var parsed = SearchQueryParser.Parse(query.Search);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (baseClauses, baseParameters) = BuildAnchorFilterClauses(
            query.Mode, query.Service, query.Operation, query.MinDurationMs, query.MaxDurationMs, parsed, startNano, endNano);

        await using var conn = await OpenConnectionAsync(cancellationToken);

        long? cursorK = null;
        long? cursorId = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var clauses = new List<string>(baseClauses);
            var parameters = new DynamicParameters(baseParameters);
            if (cursorK.HasValue)
            {
                clauses.Add(KeysetCursor.Predicate("a.anchor_start", "a.anchor_span_pk", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursorK.Value);
                parameters.Add("cursorId", cursorId!.Value);
            }

            var rows = await FetchAnchorPageAsync(conn, clauses, parameters, ExportChunkSize, descending: false, cancellationToken);
            if (rows.Count == 0) yield break;

            var hasMore = rows.Count > ExportChunkSize;
            if (hasMore) rows.RemoveAt(rows.Count - 1);

            var infos = await LoadPageTraceInfosAsync(conn, rows, cancellationToken);
            foreach (var info in infos)
                yield return info;

            if (!hasMore) yield break;

            var last = rows[^1];
            cursorK = last.AnchorStart;
            cursorId = last.AnchorSpanPk;
        }
    }

    /// <summary>Predicate matching <c>@traceIds</c> against a trace_id column on the given alias. Postgres/Timescale override to <c>= ANY(@traceIds)</c>.</summary>
    protected virtual string TraceIdInPredicate(string alias) => $"{alias}.trace_id IN @traceIds";

    /// <summary>Predicate matching <c>@resourceIds</c> against <c>resources.id</c>. Postgres/Timescale override to <c>= ANY(@resourceIds)</c>.</summary>
    protected virtual string ResourceIdInPredicate => "id IN @resourceIds";

    // =========================================================================
    // SAMPLES (list-pages-server-side plan, Phase 3, Target API) -- dashboard widgets
    // =========================================================================

    public async Task<List<TraceInfo>> GetTraceSamplesAsync(TraceSamplesQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Clamp(query.Limit, 1, 50);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        await using var conn = await OpenConnectionAsync(cancellationToken);

        if (query.Kind == "errors")
        {
            // Newest error traces, through idx_spans_error -- unfiltered population (Target API).
            var sql = $"""
                SELECT DISTINCT a.trace_id AS TraceId, a.anchor_span_pk AS AnchorSpanPk, a.anchor_span_id AS AnchorSpanId, a.resource_id AS ResourceId,
                       a.root_name AS RootName, a.anchor_kind AS AnchorKind, a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd
                FROM {AnchorsSql} a
                JOIN resources r ON r.id = a.resource_id
                WHERE r.tenant_id = @tenantId AND a.anchor_start >= @start AND a.anchor_start <= @end
                  AND a.trace_id IN (
                      SELECT s2.trace_id FROM spans s2
                      JOIN resources r2 ON s2.resource_id = r2.id
                      WHERE r2.tenant_id = @tenantId AND s2.status_code = 'ERROR'
                        AND s2.start_time_unix_nano >= @start AND s2.start_time_unix_nano <= @end
                  )
                ORDER BY a.anchor_start DESC
                {PagingClause}
                """;
            var rows = (await conn.QueryAsync<AnchorRow>(new CommandDefinition(sql,
                new { tenantId = TenantId, start = startNano, end = endNano, limit, offset = 0 }, cancellationToken: cancellationToken))).ToList();
            return await LoadPageTraceInfosAsync(conn, rows, cancellationToken);
        }
        else // "slowest"
        {
            // Anchors in the window ordered by the anchor's OWN duration (decision behind
            // mode=slow, applied the same way here), keeping the >500ms floor. There is no
            // duration index (decision 4 dropped it); scanning the window's anchors through
            // idx_spans_root_time (which covers end_time_unix_nano) stays cheap for the
            // dashboard's default 1-hour window.
            var sql = $"""
                SELECT a.trace_id AS TraceId, a.anchor_span_pk AS AnchorSpanPk, a.anchor_span_id AS AnchorSpanId, a.resource_id AS ResourceId,
                       a.root_name AS RootName, a.anchor_kind AS AnchorKind, a.anchor_start AS AnchorStart, a.anchor_end AS AnchorEnd
                FROM {AnchorsSql} a
                JOIN resources r ON r.id = a.resource_id
                WHERE r.tenant_id = @tenantId AND a.anchor_start >= @start AND a.anchor_start <= @end
                  AND (a.anchor_end - a.anchor_start) > 500000000
                ORDER BY (a.anchor_end - a.anchor_start) DESC
                {PagingClause}
                """;
            var rows = (await conn.QueryAsync<AnchorRow>(new CommandDefinition(sql,
                new { tenantId = TenantId, start = startNano, end = endNano, limit, offset = 0 }, cancellationToken: cancellationToken))).ToList();
            return await LoadPageTraceInfosAsync(conn, rows, cancellationToken);
        }
    }

    // =========================================================================
    // SHARED FILTER COMPILATION / ANCHOR AGGREGATE FETCH
    // =========================================================================

    /// <summary>
    /// Builds the time/service/operation/duration/search WHERE clauses shared by the summary,
    /// listTotal, page and samples queries, all scoped to the <see cref="AnchorsSql"/> alias
    /// <c>a</c>. Operation and search terms match ANY span in the trace (decision 9), via
    /// <see cref="DapperReadRepository.SpanLevelMatchPredicate"/>; mode=slow's duration filter
    /// applies to the anchor's own <c>anchor_end - anchor_start</c> (decision 4).
    /// </summary>
    private (List<string> Clauses, DynamicParameters Parameters) BuildAnchorFilterClauses(
        string mode, string? service, string? operation, double? minDurationMs, double? maxDurationMs,
        ParsedSearchQuery parsed, long startNano, long endNano)
    {
        var clauses = new List<string> { "a.anchor_start >= @start", "a.anchor_start <= @end" };
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);

        if (!string.IsNullOrEmpty(service))
        {
            clauses.Add($"{ResourceServiceNameExpr()} = @service");
            parameters.Add("service", service);
        }

        const string innerTime = " AND s2.start_time_unix_nano >= @start AND s2.start_time_unix_nano <= @end";

        if (!string.IsNullOrEmpty(operation))
        {
            clauses.Add(SpanLevelMatchPredicate("a.trace_id", innerTime, "s2.name = @operation"));
            parameters.Add("operation", operation);
        }

        if (mode == "errors")
        {
            clauses.Add(SpanLevelMatchPredicate("a.trace_id", innerTime, "s2.status_code = 'ERROR'"));
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
            parameters.Add("traceIdSearch", parsed.TraceId);
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
                    // decision 6/7: attribute filters match against either the span's own
                    // attributes or its resource's (a common case: filtering by a resource
                    // attribute like a k8s pod label).
                    var spanPred = AttributePredicate("s2.attributes_json", $"@{keyParam}", $"@{valueParam}", term.Negate);
                    var resPred = AttributePredicate("rs2.attributes_json", $"@{keyParam}", $"@{valueParam}", term.Negate);
                    innerPredicate = $"({spanPred} OR {resPred})";
                }
                else
                {
                    var valueParam = $"searchText{i}";
                    parameters.Add(valueParam, $"%{EscapeLike(term.FreeText ?? "")}%");
                    // decision 6: free text matches span name or status_message.
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
    /// resource-attribute check as well as a span-attribute one. PostgreSQL/Timescale/SqlServer/MySql
    /// use a correlated <c>EXISTS</c> with an extra join; ClickHouse overrides with its uncorrelated
    /// form.
    /// </summary>
    protected virtual string SpanLevelMatchPredicateWithResource(string traceIdColumn, string innerTimeClause, string innerPredicate)
        => $"EXISTS (SELECT 1 FROM spans s2 JOIN resources rs2 ON rs2.id = s2.resource_id WHERE s2.trace_id = {traceIdColumn}{innerTimeClause} AND {innerPredicate})";

    /// <summary>
    /// Fetches, per matching anchor trace, its whole-trace aggregate (span_count/min_start/max_end/
    /// has_error) plus its anchor's own resource/name/kind — the shared shape behind the raw
    /// summary path, the rollup's raw-tail merge, and per-service stats. Bounded by anchors in the
    /// window (not spans): the fan-out join to <c>spans</c> for the aggregate touches every span of
    /// every MATCHING trace, which for a busy window is still far smaller than every span in the
    /// window.
    /// </summary>
    private async Task<List<AnchorAggregateRow>> FetchAnchorAggregatesAsync(
        System.Data.Common.DbConnection conn, string mode, string? service, string? operation,
        double? minDurationMs, double? maxDurationMs, ParsedSearchQuery parsed,
        long startNano, long endNano, int? commandTimeoutSeconds, bool inboundOnly, CancellationToken ct)
    {
        var (clauses, parameters) = BuildAnchorFilterClauses(mode, service, operation, minDurationMs, maxDurationMs, parsed, startNano, endNano);
        if (inboundOnly) clauses.Add("a.anchor_kind IN ('SERVER', 'CONSUMER')");
        var where = string.Join(" AND ", clauses);

        var sql = $"""
            SELECT a.trace_id AS TraceId, a.resource_id AS ResourceId, a.anchor_start AS AnchorStart,
                   {ResourceServiceNameExpr("r")} AS ServiceName,
                   MIN(fs.start_time_unix_nano) AS MinStart, MAX(fs.end_time_unix_nano) AS MaxEnd,
                   MAX(CASE WHEN fs.status_code = 'ERROR' THEN 1 ELSE 0 END) AS HasErrorInt
            FROM {AnchorsSql} a
            JOIN resources r ON r.id = a.resource_id
            JOIN spans fs ON fs.trace_id = a.trace_id
            WHERE r.tenant_id = @tenantId AND {where}
            GROUP BY a.trace_id, a.resource_id, a.anchor_start, {ResourceServiceNameExpr("r")}
            """;

        var rows = await conn.QueryAsync<AnchorAggregateRow>(new CommandDefinition(
            sql, parameters, commandTimeout: commandTimeoutSeconds, cancellationToken: ct));
        return rows.ToList();
    }

    private static long AlignDown(long nano, long bucketSize) => nano / bucketSize * bucketSize;

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

    private sealed class RollupStateRow
    {
        public long? CoverageStartUnixNano { get; set; }
        public long RolledUntilUnixNano { get; set; }
    }

    private sealed class TraceBucketAggRow
    {
        public long BucketUnixNano { get; set; }
        public long TraceCount { get; set; }
        public long ErrorCount { get; set; }
        public double DurationSumMs { get; set; }
        public double DurationMaxMs { get; set; }
        public long Lb00 { get; set; } public long Lb01 { get; set; } public long Lb02 { get; set; } public long Lb03 { get; set; }
        public long Lb04 { get; set; } public long Lb05 { get; set; } public long Lb06 { get; set; } public long Lb07 { get; set; }
        public long Lb08 { get; set; } public long Lb09 { get; set; } public long Lb10 { get; set; } public long Lb11 { get; set; }
        public long Lb12 { get; set; } public long Lb13 { get; set; } public long Lb14 { get; set; } public long Lb15 { get; set; }
        public long Lb16 { get; set; } public long Lb17 { get; set; } public long Lb18 { get; set; } public long Lb19 { get; set; }
        public long Lb20 { get; set; } public long Lb21 { get; set; } public long Lb22 { get; set; } public long Lb23 { get; set; }
        public long Lb24 { get; set; } public long Lb25 { get; set; } public long Lb26 { get; set; } public long Lb27 { get; set; }
        public long Lb28 { get; set; } public long Lb29 { get; set; } public long Lb30 { get; set; } public long Lb31 { get; set; }
        public long Lb32 { get; set; } public long Lb33 { get; set; } public long Lb34 { get; set; } public long Lb35 { get; set; }
        public long Lb36 { get; set; } public long Lb37 { get; set; } public long Lb38 { get; set; } public long Lb39 { get; set; }

        public long[] Buckets() =>
        [
            Lb00, Lb01, Lb02, Lb03, Lb04, Lb05, Lb06, Lb07, Lb08, Lb09,
            Lb10, Lb11, Lb12, Lb13, Lb14, Lb15, Lb16, Lb17, Lb18, Lb19,
            Lb20, Lb21, Lb22, Lb23, Lb24, Lb25, Lb26, Lb27, Lb28, Lb29,
            Lb30, Lb31, Lb32, Lb33, Lb34, Lb35, Lb36, Lb37, Lb38, Lb39,
        ];

        public void SetBuckets(long[] values)
        {
            Lb00 = values[0]; Lb01 = values[1]; Lb02 = values[2]; Lb03 = values[3]; Lb04 = values[4];
            Lb05 = values[5]; Lb06 = values[6]; Lb07 = values[7]; Lb08 = values[8]; Lb09 = values[9];
            Lb10 = values[10]; Lb11 = values[11]; Lb12 = values[12]; Lb13 = values[13]; Lb14 = values[14];
            Lb15 = values[15]; Lb16 = values[16]; Lb17 = values[17]; Lb18 = values[18]; Lb19 = values[19];
            Lb20 = values[20]; Lb21 = values[21]; Lb22 = values[22]; Lb23 = values[23]; Lb24 = values[24];
            Lb25 = values[25]; Lb26 = values[26]; Lb27 = values[27]; Lb28 = values[28]; Lb29 = values[29];
            Lb30 = values[30]; Lb31 = values[31]; Lb32 = values[32]; Lb33 = values[33]; Lb34 = values[34];
            Lb35 = values[35]; Lb36 = values[36]; Lb37 = values[37]; Lb38 = values[38]; Lb39 = values[39];
        }

        public TraceBucketAggRow MergeWith(TraceBucketAggRow other)
        {
            var a = Buckets(); var b = other.Buckets();
            var merged = new long[a.Length];
            for (var i = 0; i < a.Length; i++) merged[i] = a[i] + b[i];
            var result = new TraceBucketAggRow
            {
                BucketUnixNano = BucketUnixNano,
                TraceCount = TraceCount + other.TraceCount,
                ErrorCount = ErrorCount + other.ErrorCount,
                DurationSumMs = DurationSumMs + other.DurationSumMs,
                DurationMaxMs = Math.Max(DurationMaxMs, other.DurationMaxMs),
            };
            result.SetBuckets(merged);
            return result;
        }
    }

    private sealed class AnchorAggregateRow
    {
        public string TraceId { get; set; } = null!;
        public long ResourceId { get; set; }
        public long AnchorStart { get; set; }
        public string? ServiceName { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
        public int HasErrorInt { get; set; }
    }

    private sealed class AnchorRow
    {
        public string TraceId { get; set; } = null!;
        public long AnchorSpanPk { get; set; }
        public string AnchorSpanId { get; set; } = null!;
        public long ResourceId { get; set; }
        public string RootName { get; set; } = null!;
        public string AnchorKind { get; set; } = "UNSPECIFIED";
        public long AnchorStart { get; set; }
        public long AnchorEnd { get; set; }
    }

    private sealed class TraceAggRow
    {
        public string TraceId { get; set; } = null!;
        public int SpanCount { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
        public int HasErrorInt { get; set; }
    }

    private sealed class ResourceLookupRow
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
