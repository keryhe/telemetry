using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// <see cref="ILogReadRepository"/> over <c>log_records</c> (plans/clickhouse-redesign phase 5, README R4).
///
/// The table's sort key is <c>(tenant_id, toStartOfFiveMinutes(timestamp), service_name, timestamp)</c>, so two rules run
/// through every query (Phase 0 spike 5): <b>every time range also carries a <c>toStartOfFiveMinutes(timestamp)</c>
/// predicate</b> (a predicate on <c>timestamp</c> alone does not prune the key: a 1-hour window read the whole table), and
/// <b>lists read in widening slices</b> (5 minutes first, x4, the whole remainder once the next slice would cover half of it),
/// each an ordered query with <c>LIMIT</c> on what is still missing, so a selective filter stops early instead of reading
/// the window. A whole-word search term (letters and digits) uses the token index through <c>hasToken(lower(body), 'word')</c>;
/// anything else is a substring scan of the (guarded) search window.
///
/// A log carries no body type in this layout: every body is returned as text with <see cref="AttributeType.STRING"/>
/// (a non-string body was stored as its text or JSON text). Attribute values come back as strings (README R7).
/// </summary>
public sealed class ClickHouseLogReadRepository : DapperReadRepository, ILogReadRepository
{
    private const long NanosPerMinute = 60_000_000_000L;
    private const long FirstSliceNanos = 5 * NanosPerMinute;
    private const int SliceGrowth = 4;
    private const int FacetSampleSize = 10_000;
    private const long SurroundingWindowNanos = 24L * 60 * NanosPerMinute;

    private static readonly Regex Word = new("^[A-Za-z0-9]+$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly int _summaryTimeoutSeconds;

    public ClickHouseLogReadRepository(IConfiguration configuration, ITenantContext tenantContext) : base(tenantContext)
    {
        _connectionString = configuration.GetConnectionString("Api")!;
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured) ? configured : 5;
    }

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    private static string At(string parameter) => $"fromUnixTimestamp64Nano(CAST(@{parameter} AS Int64), 'UTC')";

    private const string Columns = """
        toUnixTimestamp64Nano(timestamp) AS TimeNano, toUnixTimestamp64Nano(observed_timestamp) AS ObservedNano,
        severity_number AS Severity, severity_text AS SeverityText, event_name AS EventName, body AS Body,
        attributes AS LogAttributes, dropped_attributes_count AS Dropped, flags AS LogFlags, trace_id AS TraceId, span_id AS SpanId,
        resource_attributes AS ResourceAttributes, resource_schema_url AS ResourceSchemaUrl,
        scope_name AS ScopeName, scope_version AS ScopeVersion, scope_attributes AS ScopeAttributes, scope_schema_url AS ScopeSchemaUrl
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
        f.Parameters.Add("tenantId", (ulong)TenantId);
        var sb = new StringBuilder("tenant_id = @tenantId");
        if (!string.IsNullOrEmpty(service)) { sb.Append(" AND service_name = @service"); f.Parameters.Add("service", service); }
        if (minSeverity.HasValue) { sb.Append(" AND severity_number >= @minSeverity"); f.Parameters.Add("minSeverity", (byte)Math.Clamp(minSeverity.Value, 0, 255)); }

        if (parsed.IsTraceIdSearch)
        {
            sb.Append(" AND trace_id = @traceIdSearch");
            f.Parameters.Add("traceIdSearch", ClickHouseIds.TryTraceIdToGuid(parsed.TraceId, out var id) ? id : Guid.Empty);
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
                    predicate = term.Negate
                        ? $"(mapContains(attributes, @searchKey{i}) = 0 OR lowerUTF8(attributes[@searchKey{i}]) != @searchVal{i})"
                        : $"(mapContains(attributes, @searchKey{i}) = 1 AND lowerUTF8(attributes[@searchKey{i}]) = @searchVal{i})";
                }
                else
                {
                    var text = term.FreeText ?? "";
                    string match;
                    if (Word.IsMatch(text))
                    {
                        // a whole word: the token index serves hasToken, and only hasToken
                        f.Parameters.Add($"searchText{i}", text.ToLowerInvariant());
                        match = $"hasToken(lower(body), @searchText{i})";
                    }
                    else
                    {
                        f.Parameters.Add($"searchText{i}", text);
                        match = $"positionCaseInsensitiveUTF8(body, @searchText{i}) > 0";
                    }
                    predicate = term.Negate ? $"NOT ({match})" : match;
                }
                sb.Append(" AND ").Append(predicate);
                i++;
            }
        }
        f.Where = sb.ToString();
        return f;
    }

    /// <summary>The time-range predicate: the bound on <c>timestamp</c> plus the bucket bound that makes the sort key prune.</summary>
    private static string Range(string from, string to) =>
        $"timestamp >= {At(from)} AND timestamp < {At(to)} " +
        $"AND toStartOfFiveMinutes(timestamp) >= toStartOfFiveMinutes({At(from)}) AND toStartOfFiveMinutes(timestamp) <= toStartOfFiveMinutes({At(to)})";

    /// <summary>
    /// Reads rows in widening slices from the newest (or oldest) end of <c>[rangeFrom, rangeTo)</c> until <paramref name="need"/> are
    /// collected. Slices are disjoint, so what each returns concatenates in the final order.
    /// </summary>
    private async Task<List<T>> SliceAsync<T>(
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
                WHERE {f.Where} AND {Range("sliceFrom", "sliceTo")}
                ORDER BY toStartOfFiveMinutes(timestamp) {order}, timestamp {order}
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

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await SliceAsync<LogRow>(conn, f, Columns, limit + 1, !ListOrder.IsOldest(query.Order), startNano, endNano + 1, 0, cancellationToken);
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        return new LogListResult { Items = rows.Select(Map).ToList(), Truncated = truncated };
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));
        if (!ClickHouseIds.TryTraceIdToGuid(traceIdHex, out var trace))
            throw new ArgumentException("Trace ID must be 32 hex characters", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition($"""
            SELECT {Columns} FROM log_records
            WHERE tenant_id = @tenantId AND trace_id = @trace
            ORDER BY timestamp DESC
            """, new { tenantId = (ulong)TenantId, trace }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(
        long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default)
    {
        before = Math.Clamp(before, 0, 500);
        after = Math.Clamp(after, 0, 500);
        var f = Compile(service, null, ParsedSearchQuery.Empty);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // a day either side of the anchor bounds the read (the sort key needs a range to prune)
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

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // The sample is the newest matching rows; finding them can mean scanning the window, so it is bounded like the summaries.
        var (sample, timedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => SliceAsync<AttributesRow>(conn, f, "attributes AS LogAttributes", FacetSampleSize, true, startNano, endNano + 1, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);
        if (timedOut || sample == null) return new LogFacetsResult { TimedOut = true };

        var valueLimit = Math.Clamp(query.ValueLimit, 1, 100);
        var keyFilter = query.Keys is { Count: > 0 } ? new HashSet<string>(query.Keys) : null;
        var counts = new Dictionary<string, Dictionary<string, int>>();
        foreach (var row in sample)
        {
            if (row.LogAttributes is null) continue;
            foreach (var (key, value) in row.LogAttributes)
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
        f.Parameters.Add("from", TimeConversion.DateTimeToUnixNano(query.Start));
        f.Parameters.Add("to", TimeConversion.DateTimeToUnixNano(query.End) + 1);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var reader = await conn.ExecuteReaderAsync(new CommandDefinition($"""
            SELECT {Columns} FROM log_records
            WHERE {f.Where} AND {Range("from", "to")}
            ORDER BY toStartOfFiveMinutes(timestamp) ASC, timestamp ASC
            """, f.Parameters, commandTimeout: 0, cancellationToken: cancellationToken));
        var parse = reader.GetRowParser<LogRow>();
        while (await reader.ReadAsync(cancellationToken))
            yield return Map(parse(reader));
    }

    // =========================================================================
    // ROWS
    // =========================================================================

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static Dictionary<string, object> ToObjects(IDictionary<string, string>? map)
        => map is null ? new Dictionary<string, object>() : map.ToDictionary(kv => kv.Key, kv => (object)kv.Value);

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
        TraceIdHex = r.TraceId == Guid.Empty ? null : ClickHouseIds.GuidToTraceIdHex(r.TraceId),
        SpanIdHex = r.SpanId == 0 ? null : ClickHouseIds.UInt64ToSpanIdHex(r.SpanId)
    };

    private sealed class AttributesRow
    {
        public Dictionary<string, string>? LogAttributes { get; set; }
    }

    private sealed class LogRow
    {
        public long TimeNano { get; set; }
        public long ObservedNano { get; set; }
        public byte Severity { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? Body { get; set; }
        public Dictionary<string, string>? LogAttributes { get; set; }
        public uint Dropped { get; set; }
        public uint LogFlags { get; set; }
        public Guid TraceId { get; set; }
        public ulong SpanId { get; set; }
        public Dictionary<string, string>? ResourceAttributes { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public Dictionary<string, string>? ScopeAttributes { get; set; }
        public string? ScopeSchemaUrl { get; set; }
    }
}
