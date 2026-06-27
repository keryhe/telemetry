using System.Data.Common;
using System.Text.Json;
using Dapper;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data.Read;

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
    // JSON / ATTRIBUTE HELPERS (parity with the former EF [NotMapped] getters)
    // =========================================================================

    protected static Dictionary<string, object>? DeserializeAttributes(string? attributesJson)
        => string.IsNullOrEmpty(attributesJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object>>(attributesJson);

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
