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

    // =========================================================================
    // SQL DIALECT HOOKS (defaults are PostgreSQL/Timescale; SqlServer overrides)
    // =========================================================================

    /// <summary>Case-insensitive LIKE operator. Postgres uses <c>ILIKE</c>; SqlServer uses <c>LIKE</c> (case-insensitive collation).</summary>
    protected virtual string LikeOperator => "ILIKE";

    /// <summary>Trailing LIMIT/OFFSET clause for a paged query; expects <c>@limit</c> and <c>@offset</c> parameters and a preceding ORDER BY.</summary>
    protected virtual string PagingClause => "LIMIT @limit OFFSET @offset";

    /// <summary>
    /// SQL expression extracting <c>service.name</c> from a resource's <c>attributes_json</c>
    /// column, aliased <paramref name="resourceAlias"/> (default <c>r</c>, the alias every
    /// existing caller's own resources join uses; trace-side callers pass <c>r2</c> for their
    /// correlated subqueries — see <c>TraceReadRepositoryBase.ServiceTracePredicate</c>).
    /// </summary>
    protected virtual string ResourceServiceNameExpr(string resourceAlias = "r") => $"{resourceAlias}.attributes_json ->> 'service.name'";

    /// <summary>
    /// SQL boolean expression: does the JSON column <paramref name="jsonColumn"/> contain the key
    /// named by parameter <paramref name="keyParam"/> (e.g. <c>"@tagKey0"</c>), regardless of the
    /// value's type? Unlike <see cref="ResourceServiceNameExpr"/>'s value-extraction expressions,
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
    /// truncates toward zero on Postgres/Timescale/SqlServer, which is correct floor division
    /// since the numerator is always &gt;= 0. ClickHouse and MySQL promote <c>/</c> to a
    /// floating-point result and must override this with their integer-division operator.
    /// </summary>
    protected virtual string BucketIndexExpr(string numerator, string denominator) => $"({numerator} / {denominator})";

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
