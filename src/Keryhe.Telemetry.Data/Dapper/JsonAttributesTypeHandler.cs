using System.Data;
using System.Text.Json;
using Dapper;

namespace Keryhe.Telemetry.Data.Dapper;

/// <summary>
/// Base Dapper type handler that maps a JSON attribute column to/from a CLR value.
/// The serialization (System.Text.Json) is provider-agnostic and lives here; the
/// only provider difference is the parameter's wire type — <c>jsonb</c> for Npgsql
/// vs <c>nvarchar(max)</c> for Microsoft.Data.SqlClient — which subclasses set in
/// <see cref="ConfigureParameter"/>. Concrete handlers are registered per provider
/// in Phase 4 via <c>SqlMapper.AddTypeHandler</c>.
/// </summary>
/// <typeparam name="T">The CLR type stored in the JSON column (e.g. a
/// <see cref="Dictionary{TKey,TValue}"/> attribute bag).</typeparam>
public abstract class JsonAttributesTypeHandler<T> : SqlMapper.TypeHandler<T>
{
    public override T? Parse(object value)
        => value is string json && !string.IsNullOrEmpty(json)
            ? JsonSerializer.Deserialize<T>(json)
            : default;

    public override void SetValue(IDbDataParameter parameter, T? value)
    {
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
        ConfigureParameter(parameter);
    }

    /// <summary>
    /// Apply the provider-specific parameter type for a JSON column. For example,
    /// Npgsql sets <c>NpgsqlDbType.Jsonb</c>; SqlClient sets <c>SqlDbType.NVarChar</c>.
    /// </summary>
    protected abstract void ConfigureParameter(IDbDataParameter parameter);
}
