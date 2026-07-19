using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Read;

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

        var rows = (await conn.QueryAsync<FullSpanRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        if (rows.Count == 0) return new List<SpanModel>();

        // span_id values are DB-generated bigint primary keys, so they are safe to inline
        // directly into the IN(...) list. This avoids relying on Dapper list-parameter
        // expansion, which Npgsql rejects (it receives a single array param -> "IN $1").
        var idList = string.Join(",", rows.Select(r => r.Id));

        var events = (await conn.QueryAsync<SpanEventRow>(new CommandDefinition(
            $"SELECT span_id AS SpanDbId, name AS Name, time_unix_nano AS TimeUnixNano, dropped_attributes_count AS DroppedAttributesCount, attributes_json AS AttributesJson FROM span_events WHERE span_id IN ({idList}) ORDER BY id",
            cancellationToken: ct))).ToLookup(e => e.SpanDbId);

        var links = (await conn.QueryAsync<SpanLinkRow>(new CommandDefinition(
            $"SELECT span_id AS SpanDbId, linked_trace_id AS LinkedTraceId, linked_span_id AS LinkedSpanId, trace_state AS TraceState, flags AS Flags, dropped_attributes_count AS DroppedAttributesCount, attributes_json AS AttributesJson FROM span_links WHERE span_id IN ({idList}) ORDER BY id",
            cancellationToken: ct))).ToLookup(l => l.SpanDbId);

        return rows.Select(r => MapSpan(r, events[r.Id], links[r.Id])).ToList();
    }

    private static SpanModel MapSpan(FullSpanRow r, IEnumerable<SpanEventRow> events, IEnumerable<SpanLinkRow> links) => new()
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
        Events = events.Select(e => new SpanEventModel
        {
            Name = e.Name,
            TimeUnixNano = e.TimeUnixNano,
            DroppedAttributesCount = e.DroppedAttributesCount,
            Attributes = DeserializeAttributes(e.AttributesJson)
        }).ToList(),
        Links = links.Select(l => new SpanLinkModel
        {
            LinkedTraceIdHex = l.LinkedTraceId,
            LinkedSpanIdHex = l.LinkedSpanId,
            TraceState = l.TraceState,
            Flags = l.Flags,
            DroppedAttributesCount = l.DroppedAttributesCount,
            Attributes = DeserializeAttributes(l.AttributesJson)
        }).ToList(),
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

        var filtered = raw.Where(s => s.ResourceAttributes != null &&
                                      s.ResourceAttributes.ContainsKey("service.name") &&
                                      s.ResourceAttributes["service.name"]?.ToString() == serviceName);

        var groups = filtered
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
                HasErrors = g.Any(s => s.StatusCode == "ERROR"),
                RootSpan = g.FirstOrDefault(s => s.ParentSpanId == null) ?? g.OrderBy(s => s.StartTimeUnixNano).First()
            })
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
        var (where, p) = BuildTimeFilter(startTime, endTime, startBound: "s.start_time_unix_nano", endBound: "s.start_time_unix_nano",
            extra: "s.status_code = 'ERROR'");
        var raw = await FetchRawSpansAsync(where, p, cancellationToken);

        var groups = raw
            .GroupBy(s => s.TraceId)
            .Select(g => new
            {
                TraceIdHex = g.Key,
                SpanCount = g.Count(),
                MinStartTimeNano = g.Min(s => s.StartTimeUnixNano),
                MaxEndTimeNano = g.Max(s => s.EndTimeUnixNano),
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
            .Select(t => ToTraceInfo(t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, true, t.ServiceName, t.RootSpan))
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
    /// Fetches the raw spans matching the query's mode/service/time predicates, groups them into the
    /// same trace-level shape the legacy <c>Get*TracesAsync</c> methods produce, filters out non-root
    /// traces, and returns the full ordered list (no offset/limit applied — the caller pages it).
    /// </summary>
    private async Task<List<TraceInfo>> ComputeTraceInfosAsync(TraceQuery query, CancellationToken ct)
    {
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var isSlow = query.Mode == "slow";
        var minDurationNano = isSlow ? (long)((query.MinDurationMs ?? 500) * 1_000_000) : 0;
        var maxDurationNano = isSlow && query.MaxDurationMs.HasValue
            ? (long)(query.MaxDurationMs.Value * 1_000_000)
            : long.MaxValue;
        var operation = string.IsNullOrWhiteSpace(query.Operation) ? null : query.Operation;
        var tags = query.Tags;

        var clauses = new List<string> { "s.start_time_unix_nano >= @start", "s.start_time_unix_nano <= @end" };
        if (query.Mode == "errors") clauses.Add("s.status_code = 'ERROR'");
        var where = string.Join(" AND ", clauses);

        var raw = await FetchRawSpansAsync(where, new { tenantId = TenantId, start = startNano, end = endNano }, ct);

        if (!string.IsNullOrEmpty(query.Service))
        {
            raw = raw.Where(s => s.ResourceAttributes != null &&
                                 s.ResourceAttributes.TryGetValue("service.name", out var v) &&
                                 v?.ToString() == query.Service).ToList();
        }

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
                    ServiceName = ExtractServiceName(first.ResourceAttributes),
                    Spans = spans,
                };
            })
            .Select(t => new { t.TraceIdHex, t.SpanCount, t.MinStartTimeNano, t.MaxEndTimeNano, t.HasErrors, t.RootSpan, t.ServiceName, t.Spans, DurationNano = t.MaxEndTimeNano - t.MinStartTimeNano })
            .Where(t => !isSlow || (t.DurationNano >= minDurationNano && t.DurationNano <= maxDurationNano))
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
        return rows.Select(r => new RawSpan
        {
            TraceId = r.TraceId,
            StartTimeUnixNano = r.StartTimeUnixNano,
            EndTimeUnixNano = r.EndTimeUnixNano,
            StatusCode = r.StatusCode,
            Name = r.Name,
            ParentSpanId = r.ParentSpanId,
            SpanAttributes = DeserializeAttributes(r.SpanAttributesJson),
            ResourceAttributes = DeserializeAttributes(r.ResourceAttributesJson)
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
        public Dictionary<string, object>? SpanAttributes { get; set; }
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
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string ScopeName { get; set; } = null!;
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }

    private sealed class SpanEventRow
    {
        public long SpanDbId { get; set; }
        public string Name { get; set; } = null!;
        public long TimeUnixNano { get; set; }
        public int DroppedAttributesCount { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class SpanLinkRow
    {
        public long SpanDbId { get; set; }
        public string LinkedTraceId { get; set; } = null!;
        public string LinkedSpanId { get; set; } = null!;
        public string? TraceState { get; set; }
        public int Flags { get; set; }
        public int DroppedAttributesCount { get; set; }
        public string? AttributesJson { get; set; }
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
