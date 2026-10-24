using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Sqlite;

/// <summary>
/// The SQLite control-plane schema: this build's version, its embedded create script and its forward-only migrations
/// (<c>schema/SQLite-ControlPlane.sql</c> and <c>schema/SQLite-ControlPlane-Migrations</c>), and the two entry points any host can
/// call. <see cref="EnsureCurrentAsync"/> creates the file or migrates it forward (the API host does this at startup);
/// <see cref="CheckAsync"/> only verifies (the collector and the Admin tool do this, and fail when the file is missing or not at
/// exactly this build's version, so the upgrade order is the API host first). Downgrades are not supported.
/// </summary>
public static class SqliteControlPlaneSchema
{
    /// <summary>The control-plane schema version this build expects. The create script records the same version.</summary>
    public const string TargetVersion = "4.0.1";

    private const string CreateResource = "Keryhe.Telemetry.Sqlite.create.sql";
    private const string MigrationPrefix = "Keryhe.Telemetry.Sqlite.migrations.";

    private static readonly Lazy<SqliteSchemaDefinition> CurrentDefinition = new(Load);

    internal static SqliteSchemaDefinition Current => CurrentDefinition.Value;

    /// <summary>The full create script (a fresh install runs this, never the migration chain).</summary>
    public static string CreateScript => Current.CreateScript;

    /// <summary>The embedded migrations' names (<c>4.0.1-to-4.0.2</c>), oldest first. Empty until the first schema change.</summary>
    public static IReadOnlyList<string> MigrationNames => Current.Migrations.Select(m => m.Name).ToList();

    /// <summary>
    /// Creates the database when it has no schema, migrates it forward when it records an older version, and does nothing when it
    /// is current. Throws when it records a newer version, or an older one with no complete migration chain. The directory must
    /// exist; the file is created. Safe to run from several processes at once.
    /// </summary>
    public static Task EnsureCurrentAsync(SqliteControlPlaneConnection connection, ILogger logger, CancellationToken cancellationToken = default) =>
        SqliteSchemaRunner.EnsureCurrentAsync(Current, connection, logger, cancellationToken);

    /// <summary>Reports whether the file exists and is at exactly this build's version. Never creates or changes anything.</summary>
    public static Task<SqliteSchemaCheck> CheckAsync(SqliteControlPlaneConnection connection, CancellationToken cancellationToken = default) =>
        SqliteSchemaRunner.CheckAsync(Current, connection, cancellationToken);

    private static SqliteSchemaDefinition Load()
    {
        var assembly = typeof(SqliteControlPlaneSchema).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MigrationPrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => SqliteSchemaDefinition.ParseMigration(
                name[MigrationPrefix.Length..^".sql".Length], ReadResource(assembly, name)));
        return new SqliteSchemaDefinition(TargetVersion, ReadResource(assembly, CreateResource), migrations);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
