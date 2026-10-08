using System.Data.Common;
using System.Text.Json;
using Dapper;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Shared base for the Dapper read repositories. Provides the per-provider connection,
/// the active tenant id, and the JSON/attribute helpers that mirror exactly what the
/// former EF entity getters did (System.Text.Json deserialization into
/// <see cref="Dictionary{TKey,TValue}"/> with <see cref="JsonElement"/> values), so the
/// downstream shaping logic produces output identical to the EF implementation.
/// </summary>
public abstract class DapperReadRepository
{
    static DapperReadRepository()
    {
        // Map snake_case result columns (e.g. start_time_unix_nano) onto PascalCase row DTOs.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private readonly ITenantContext _tenantContext;

    protected DapperReadRepository(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    /// <summary>The active tenant id; telemetry reads are scoped to it (EF global query filter parity).</summary>
    protected long TenantId => _tenantContext.GetRequiredTenantId();

    /// <summary>Opens a provider-specific database connection for the read path.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Wraps an idempotent read (summary/list/facets) so a provider can retry it once on a
    /// transient error — SqlServer's read repositories override this to retry error 1205 (snapshot
    /// update conflict / deadlock victim) with a short jittered delay (decision 35). Every other
    /// provider's reads don't take locks that produce an equivalent transient failure, so the base
    /// implementation just runs the operation once.
    /// </summary>
    protected virtual Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation) => operation();

    // =========================================================================
    // SQL DIALECT HOOKS (defaults are PostgreSQL; SqlServer overrides)
    // =========================================================================

    /// <summary>Case-insensitive LIKE operator. Postgres uses <c>ILIKE</c>; SqlServer uses <c>LIKE</c> (case-insensitive collation).</summary>
    protected virtual string LikeOperator => "ILIKE";

    /// <summary>Trailing LIMIT/OFFSET clause for a paged query; expects <c>@limit</c> and <c>@offset</c> parameters and a preceding ORDER BY.</summary>
    protected virtual string PagingClause => "LIMIT @limit OFFSET @offset";

    /// <summary>
    /// How a trace/span id is bound as a parameter. The default is the plain string, which is right
    /// wherever the driver's default parameter type matches the column (Npgsql <c>text</c> against a
    /// <c>text</c> column, MySqlConnector against <c>ascii_bin</c>). SQL Server overrides it to a sized
    /// ANSI <c>DbString</c>, because Dapper's default <c>nvarchar(4000)</c> would force an implicit
    /// conversion of the <c>varchar</c> column and turn every id seek into a scan.
    /// </summary>
    protected virtual object IdParam(string? value, int length) => value!;

    /// <summary>The table expressions a fact row's resource and scope are joined from. ClickHouse overrides them to collapse the not-yet-merged duplicate rows of its eventually-deduplicated reference tables, so a duplicate cannot multiply the fact rows.</summary>
    protected virtual string ResourcesTable => "resources";
    protected virtual string ScopesTable => "instrumentation_scopes";

    /// <summary>
    /// SQL boolean expression: does the JSON column <paramref name="jsonColumn"/> contain the key
    /// named by parameter <paramref name="keyParam"/> (e.g. <c>"@tagKey0"</c>), regardless of the
    /// value's type? Unlike a value-extraction expression,
    /// this must not return NULL for a present key whose value is a JSON object/array/null — that
    /// would under-match. Used only as a coarse, safe-to-over-include pre-filter ahead of
    /// <c>TraceReadRepositoryBase</c>'s own authoritative C# tag-value check (list-page-scale
    /// plan, Phase 4) — a false positive here just means one extra trace gets fetched and then
    /// correctly excluded in C#; a false negative would silently drop a matching trace, which is
    /// why every override picks a JSON function that lists/tests keys, not one that extracts a
    /// scalar value.
    /// </summary>
    protected virtual string JsonHasKeyExpr(string jsonColumn, string keyParam) => $"({jsonColumn} -> {keyParam}) IS NOT NULL";

    /// <summary>
    /// Integer floor-division SQL expression, <c>numerator / denominator</c>, used to compute
    /// histogram bucket indices from nanosecond timestamps. The default (<c>bigint / bigint</c>)
    /// truncates toward zero on Postgres/SqlServer, which is correct floor division
    /// since the numerator is always &gt;= 0. ClickHouse and MySQL promote <c>/</c> to a
    /// floating-point result and must override this with their integer-division operator.
    /// </summary>
    protected virtual string BucketIndexExpr(string numerator, string denominator) => $"({numerator} / {denominator})";

    /// <summary>
    /// Casts an aggregate to a 64-bit integer, so a <c>SUM</c> that a provider returns as a
    /// <c>numeric</c>/<c>decimal</c> (PostgreSQL, MySQL) maps onto a <c>long</c> property.
    /// </summary>
    protected virtual string BigintExpr(string expression) => $"CAST({expression} AS BIGINT)";

    /// <summary>
    /// Escapes LIKE/ILIKE wildcards in user search text so <c>%</c>/<c>_</c> match literally.
    /// Postgres/ILIKE default: backslash escape. SqlServer overrides to bracket escaping. Moved
    /// here from <c>LogReadRepositoryBase</c> (list-pages-server-side plan, Phase 1) so
    /// <see cref="FreeTextPredicate"/> and the search-query-to-SQL compiler can reuse it for
    /// traces too, not just logs.
    /// </summary>
    protected virtual string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Substring-match predicate for a free-text search term (decision 5/6). The caller binds
    /// <paramref name="valueParam"/> to a <c>%</c>-wrapped, <see cref="EscapeLike"/>-escaped
    /// pattern. Postgres default to case-insensitive <c>ILIKE</c>; SqlServer/MySql
    /// override <see cref="LikeOperator"/> to plain <c>LIKE</c> (case-insensitive under their
    /// default collation already), so this hook needs no per-provider override of its own.
    /// </summary>
    protected virtual string FreeTextPredicate(string column, string valueParam) => $"{column} {LikeOperator} {valueParam}";

    /// <summary>
    /// The parameter VALUE to bind for a <c>key:value</c>/<c>key=value</c> attribute filter's key
    /// (list-pages-server-side plan, Phase 1, decision 7). PostgreSQL and ClickHouse
    /// take the raw key: their extraction functions (<c>-&gt;&gt;</c>, <c>JSONExtractRaw</c>) treat
    /// it as an object member name literal. SQL Server's <c>JSON_VALUE</c> and MySQL's
    /// <c>JSON_EXTRACT</c> instead take a JSON *path*, and an OpenTelemetry key routinely contains
    /// dots (e.g. <c>service.name</c>) that a naive path would read as nesting — so those two
    /// providers override this to build <c>'$."' + escaped key + '"'</c> in C# and bind that
    /// instead, keeping the key itself out of the SQL text entirely (never interpolated).
    /// </summary>
    protected virtual object AttributeKeyParamValue(string key) => key;

    /// <summary>
    /// SQL predicate testing a JSON column's extracted value (as text) against
    /// <paramref name="valueParam"/> (decisions 7 and 10). <paramref name="keyParam"/>/
    /// <paramref name="valueParam"/> are parameter name references (e.g. <c>"@key0"</c>) whose
    /// values the caller binds — the key parameter's bound value comes from
    /// <see cref="AttributeKeyParamValue"/>, so it is a raw key on three providers and a JSON path
    /// on two. Comparing the extracted TEXT form (not a typed value) is what lets <c>k:500</c>
    /// match both the string <c>"500"</c> and the number <c>500</c> (decision 7).
    ///
    /// Both sides are compared case-insensitively (mirrors <c>MetricReadRepositoryBase</c>'s
    /// retired <c>MatchesLabelFilters</c>, which this hook now replaces for metric label filters —
    /// see that type's doc comment). The attribute KEY itself is matched case-sensitively: OpenTelemetry
    /// semantic-convention keys are lowercase-dotted by convention, and a case-insensitive key
    /// lookup would need scanning every key in the JSON document on every provider instead of a
    /// single indexed-or-not extraction, which is out of Phase 1's scope.
    ///
    /// Negation keeps rows that lack the key (decision 10): <c>NOT (x = @v)</c> evaluates to NULL
    /// for a missing key and would drop the row, so negation is compiled as an explicit
    /// null-tolerant form per provider (<c>IS DISTINCT FROM</c> on Postgres;
    /// <c>IS NULL OR &lt;&gt;</c> on SqlServer/MySql; <c>JSONHas(...) = 0 OR !=</c> on ClickHouse).
    /// </summary>
    protected virtual string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
    {
        var expr = $"LOWER({column} ->> {keyParam})";
        var valueExpr = $"LOWER({valueParam})";
        return negated ? $"{expr} IS DISTINCT FROM {valueExpr}" : $"{expr} = {valueExpr}";
    }

    /// <summary>
    /// "Any span in this trace matches" form of an attribute/free-text predicate (decision 9),
    /// for use once trace search is wired up (Phase 3) — built now, per the Phase 1 spec, even
    /// though nothing calls it yet. <paramref name="innerPredicate"/> is a complete boolean SQL
    /// expression (typically an <see cref="AttributePredicate"/> or <see cref="FreeTextPredicate"/>
    /// result) referencing the correlated alias <paramref name="spanAlias"/> (default <c>s2</c>,
    /// matching <c>TraceReadRepositoryBase</c>'s existing correlated-subquery convention). The time
    /// range is mandatory: it is what keeps this narrowing rather than an unbounded scan on every
    /// provider.
    ///
    /// PostgreSQL/SqlServer/MySql use a correlated <c>EXISTS</c>; ClickHouse — which
    /// doesn't reliably support correlated <c>EXISTS</c> — uses an uncorrelated <c>trace_id IN
    /// (...)</c> instead, overridden below.
    /// </summary>
    protected virtual string SpanLevelMatchPredicate(string traceIdColumn, string innerTimeClause, string innerPredicate, string spanAlias = "s2")
        => $"EXISTS (SELECT 1 FROM spans {spanAlias} WHERE {spanAlias}.trace_id = {traceIdColumn}{innerTimeClause} AND {innerPredicate})";

    // =========================================================================
    // SEARCH QUERY -> SQL COMPILER (list-pages-server-side plan, Phase 1)
    // =========================================================================

    /// <summary>
    /// Compiles a <see cref="ParsedSearchQuery"/>'s terms into one SQL fragment (leading " AND ",
    /// empty when there are no terms) plus the parameters it references, using
    /// <see cref="FreeTextPredicate"/>/<see cref="AttributePredicate"/> so every provider gets the
    /// same compiled shape through its own dialect hooks. Terms are ANDed, mirroring the parser's
    /// own <c>' AND '</c> split. Nothing calls this yet — phases 2 and 3 wire it into the logs and
    /// traces endpoints; it is built now, correct and reusable, because the AttributePredicate/
    /// FreeTextPredicate hooks it depends on are also built in this phase with nothing else
    /// exercising them yet.
    ///
    /// Decision 7's exact-vs-contains distinction (<c>key=value</c> vs <c>key:value</c>) has no
    /// separate SQL form in Phase 1's scope: both compile through the same
    /// <see cref="AttributePredicate"/> equality check, since attribute values aren't free text to
    /// substring-match — there is nothing for "contains" to mean differently from "exact" once the
    /// value is extracted as text. If a future phase needs a real substring match on an attribute
    /// value, that would be a new hook, not a change to this one.
    /// </summary>
    protected (string Sql, DynamicParameters Parameters) CompileSearch(
        ParsedSearchQuery query, string freeTextColumn, string attributesColumn)
    {
        var parameters = new DynamicParameters();
        if (query.Terms.Count == 0)
            return ("", parameters);

        var clauses = new List<string>();
        var i = 0;
        foreach (var term in query.Terms)
        {
            if (term.IsAttributeFilter)
            {
                var keyParam = $"searchKey{i}";
                var valueParam = $"searchVal{i}";
                parameters.Add(keyParam, AttributeKeyParamValue(term.Key ?? ""));
                parameters.Add(valueParam, term.Value ?? "");
                clauses.Add(AttributePredicate(attributesColumn, $"@{keyParam}", $"@{valueParam}", term.Negate));
            }
            else
            {
                var valueParam = $"searchText{i}";
                parameters.Add(valueParam, $"%{EscapeLike(term.FreeText ?? "")}%");
                var predicate = FreeTextPredicate(freeTextColumn, $"@{valueParam}");
                clauses.Add(term.Negate ? $"NOT ({predicate})" : predicate);
            }
            i++;
        }

        return (" AND " + string.Join(" AND ", clauses), parameters);
    }

    /// <summary>
    /// <c>ToDictionary</c> that keeps the first row per key instead of throwing on a duplicate. The reference
    /// tables (<c>resources</c>, <c>instrumentation_scopes</c>, <c>metrics</c>) are <c>ReplacingMergeTree</c> on
    /// ClickHouse, whose dedup is eventual: two flushes that both missed the cache can store the same id twice
    /// until a merge, and a plain <c>ToDictionary</c> over those rows turned that into a 400 on every logs page.
    /// </summary>
    protected static Dictionary<TKey, TValue> ToDictionaryFirst<TSource, TKey, TValue>(
        IEnumerable<TSource> source, Func<TSource, TKey> key, Func<TSource, TValue> value) where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var item in source) result.TryAdd(key(item), value(item));
        return result;
    }

    /// <summary>
    /// <c>ToDictionary</c> that keeps the NEWEST row per key (largest <paramref name="observedAt"/>) instead of
    /// throwing on a duplicate, for lookups keyed on something the database does not enforce as unique. The five
    /// data-point tables are plain append targets since schema 3.0.0 (decision 7: no unique key, no foreign keys),
    /// so a re-delivered point is stored twice and any "one row per stream" result set is only as unique as the
    /// query that derived it; reads must tolerate a repeat rather than turn it into a 500. On a tie the first row
    /// wins — equal <paramref name="observedAt"/> means the same observation arrived twice, so either will do.
    /// Compare <see cref="ToDictionaryFirst"/>, which is the same guard for rows that carry no timestamp to rank by.
    /// </summary>
    protected static Dictionary<TKey, TSource> ToDictionaryNewest<TSource, TKey>(
        IEnumerable<TSource> source, Func<TSource, TKey> key, Func<TSource, long> observedAt) where TKey : notnull
    {
        var result = new Dictionary<TKey, TSource>();
        foreach (var item in source)
        {
            var k = key(item);
            if (!result.TryGetValue(k, out var existing) || observedAt(item) > observedAt(existing))
                result[k] = item;
        }
        return result;
    }

    // =========================================================================
    // JSON / ATTRIBUTE HELPERS (parity with the former EF [NotMapped] getters)
    // =========================================================================

    protected static Dictionary<string, object>? DeserializeAttributes(string? attributesJson)
        => string.IsNullOrEmpty(attributesJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(attributesJson);

    /// <summary>
    /// Reads a child collection stored as a JSON-text column back into a list, treating NULL and
    /// empty text as an empty list. Write paths store <c>null</c> rather than <c>"[]"</c> for the
    /// common empty case (see <c>TelemetryIngestionHelpers.SerializeListOrNull</c>), so the null
    /// branch here is the usual one, not an error path. Mirrors
    /// <c>MetricReadRepositoryBase.DeserializeExemplars</c>, which returns null instead because
    /// <c>MetricDataPoint.Exemplars</c> is itself nullable; span events/links are not.
    /// </summary>
    protected static List<T> DeserializeList<T>(string? json)
        => string.IsNullOrEmpty(json)
            ? new List<T>()
            : JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();

    protected static string? ExtractServiceName(Dictionary<string, object>? attributes)
    {
        if (attributes == null || !attributes.ContainsKey("service.name"))
            return null;
        return attributes["service.name"]?.ToString();
    }

    protected static string ConvertAttributeValueToString(object value)
    {
        return value switch
        {
            null => "",
            string str => str,
            bool b => b.ToString().ToLower(),
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString("G17"),
            float f => f.ToString("G9"),
            byte[] bytes => Convert.ToBase64String(bytes),
            JsonElement jsonElement => ConvertJsonElementToString(jsonElement),
            _ => JsonSerializer.Serialize(value)
        };
    }

    private static string ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetDouble().ToString("G17"),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            JsonValueKind.Array => JsonSerializer.Serialize(element),
            JsonValueKind.Object => JsonSerializer.Serialize(element),
            _ => element.ToString()
        };
    }
}
