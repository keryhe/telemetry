using Microsoft.Data.Sqlite;

namespace Keryhe.Telemetry.Admin.Data;

/// <summary>
/// This tool's own copy of <c>Keryhe.Telemetry.Sqlite</c>'s "is the control-plane file there and at exactly this build's schema
/// version" check (the tool references no provider project, as <c>ApiKeyHashing</c> is a copy too). It never creates or migrates the
/// database: the API host does. A test keeps <see cref="TargetVersion"/> equal to <c>SqliteControlPlaneSchema.TargetVersion</c> and
/// the messages in step.
/// </summary>
public static class SqliteSchemaGate
{
    public const string TargetVersion = "4.0.1";

    /// <summary>Null when the file exists and records exactly <see cref="TargetVersion"/>; otherwise one sentence saying what is wrong and what to do.</summary>
    public static async Task<string?> CheckAsync(string dataSource, string connectionString, CancellationToken ct)
    {
        if (!File.Exists(dataSource))
            return $"The SQLite control-plane database '{dataSource}' does not exist. Start the API host once to create it " +
                   $"(or run schema/apply-schema.sh controlplane sqlite '{dataSource}').";

        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var exists = conn.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'control_plane_schema_version'";
        if (await exists.ExecuteScalarAsync(ct) is null)
            return $"The SQLite control-plane database '{dataSource}' exists but has no control-plane schema. Start the API host once to create it.";

        Version? recorded = null;
        await using var versions = conn.CreateCommand();
        versions.CommandText = "SELECT version FROM control_plane_schema_version";
        await using (var reader = await versions.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (Version.TryParse(reader.GetString(0), out var version) && (recorded is null || version > recorded)) recorded = version;
        if (recorded is null)
            return $"The SQLite control-plane database '{dataSource}' exists but has no control-plane schema. Start the API host once to create it.";

        var target = Version.Parse(TargetVersion);
        if (recorded == target) return null;
        return recorded < target
            ? $"The SQLite control-plane database '{dataSource}' is at schema version {recorded} but this tool needs {TargetVersion}. Start the API host to migrate it."
            : $"The SQLite control-plane database '{dataSource}' is at schema version {recorded}, newer than this tool's {TargetVersion}. " +
              "This Admin tool is older than the database; use a newer build.";
    }
}
