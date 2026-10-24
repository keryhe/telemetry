using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.Sqlite;

/// <summary>One forward-only migration: the script that takes the schema from <see cref="From"/> to <see cref="To"/>.</summary>
internal sealed record SqliteMigration(Version From, Version To, string Name, string Script);

/// <summary>
/// A schema version's full create script plus the forward-only migrations that lead to it. The shipped definition is built from
/// the embedded resources (<see cref="SqliteControlPlaneSchema.Current"/>); the migration runner tests build their own.
/// </summary>
internal sealed class SqliteSchemaDefinition
{
    private static readonly Regex MigrationName = new(@"^(?<from>\d+(\.\d+){1,3})-to-(?<to>\d+(\.\d+){1,3})$", RegexOptions.Compiled);

    private readonly Dictionary<Version, SqliteMigration> _byFrom = new();

    public SqliteSchemaDefinition(string targetVersion, string createScript, IEnumerable<SqliteMigration>? migrations = null)
    {
        TargetVersion = targetVersion;
        Target = Version.Parse(targetVersion);
        CreateScript = createScript;
        foreach (var migration in migrations ?? [])
        {
            if (migration.To <= migration.From)
                throw new InvalidOperationException($"SQLite control-plane migration '{migration.Name}' does not go forward ({migration.From} to {migration.To}).");
            if (!_byFrom.TryAdd(migration.From, migration))
                throw new InvalidOperationException(
                    $"SQLite control-plane migrations '{_byFrom[migration.From].Name}' and '{migration.Name}' both start from {migration.From}.");
        }
        Migrations = _byFrom.Values.OrderBy(m => m.From).ToList();
    }

    public string TargetVersion { get; }
    public Version Target { get; }
    public string CreateScript { get; }
    public IReadOnlyList<SqliteMigration> Migrations { get; }

    /// <summary>
    /// The migrations that take <paramref name="recorded"/> to the target, in order; null with a <paramref name="problem"/> naming
    /// the missing step when there is no complete chain.
    /// </summary>
    public IReadOnlyList<SqliteMigration>? ChainFrom(Version recorded, out string? problem)
    {
        problem = null;
        var chain = new List<SqliteMigration>();
        var current = recorded;
        while (current < Target)
        {
            if (!_byFrom.TryGetValue(current, out var step))
            {
                problem = $"there is no migration from version {current} (expected a file named '{current}-to-<version>.sql') on the way to {TargetVersion}";
                return null;
            }
            if (step.To > Target)
            {
                problem = $"migration '{step.Name}' goes past this build's version {TargetVersion}";
                return null;
            }
            chain.Add(step);
            current = step.To;
        }
        return chain;
    }

    /// <summary>Parses a migration resource/file name (<c>4.0.1-to-4.0.2</c>) and wraps its script.</summary>
    public static SqliteMigration ParseMigration(string name, string script)
    {
        var match = MigrationName.Match(name);
        if (!match.Success)
            throw new InvalidOperationException($"SQLite control-plane migration '{name}' is not named '<from>-to-<to>' (for example '4.0.1-to-4.0.2').");
        return new SqliteMigration(Version.Parse(match.Groups["from"].Value), Version.Parse(match.Groups["to"].Value), name, script);
    }
}
