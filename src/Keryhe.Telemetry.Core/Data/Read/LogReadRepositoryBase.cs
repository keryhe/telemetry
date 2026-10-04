using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ILogReadRepository"/>. Since schema 3.0.0 a log record
/// carries its own <c>tenant_id</c> and <c>service_name</c>, so the tenant, service, severity and
/// time filters are plain predicates on <c>log_records</c>; <c>resources</c>/<c>instrumentation_scopes</c>
/// are joined only where their columns are selected (record detail). Shapes rows into
/// <see cref="LogRecordModel"/>.
/// </summary>
public abstract class LogReadRepositoryBase : DapperReadRepository, ILogReadRepository
{
    /// <summary>
    /// Defaults to <see cref="QueryOptions.SummaryTimeoutSeconds"/>'s own default (5) so every
    /// provider's read repository — none of which currently passes <see cref="IConfiguration"/> to
    /// this base — gets a sane timeout rather than 0, which would cancel every summary/page query
    /// immediately (<see cref="TimedQuery.RunAsync{T}"/> treats a 0-second budget as "expire now").
    /// The two-argument constructor below overrides it from configuration when a provider does
    /// pass one.
    /// </summary>
    private readonly int _summaryTimeoutSeconds = 5;

    /// <summary>Sample size for <see cref="GetLogFacetsAsync"/> (decision 15): the newest N matching rows, not the whole filtered set.</summary>
    private const int FacetSampleSize = 10_000;

    protected LogReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    protected LogReadRepositoryBase(ITenantContext tenantContext, IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
    }

    private string BaseSelect => $"""
        SELECT
            lr.time_unix_nano            AS TimeUnixNano,
            lr.observed_time_unix_nano   AS ObservedTimeUnixNano,
            lr.severity_number           AS SeverityNumber,
            lr.severity_text             AS SeverityText,
            lr.event_name                AS EventName,
            lr.body_type                 AS BodyType,
            lr.body_value                AS BodyValue,
            lr.dropped_attributes_count  AS DroppedAttributesCount,
            lr.flags                     AS Flags,
            lr.trace_id                  AS TraceId,
            lr.span_id                   AS SpanId,
            lr.attributes_json           AS AttributesJson,
            r.schema_url                 AS ResourceSchemaUrl,
            r.attributes_json            AS ResourceAttributesJson,
            sc.name                      AS ScopeName,
            sc.version                   AS ScopeVersion,
            sc.schema_url                AS ScopeSchemaUrl,
            sc.attributes_json           AS ScopeAttributesJson
        FROM log_records lr
        JOIN {ResourcesTable} r               ON lr.resource_id = r.id
        JOIN {ScopesTable} sc ON lr.scope_id = sc.id
        WHERE lr.tenant_id = @tenantId
        """;

    public async Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.id = @id",
            new { tenantId = TenantId, id }, cancellationToken: cancellationToken));
        return row == null ? null : Map(row);
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.trace_id = @traceId ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, traceId = IdParam(traceIdHex, 32) }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.time_unix_nano >= @start AND lr.time_unix_nano <= @end ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, start = startNano, end = endNano }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(
        long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default)
    {
        before = Math.Clamp(before, 0, 500);
        after = Math.Clamp(after, 0, 500);

        var serviceClause = string.IsNullOrEmpty(service) ? "" : " AND lr.service_name = @service";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        // Records strictly before the anchor, newest-first, capped at `before`.
        var older = (await conn.QueryAsync<LogRow>(new CommandDefinition(
            $"{BaseSelect}{serviceClause} AND lr.time_unix_nano < @anchor ORDER BY lr.time_unix_nano DESC {PagingClause}",
            new { tenantId = TenantId, service, anchor = anchorTimeUnixNano, limit = before, offset = 0 },
            cancellationToken: cancellationToken))).ToList();

        // The anchor row(s) plus records after it, oldest-first (+1 to include the anchor itself).
        var newer = (await conn.QueryAsync<LogRow>(new CommandDefinition(
            $"{BaseSelect}{serviceClause} AND lr.time_unix_nano >= @anchor ORDER BY lr.time_unix_nano ASC {PagingClause}",
            new { tenantId = TenantId, service, anchor = anchorTimeUnixNano, limit = after + 1, offset = 0 },
            cancellationToken: cancellationToken))).ToList();

        older.Reverse(); // DESC -> ASC so the whole window reads oldest -> newest.
        return older.Concat(newer).Select(Map).ToList();
    }

    // =========================================================================
    // PAGE (list-pages-server-side plan, Phase 2, keyset paging)
    // =========================================================================

    public async Task<LogPageResult> GetLogPageAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await ExecuteWithRetryAsync(() => GetLogPageCoreAsync(query, cancellationToken));
    }

    private async Task<LogPageResult> GetLogPageCoreAsync(LogQuery query, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(query.Size, 1, 500);
        var parsed = SearchQueryParser.Parse(query.Search);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);
        clauses.Add("lr.created_at <= @asOf");
        parameters.Add("asOf", asOf);

        var filterHashText = $"{query.Start:O}|{query.End:O}|{asOf:O}|{query.Service}|{query.Search}|{query.MinSeverity}";
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        DecodedCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = KeysetCursor.Decode(query.Cursor);
            if (cursor == null || !KeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        List<SlimLogRow> rows;
        bool forward;
        int requestedSize;

        switch (nav)
        {
            case "next":
                forward = true;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("lr.time_unix_nano", "lr.id", "cursorK", "cursorId", descending: true));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;

            case "prev":
                forward = false;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("lr.time_unix_nano", "lr.id", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            default: // "first"
                forward = true;
                requestedSize = size;
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;
        }

        // FetchPageAsync always fetches requestedSize+1 rows in its own query order, so a
        // (requestedSize+1)th row (whichever end it lands on doesn't matter — it's always the last
        // element returned) means there is more data beyond this page in that direction.
        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<SlimLogRow> displayRows;
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

        var resourceById = await LoadResourcesAsync(conn, cancellationToken);
        var scopeById = await LoadScopesAsync(conn, cancellationToken);

        return new LogPageResult
        {
            Items = displayRows.Select(r => MapSlim(r, resourceById, scopeById)).ToList(),
            NextCursor = nextCursor,
            PrevCursor = prevCursor,
            AsOf = asOf
        };
    }

    private static string Encode(SlimLogRow row, string filterHash) => KeysetCursor.Encode(row.TimeUnixNano ?? 0, row.Id, filterHash);

    private async Task<List<SlimLogRow>> FetchPageAsync(
        System.Data.Common.DbConnection conn, List<string> clauses, DynamicParameters parameters,
        int size, bool descending, CancellationToken cancellationToken)
    {
        var where = string.Join(" AND ", clauses);
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT
                lr.id                        AS Id,
                lr.time_unix_nano            AS TimeUnixNano,
                lr.observed_time_unix_nano   AS ObservedTimeUnixNano,
                lr.severity_number           AS SeverityNumber,
                lr.severity_text             AS SeverityText,
                lr.event_name                AS EventName,
                lr.body_type                 AS BodyType,
                lr.body_value                AS BodyValue,
                lr.dropped_attributes_count  AS DroppedAttributesCount,
                lr.flags                     AS Flags,
                lr.trace_id                  AS TraceId,
                lr.span_id                   AS SpanId,
                lr.attributes_json           AS AttributesJson,
                lr.resource_id               AS ResourceId,
                lr.scope_id                  AS ScopeId
            FROM log_records lr
            WHERE lr.tenant_id = @tenantId AND {where}
            ORDER BY lr.time_unix_nano {order}, lr.id {order}
            {PagingClause}
            """;
        var clone = new DynamicParameters(parameters);
        clone.Add("limit", size + 1);
        clone.Add("offset", 0);
        var rows = await conn.QueryAsync<SlimLogRow>(new CommandDefinition(sql, clone, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    // =========================================================================
    // FACETS (list-pages-server-side plan, Phase 2, decision 15)
    // =========================================================================

    public async Task<LogFacetsResult> GetLogFacetsAsync(LogFacetsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var parsed = SearchQueryParser.Parse(query.Search);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);
        var where = string.Join(" AND ", clauses);

        var sql = $"""
            SELECT lr.attributes_json AS AttributesJson
            FROM log_records lr
            WHERE lr.tenant_id = @tenantId AND {where}
            ORDER BY lr.time_unix_nano DESC
            {PagingClause}
            """;
        parameters.Add("limit", FacetSampleSize);
        parameters.Add("offset", 0);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // Bounded like the summaries: the sample is the newest matching rows, but finding them can mean scanning the
        // window (a search term, a narrow service), and without a budget it ran into the driver's 30 s default and
        // answered 500 under load (Timescale, the 2026-10-03 timeout-work ramp).
        var (attributeJsonRows, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => (await conn.QueryAsync<string?>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList(),
            _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || attributeJsonRows == null) return new LogFacetsResult { TimedOut = true };

        var valueLimit = Math.Clamp(query.ValueLimit, 1, 100);
        var keyFilter = query.Keys is { Count: > 0 } ? new HashSet<string>(query.Keys) : null;
        var counts = new Dictionary<string, Dictionary<string, int>>();

        foreach (var json in attributeJsonRows)
        {
            var attrs = DeserializeAttributes(json);
            if (attrs == null) continue;
            foreach (var (key, value) in attrs)
            {
                if (keyFilter != null && !keyFilter.Contains(key)) continue;
                var valueText = ConvertAttributeValueToString(value);
                if (!counts.TryGetValue(key, out var valueCounts))
                {
                    valueCounts = new Dictionary<string, int>();
                    counts[key] = valueCounts;
                }
                valueCounts[valueText] = valueCounts.GetValueOrDefault(valueText) + 1;
            }
        }

        var facets = counts.Select(kv => new LogFacet
        {
            Key = kv.Key,
            Values = kv.Value
                .OrderByDescending(v => v.Value)
                .Take(valueLimit)
                .Select(v => new LogFacetValue { Value = v.Key, Count = v.Value })
                .ToList()
        }).OrderBy(f => f.Key).ToList();

        return new LogFacetsResult { SampleSize = attributeJsonRows.Count, Facets = facets };
    }

    // =========================================================================
    // EXPORT (list-pages-server-side plan, Phase 8, decision 17)
    // =========================================================================

    /// <summary>
    /// Streams every matching log record, oldest first, with no row cap. Reuses
    /// <see cref="BuildFilterClauses"/> — the exact same time/service/severity/search compilation
    /// the summary and page endpoints use — so export can never see a different population than the
    /// list it's exporting from.
    ///
    /// Reads via <c>SqlMapper.ExecuteReaderAsync(CommandDefinition)</c> +
    /// <c>SqlMapper.GetRowParser&lt;T&gt;</c> rather than a buffered <c>QueryAsync</c> (or Dapper's
    /// own <c>QueryUnbufferedAsync</c>, which has no <c>CommandDefinition</c>/CancellationToken
    /// overload in this Dapper version — confirmed by reflecting its signature, not assumed): the
    /// reader is advanced one row at a time as the caller enumerates, so the whole matching set is
    /// never materialized in memory, and <see cref="CancellationToken"/> flows all the way to
    /// <c>ExecuteReaderAsync</c>/<c>ReadAsync</c> — which is what actually cancels the underlying
    /// database command when <c>HttpContext.RequestAborted</c> fires on a client disconnect, not
    /// just the enumeration loop. <c>CommandTimeout = 0</c> is set explicitly because Dapper's own
    /// default (30s) would cut off a genuinely large export partway through.
    /// </summary>
    public async IAsyncEnumerable<LogRecordModel> ExportLogsAsync(
        LogExportQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var parsed = SearchQueryParser.Parse(query.Search);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);
        var where = string.Join(" AND ", clauses);

        var sql = $"""
            {BaseSelect} AND {where}
            ORDER BY lr.time_unix_nano ASC, lr.id ASC
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(
            sql, parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<LogRow>();
        while (await reader.ReadAsync(cancellationToken))
            yield return Map(parse(reader));
    }

    // =========================================================================
    // SHARED FILTER COMPILATION
    // =========================================================================

    /// <summary>
    /// Builds the common time/service/minSeverity/search WHERE clauses (plus their parameters)
    /// shared by the summary, page and facets queries. A trace-id-shaped <c>q</c> (decision 10)
    /// filters on <c>lr.trace_id</c> exactly rather than through <see cref="DapperReadRepository.CompileSearch"/>,
    /// whose free-text/attribute compiler has no trace-id concept of its own.
    /// </summary>
    private (List<string> Clauses, DynamicParameters Parameters) BuildFilterClauses(
        string? service, int? minSeverity, ParsedSearchQuery parsed, long startNano, long endNano)
    {
        var clauses = new List<string> { "lr.time_unix_nano >= @start", "lr.time_unix_nano <= @end" };
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);

        if (!string.IsNullOrEmpty(service))
        {
            clauses.Add("lr.service_name = @service");
            parameters.Add("service", service);
        }
        if (minSeverity.HasValue)
        {
            clauses.Add("lr.severity_number >= @minSeverity");
            parameters.Add("minSeverity", minSeverity.Value);
        }

        if (parsed.IsTraceIdSearch)
        {
            clauses.Add("lr.trace_id = @traceIdSearch");
            parameters.Add("traceIdSearch", IdParam(parsed.TraceId, 32));
        }
        else if (parsed.Terms.Count > 0)
        {
            // CompileSearch returns its fragment with a leading " AND " (meant to be appended
            // directly to a WHERE clause); strip exactly that literal prefix rather than trimming
            // characters, which would also eat the leading "N" of a negated term's "NOT (...)".
            var (searchSql, searchParams) = CompileSearch(parsed, "lr.body_value", "lr.attributes_json");
            const string andPrefix = " AND ";
            clauses.Add(searchSql.StartsWith(andPrefix, StringComparison.Ordinal) ? searchSql[andPrefix.Length..] : searchSql);
            parameters.AddDynamicParams(searchParams);
        }

        return (clauses, parameters);
    }

    private async Task<Dictionary<long, ResourceRow>> LoadResourcesAsync(System.Data.Common.DbConnection conn, CancellationToken cancellationToken)
    {
        var resourceRows = await conn.QueryAsync<ResourceRow>(new CommandDefinition(
            "SELECT id AS Id, schema_url AS SchemaUrl, attributes_json AS AttributesJson FROM resources WHERE tenant_id = @tenantId",
            new { tenantId = TenantId }, cancellationToken: cancellationToken));
        return ToDictionaryFirst(resourceRows, r => r.Id, r => r);
    }

    private async Task<Dictionary<long, ScopeRow>> LoadScopesAsync(System.Data.Common.DbConnection conn, CancellationToken cancellationToken)
    {
        var scopeRows = await conn.QueryAsync<ScopeRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, version AS Version, schema_url AS SchemaUrl, attributes_json AS AttributesJson FROM instrumentation_scopes",
            cancellationToken: cancellationToken));
        return ToDictionaryFirst(scopeRows, s => s.Id, s => s);
    }

    // ClickHouse stores an absent trace id/span id/event name as '' (non-Nullable columns); the model keeps null.
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static LogRecordModel Map(LogRow r) => new()
    {
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
        },
        SeverityText = r.SeverityText,
        EventName = NullIfEmpty(r.EventName),
        SeverityNumber = r.SeverityNumber,
        Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>(),
        TraceIdHex = NullIfEmpty(r.TraceId),
        BodyType = r.BodyType == null ? null : Enum.Parse<AttributeType>(r.BodyType),
        BodyValue = r.BodyValue,
        DroppedAttributesCount = r.DroppedAttributesCount,
        Flags = r.Flags,
        ObservedTimeUnixNano = r.ObservedTimeUnixNano,
        SpanIdHex = NullIfEmpty(r.SpanId),
        TimeUnixNano = r.TimeUnixNano
    };

    private static LogRecordModel MapSlim(SlimLogRow r, IReadOnlyDictionary<long, ResourceRow> resourceById, IReadOnlyDictionary<long, ScopeRow> scopeById)
    {
        resourceById.TryGetValue(r.ResourceId, out var resource);
        scopeById.TryGetValue(r.ScopeId, out var scope);
        return new LogRecordModel
        {
            Resource = new ResourceModel
            {
                SchemaUrl = resource?.SchemaUrl,
                Attributes = DeserializeAttributes(resource?.AttributesJson) ?? new Dictionary<string, object>()
            },
            InstrumentationScope = new InstrumentationScopeModel
            {
                Name = scope?.Name ?? "",
                Version = scope?.Version,
                SchemaUrl = scope?.SchemaUrl,
                Attributes = DeserializeAttributes(scope?.AttributesJson) ?? new Dictionary<string, object>()
            },
            SeverityText = r.SeverityText,
            EventName = NullIfEmpty(r.EventName),
            SeverityNumber = r.SeverityNumber,
            Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>(),
            TraceIdHex = NullIfEmpty(r.TraceId),
            BodyType = r.BodyType == null ? null : Enum.Parse<AttributeType>(r.BodyType),
            BodyValue = r.BodyValue,
            DroppedAttributesCount = r.DroppedAttributesCount,
            Flags = r.Flags,
            ObservedTimeUnixNano = r.ObservedTimeUnixNano,
            SpanIdHex = NullIfEmpty(r.SpanId),
            TimeUnixNano = r.TimeUnixNano
        };
    }

    private sealed class LogRow
    {
        public long? TimeUnixNano { get; set; }
        public long? ObservedTimeUnixNano { get; set; }
        public int? SeverityNumber { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? BodyType { get; set; }
        public string? BodyValue { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int Flags { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? AttributesJson { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string ScopeName { get; set; } = null!;
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }

    private sealed class SlimLogRow
    {
        public long Id { get; set; }
        public long? TimeUnixNano { get; set; }
        public long? ObservedTimeUnixNano { get; set; }
        public int? SeverityNumber { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? BodyType { get; set; }
        public string? BodyValue { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int Flags { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? AttributesJson { get; set; }
        public long ResourceId { get; set; }
        public long ScopeId { get; set; }
    }

    private sealed class ResourceRow
    {
        public long Id { get; set; }
        public string? SchemaUrl { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class ScopeRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? Version { get; set; }
        public string? SchemaUrl { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class LogBucketRow
    {
        public int BucketIndex { get; set; }
        public long Trace { get; set; }
        public long Debug { get; set; }
        public long Info { get; set; }
        public long Warn { get; set; }
        public long Error { get; set; }
        public long Fatal { get; set; }
    }
}
