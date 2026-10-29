using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Opens a read connection to the control plane from <c>Telemetry:Collector:Provider:ControlPlane</c> and <c>ConnectionStrings:ControlPlane</c>, for the
/// collector-side readers that have no control-plane repository of their own (<see cref="RetentionWindowCache"/>, the fixed-tenant
/// startup check). Core references no relational driver, so the connection is created by type name: the host has loaded that provider's
/// driver for its API-key lookup.
/// </summary>
internal static class ControlPlaneDbConnection
{
    private static readonly Dictionary<string, string> ConnectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PostgreSQL"] = "Npgsql.NpgsqlConnection, Npgsql",
        ["SqlServer"] = "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient",
        ["MySql"] = "MySqlConnector.MySqlConnection, MySqlConnector",
        ["Sqlite"] = "Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite"
    };

    /// <summary>
    /// The connection string as these readers open it. SQLite creates a missing file by default and they only read, so it opens
    /// read-only (the collector's startup check has already confirmed the file exists); the others are used as configured.
    /// </summary>
    private static string ConnectionStringFor(string provider, string connectionString) =>
        provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)
            ? connectionString.TrimEnd().TrimEnd(';') + ";Mode=ReadOnly"
            : connectionString;

    /// <summary>An unopened connection, or null when the provider is unknown or the connection string is empty.</summary>
    public static DbConnection? TryCreate(IConfiguration configuration)
    {
        var provider = configuration[ProviderKeys.CollectorControlPlane] ?? "";
        var connectionString = configuration.GetConnectionString("ControlPlane");
        if (!ConnectionTypes.TryGetValue(provider, out var typeName) || string.IsNullOrEmpty(connectionString))
            return null;
        var type = Type.GetType(typeName) ?? throw new InvalidOperationException($"{typeName} is not loaded.");
        return (DbConnection)Activator.CreateInstance(type, ConnectionStringFor(provider, connectionString))!;
    }
}
