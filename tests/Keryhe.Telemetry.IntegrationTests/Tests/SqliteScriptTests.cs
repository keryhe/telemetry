using System.Text.RegularExpressions;
using Keryhe.Telemetry.Sqlite;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// <c>schema/SQLite-ControlPlane.sql</c> against its siblings: it must describe the same tables, columns, limits, defaults and indexes
/// as the PostgreSQL control-plane script (the script every control-plane provider is held to), and the copy embedded in the
/// assembly must be the file on disk. No Docker.
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class SqliteScriptTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{string.Join('/', parts)} was not found above {AppContext.BaseDirectory}.");
    }

    private sealed record Column(bool NotNull, int? Limit, string? Default);

    private static string StripComment(string line)
    {
        var at = line.IndexOf("--", StringComparison.Ordinal);
        return at >= 0 ? line[..at] : line;
    }

    /// <summary>Tables to columns for the two dialects (one column per line in both scripts).</summary>
    private static Dictionary<string, Dictionary<string, Column>> Tables(string script, Regex limit)
    {
        var tables = new Dictionary<string, Dictionary<string, Column>>();
        Dictionary<string, Column>? current = null;
        foreach (var raw in script.Split('\n'))
        {
            var line = StripComment(raw);
            var start = Regex.Match(line, @"^\s*CREATE TABLE (?:IF NOT EXISTS )?(\w+)\s*\(");
            if (start.Success) { current = tables[start.Groups[1].Value] = new(); continue; }
            if (current is null) continue;
            if (Regex.IsMatch(line, @"^\s*\)")) { current = null; continue; }

            var text = line.Trim().TrimEnd(',');
            if (text.Length == 0 || Regex.IsMatch(text, @"^(CONSTRAINT|PRIMARY|FOREIGN|UNIQUE|CHECK)\b", RegexOptions.IgnoreCase)) continue;
            var column = Regex.Match(text, @"^""?(\w+)""?\s+(.*)$");
            if (!column.Success) continue;

            var rest = column.Groups[2].Value;
            var notNull = rest.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase) || rest.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
            var limitMatch = limit.Match(rest);
            var defaultMatch = Regex.Match(rest, @"DEFAULT\s+(\(.*\)|[^\s,]+)", RegexOptions.IgnoreCase);
            current[column.Groups[1].Value] = new Column(
                notNull,
                limitMatch.Success ? int.Parse(limitMatch.Groups[1].Value) : null,
                defaultMatch.Success ? NormalizeDefault(defaultMatch.Groups[1].Value) : null);
        }
        return tables;
    }

    private static string NormalizeDefault(string value) => value.ToUpperInvariant() switch
    {
        var v when v.Contains("NOW()") || v.Contains("STRFTIME") || v.Contains("CURRENT_TIMESTAMP") => "now",
        "TRUE" => "1",
        "FALSE" => "0",
        var v => v
    };

    private static HashSet<string> Indexes(string script) =>
        Regex.Matches(script, @"CREATE (?:UNIQUE )?INDEX (?:IF NOT EXISTS )?(\w+)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void The_sqlite_script_describes_the_same_tables_columns_limits_defaults_and_indexes_as_the_postgresql_one()
    {
        var sqlite = File.ReadAllText(RepoFile("schema", "SQLite-ControlPlane.sql"));
        var postgres = File.ReadAllText(RepoFile("schema", "PostgreSQL-ControlPlane.sql"));
        var sqliteTables = Tables(sqlite, new Regex(@"length\(\w+\)\s*<=\s*(\d+)"));
        var postgresTables = Tables(postgres, new Regex(@"(?:VARCHAR|CHAR)\((\d+)\)", RegexOptions.IgnoreCase));

        Assert.Equal(postgresTables.Keys.Order(), sqliteTables.Keys.Order());
        foreach (var (table, columns) in postgresTables)
        {
            Assert.Equal(columns.Keys.Order(), sqliteTables[table].Keys.Order());
            foreach (var (name, expected) in columns)
                Assert.True(expected == sqliteTables[table][name], $"{table}.{name}: PostgreSQL {expected}, SQLite {sqliteTables[table][name]}");
        }
        Assert.Equal(Indexes(postgres).Order(), Indexes(sqlite).Order());
    }

    [Fact]
    public void The_embedded_scripts_are_the_files_on_disk()
    {
        var onDisk = File.ReadAllText(RepoFile("schema", "SQLite-ControlPlane.sql"));
        Assert.Equal(onDisk, SqliteControlPlaneSchema.CreateScript);

        var migrationDirectory = Path.GetDirectoryName(RepoFile("schema", "SQLite-ControlPlane-Migrations", "README.md"))!;
        var files = Directory.GetFiles(migrationDirectory, "*.sql").Select(Path.GetFileNameWithoutExtension).Order().ToList();
        Assert.Equal(files, SqliteControlPlaneSchema.MigrationNames.Order());
        foreach (var name in files)
            Assert.Equal(File.ReadAllText(Path.Combine(migrationDirectory, name + ".sql")), SqliteControlPlaneSchema.Current.Migrations.Single(m => m.Name == name).Script);
    }

    [Fact]
    public void The_target_version_is_the_one_the_script_records_and_the_runner_script_gates_on()
    {
        Assert.Contains($"VALUES ('{SqliteControlPlaneSchema.TargetVersion}'", SqliteControlPlaneSchema.CreateScript);
        Assert.Contains($"-- control-plane schema {SqliteControlPlaneSchema.TargetVersion}", SqliteControlPlaneSchema.CreateScript);
        var applySchema = File.ReadAllText(RepoFile("schema", "apply-schema.sh"));
        Assert.Contains($"CONTROLPLANE_TARGET_VERSION=\"{SqliteControlPlaneSchema.TargetVersion}\"", applySchema);
        // The sibling scripts record the same version (the control-plane component has one version for all of them).
        foreach (var script in new[] { "PostgreSQL-ControlPlane.sql", "SqlServer-ControlPlane.sql", "MySQL-ControlPlane.sql" })
            Assert.Contains($"control-plane schema {SqliteControlPlaneSchema.TargetVersion}", File.ReadAllText(RepoFile("schema", script)));
    }
}
