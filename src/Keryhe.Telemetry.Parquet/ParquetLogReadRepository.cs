using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.ParquetRows;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// <see cref="ILogReadRepository"/> over <c>log_records</c> (plans/parquet-duckdb phase 5), following
/// <c>ClickHouseLogReadRepository</c>. A list reads <i>slices</i> of time from the newest (or oldest) end of its window, widening
/// (5 minutes first, x4, the whole remainder once the next slice would cover half of it) until it has <c>limit + 1</c> rows, so a
/// selective filter stops early. Free text is a case-insensitive substring of the body and an attribute filter an equality on the
/// lower-cased value, both unindexed: <c>RawSearchWindowGuard</c> is the bound (decision 2). A log carries no body type in this
/// layout, so a body comes back as text (<see cref="AttributeType.STRING"/>) and attribute values as strings (README D4).
/// </summary>
public sealed class ParquetLogReadRepository : DapperReadRepository, ILogReadRepository
{
    private const long NanosPerMinute = 60_000_000_000L;
    private const long FirstSliceNanos = 5 * NanosPerMinute;
    private const int SliceGrowth = 4;
    private const int FacetSampleSize = 10_000;
    private const long SurroundingWindowNanos = 24L * 60 * NanosPerMinute;
    private const long TraceLogMarginNanos = 5 * NanosPerMinute;

    private readonly ParquetQueryEngine _engine;
    private readonly int _summaryTimeoutSeconds;

    public ParquetLogReadRepository(IConfiguration configuration, ITenantContext tenantContext, ParquetQueryEngine engine) : base(tenantContext)
    {
        _engine = engine;
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured) ? configured : 5;
    }

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await _engine.OpenAsync([], cancellationToken);

    private ParquetViewScope LogScope(long? from, long? to) => new(ParquetTables.LogTable, TenantId, from, to);

    private const string Columns = """
        time_unix_nano AS TimeNano, observed_time_unix_nano AS ObservedNano, severity_number AS Severity, severity_text AS SeverityText,
        event_name AS EventName, body AS Body, to_json(attributes) AS LogAttributes, dropped_attributes_count AS Dropped, flags AS LogFlags,
        trace_hi AS TraceHi, trace_lo AS TraceLo, span_id AS SpanId,
        to_json(resource_attributes) AS ResourceAttributes, resource_schema_url AS ResourceSchemaUrl,
        scope_name AS ScopeName, scope_version AS ScopeVersion, to_json(scope_attributes) AS ScopeAttributes, scope_schema_url AS ScopeSchemaUrl
        """;

    // =========================================================================
    // FILTERS
    // =========================================================================

    private sealed class Filter
    {
        public string Where = "";
        public readonly DynamicParameters Parameters = new();
    }

    private Filter Compile(string? service, int? minSeverity, ParsedSearchQuery parsed)
    {
        var f = new Filter();
        f.Parameters.Add("tenantId", TenantId);
        var sb = new StringBuilder("tenant_id = @tenantId");
        if (!string.IsNullOrEmpty(service)) { sb.Append(" AND service_name = @service"); f.Parameters.Add("service", service); }
        if (minSeverity.HasValue) { sb.Append(" AND severity_number >= @minSeverity"); f.Parameters.Add("minSeverity", (byte)Math.Clamp(minSeverity.Value, 0, 255)); }

        if (parsed.IsTraceIdSearch)
        {
            var (hi, lo) = ParquetIds.TryTraceId(parsed.TraceId, out var id) ? id : (0UL, 0UL);
            sb.Append(" AND trace_hi = @traceHi AND trace_lo = @traceLo");
            f.Parameters.Add("traceHi", hi);
            f.Parameters.Add("traceLo", lo);
        }
        else
        {
            var i = 0;
            foreach (var term in parsed.Terms)
            {
                string predicate;
                if (term.IsAttributeFilter)
                {
                    f.Parameters.Add($"searchKey{i}", term.Key ?? "");
                    f.Parameters.Add($"searchVal{i}", (term.Value ?? "").ToLowerInvariant());
                    var value = $"element_at(attributes, @searchKey{i})[1]";
                    predicate = term.Negate
                        ? $"({value} IS NULL OR lower({value}) != @searchVal{i})"
                        : $"(lower({value}) = @searchVal{i})";
                }
                else
                {
                    f.Parameters.Add($"searchText{i}", (term.FreeText ?? "").ToLowerInvariant());
                    var match = $"contains(lower(body), @searchText{i})";
                    predicate = term.Negate ? $"NOT ({match})" : match;
                }
                sb.Append(" AND ").Append(predicate);
                i++;
            }
        }
        f.Where = sb.ToString();
        return f;
    }

    /// <summary>
    /// Reads rows in widening slices from the newest (or oldest) end of <c>[rangeFrom, rangeTo)</c> until <paramref name="need"/> are
    /// collected. Slices are disjoint, so what each returns concatenates in the final order.
    /// </summary>
    private static async Task<List<T>> SliceAsync<T>(
        DbConnection conn, Filter f, string select, int need, bool descending, long rangeFrom, long rangeTo,
        int timeoutSeconds, CancellationToken ct)
    {
        var rows = new List<T>(Math.Min(need, 1024));
        var order = descending ? "DESC" : "ASC";
        long lo = rangeFrom, hi = rangeTo, width = FirstSliceNanos;
        if (hi - lo <= width) width = Math.Max(1, hi - lo);

        while (rows.Count < need && lo < hi)
        {
            long sliceFrom, sliceTo;
            if (descending) { sliceTo = hi; sliceFrom = Math.Max(lo, hi - width); }
            else { sliceFrom = lo; sliceTo = Math.Min(hi, lo + width); }

            var p = new DynamicParameters(f.Parameters);
            p.Add("sliceFrom", sliceFrom);
            p.Add("sliceTo", sliceTo);
            rows.AddRange(await conn.QueryAsync<T>(new CommandDefinition($"""
                SELECT {select}
                FROM log_records
                WHERE {f.Where} AND time_unix_nano >= @sliceFrom AND time_unix_nano < @sliceTo
                ORDER BY time_unix_nano {order}
                LIMIT {need - rows.Count}
                """, p, commandTimeout: timeoutSeconds > 0 ? timeoutSeconds : null, cancellationToken: ct)));

            if (descending) hi = sliceFrom; else lo = sliceTo;
            var remaining = hi - lo;
            width = width > long.MaxValue / SliceGrowth ? remaining : width * SliceGrowth;
            if (width >= remaining / 2) width = Math.Max(1, remaining);
        }
        return rows;
    }

    // =========================================================================
    // LIST, BY TRACE, SURROUNDING
    // =========================================================================

    public async Task<LogListResult> GetLogListAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Max(1, query.Limit);
        var f = Compile(query.Service, query.MinSeverity, SearchQueryParser.Parse(query.Search));
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        await using var conn = await _engine.OpenAsync([LogScope(startNano, endNano)], cancellationToken);
        var rows = await SliceAsync<LogRow>(conn, f, Columns, limit + 1, !ListOrder.IsOldest(query.Order), startNano, endNano + 1, 0, cancellationToken);
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new LogListResult { Items = rows.Select(Map).ToList(), Truncated = truncated };
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (!ParquetIds.TryTraceId(traceIdHex, out var trace))
            throw new ArgumentException("Trace ID must be 32 hex characters", nameof(traceIdHex));

        // The trace's extent from trace_index names the hours to read; a trace the index does not know (logs without spans) is
        // searched for over every hour the tenant has.
        await using var conn = await _engine.OpenAsync([new ParquetViewScope(ParquetTables.TraceIndexTable, TenantId, null, null)], cancellationToken);
        var bounds = await conn.QuerySingleAsync<BoundsRow>(new CommandDefinition("""
            SELECT count(*) AS Cnt, CAST(coalesce(min(start_min), 0) AS BIGINT) AS MinStart, CAST(coalesce(max(end_max), 0) AS BIGINT) AS MaxEnd
            FROM trace_index WHERE tenant_id = @tenantId AND trace_hi = @hi AND trace_lo = @lo
            """, new { tenantId = TenantId, hi = trace.Hi, lo = trace.Lo }, cancellationToken: cancellationToken));
        await _engine.DefineAsync(conn, bounds.Cnt == 0 ? LogScope(null, null) : LogScope(bounds.MinStart - TraceLogMarginNanos, bounds.MaxEnd + TraceLogMarginNanos), cancellationToken);

        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition($"""
            SELECT {Columns} FROM log_records
            WHERE tenant_id = @tenantId AND trace_hi = @hi AND trace_lo = @lo
            ORDER BY time_unix_nano DESC
            """, new { tenantId = TenantId, hi = trace.Hi, lo = trace.Lo }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(
        long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default)
    {
        before = Math.Clamp(before, 0, 500);
        after = Math.Clamp(after, 0, 500);
        var f = Compile(service, null, ParsedSearchQuery.Empty);

        await using var conn = await _engine.OpenAsync(
            [LogScope(anchorTimeUnixNano - SurroundingWindowNanos, anchorTimeUnixNano + SurroundingWindowNanos)], cancellationToken);
        var older = before == 0 ? [] : await SliceAsync<LogRow>(conn, f, Columns, before, true, anchorTimeUnixNano - SurroundingWindowNanos, anchorTimeUnixNano, 0, cancellationToken);
        var newer = await SliceAsync<LogRow>(conn, f, Columns, after + 1, false, anchorTimeUnixNano, anchorTimeUnixNano + SurroundingWindowNanos, 0, cancellationToken);
        older.Reverse();
        return older.Concat(newer).Select(Map).ToList();
    }

    // =========================================================================
    // FACETS
    // =========================================================================

    public async Task<LogFacetsResult> GetLogFacetsAsync(LogFacetsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var f = Compile(query.Service, query.MinSeverity, SearchQueryParser.Parse(query.Search));
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        await using var conn = await _engine.OpenAsync([LogScope(startNano, endNano)], cancellationToken);
        // The sample is the newest matching rows; finding them can mean scanning the window, so it is bounded like the summaries.
        var (sample, timedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => SliceAsync<AttributesRow>(conn, f, "to_json(attributes) AS LogAttributes", FacetSampleSize, true, startNano, endNano + 1, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || sample == null) return new LogFacetsResult { TimedOut = true };

        var valueLimit = Math.Clamp(query.ValueLimit, 1, 100);
        var keyFilter = query.Keys is { Count: > 0 } ? new HashSet<string>(query.Keys) : null;
        var counts = new Dictionary<string, Dictionary<string, int>>();
        foreach (var row in sample)
        {
            foreach (var (key, value) in ToStrings(row.LogAttributes))
            {
                if (keyFilter != null && !keyFilter.Contains(key)) continue;
                if (!counts.TryGetValue(key, out var valueCounts)) counts[key] = valueCounts = new Dictionary<string, int>();
                valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
            }
        }

        var facets = counts.Select(kv => new LogFacet
        {
            Key = kv.Key,
            Values = kv.Value.OrderByDescending(v => v.Value).Take(valueLimit).Select(v => new LogFacetValue { Value = v.Key, Count = v.Value }).ToList()
        }).OrderBy(x => x.Key).ToList();
        return new LogFacetsResult { SampleSize = sample.Count, Facets = facets };
    }

    // =========================================================================
    // EXPORT
    // =========================================================================

    /// <summary>One unbuffered ordered read of the whole window (no cap); the cancellation token reaches the command and the timeout is off.</summary>
    public async IAsyncEnumerable<LogRecordModel> ExportLogsAsync(
        LogExportQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var f = Compile(query.Service, query.MinSeverity, SearchQueryParser.Parse(query.Search));
        var from = TimeConversion.DateTimeToUnixNano(query.Start);
        var to = TimeConversion.DateTimeToUnixNano(query.End) + 1;
        f.Parameters.Add("from", from);
        f.Parameters.Add("to", to);

        await using var conn = await _engine.OpenAsync([LogScope(from, to)], cancellationToken);
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition($"""
            SELECT {Columns} FROM log_records
            WHERE {f.Where} AND time_unix_nano >= @from AND time_unix_nano < @to
            ORDER BY time_unix_nano ASC
            """, f.Parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<LogRow>();
        while (await reader.ReadAsync(cancellationToken))
            yield return Map(parse(reader));
    }

    // =========================================================================
    // ROWS
    // =========================================================================

    private static LogRecordModel Map(LogRow r) => new()
    {
        Resource = new ResourceModel { SchemaUrl = NullIfEmpty(r.ResourceSchemaUrl), Attributes = ToObjects(r.ResourceAttributes) },
        InstrumentationScope = new InstrumentationScopeModel
        {
            Name = r.ScopeName ?? "", Version = NullIfEmpty(r.ScopeVersion), SchemaUrl = NullIfEmpty(r.ScopeSchemaUrl), Attributes = ToObjects(r.ScopeAttributes)
        },
        TimeUnixNano = r.TimeNano,
        ObservedTimeUnixNano = r.ObservedNano,
        SeverityNumber = r.Severity == 0 ? null : r.Severity,
        SeverityText = NullIfEmpty(r.SeverityText),
        EventName = NullIfEmpty(r.EventName),
        BodyType = AttributeType.STRING,
        BodyValue = r.Body,
        Attributes = ToObjects(r.LogAttributes),
        DroppedAttributesCount = (int)r.Dropped,
        Flags = (int)r.LogFlags,
        TraceIdHex = r.TraceHi == 0 && r.TraceLo == 0 ? null : ParquetIds.TraceIdHex(r.TraceHi, r.TraceLo),
        SpanIdHex = r.SpanId == 0 ? null : ParquetIds.SpanIdHex(r.SpanId)
    };

    private sealed class BoundsRow
    {
        public long Cnt { get; set; }
        public long MinStart { get; set; }
        public long MaxEnd { get; set; }
    }

    private sealed class AttributesRow
    {
        public string? LogAttributes { get; set; }
    }

    private sealed class LogRow
    {
        public long TimeNano { get; set; }
        public long ObservedNano { get; set; }
        public byte Severity { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? Body { get; set; }
        public string? LogAttributes { get; set; }
        public ulong Dropped { get; set; }
        public ulong LogFlags { get; set; }
        public ulong TraceHi { get; set; }
        public ulong TraceLo { get; set; }
        public ulong SpanId { get; set; }
        public string? ResourceAttributes { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public string? ScopeAttributes { get; set; }
        public string? ScopeSchemaUrl { get; set; }
    }
}
