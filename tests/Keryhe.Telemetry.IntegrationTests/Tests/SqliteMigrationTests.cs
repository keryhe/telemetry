using System.Text.RegularExpressions;
using Keryhe.Telemetry.IntegrationTests.Sqlite;
using Keryhe.Telemetry.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The forward-only migration runner, against test-only scripts handed to it in place of the embedded set (none ship yet): a chain
/// 4.0.1 -> 4.0.2 (adds a column and a table) -> 4.0.3 (rebuilds <c>api_keys</c> with a foreign key, SQLite's way of changing a table).
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class SqliteMigrationTests
{
    private const string AddColumnAndTable = """
        ALTER TABLE tenants ADD COLUMN note TEXT;
        CREATE TABLE extra (
            id        INTEGER PRIMARY KEY,
            tenant_id INTEGER NOT NULL REFERENCES tenants (id) ON DELETE CASCADE
        ) STRICT;
        """;

    private const string RebuildApiKeys = """
        CREATE TABLE api_keys_new (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            tenant_id    INTEGER NOT NULL,
            key_hash     TEXT    NOT NULL CHECK (length(key_hash) <= 64),
            name         TEXT    NOT NULL CHECK (length(name) <= 255),
            is_active    INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
            created_at   TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%d %H:%M:%f0000', 'now')),
            last_used_at TEXT,
            expires_at   TEXT,
            label        TEXT,
            CONSTRAINT uk_api_key_hash UNIQUE (key_hash),
            CONSTRAINT fk_api_keys_tenants FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE CASCADE
        ) STRICT;
        INSERT INTO api_keys_new (id, tenant_id, key_hash, name, is_active, created_at, last_used_at, expires_at)
            SELECT id, tenant_id, key_hash, name, is_active, created_at, last_used_at, expires_at FROM api_keys;
        DROP TABLE api_keys;
        ALTER TABLE api_keys_new RENAME TO api_keys;
        CREATE INDEX idx_api_keys_tenant_id ON api_keys (tenant_id);
        """;

    private static SqliteMigration Step(string from, string to, string script) =>
        SqliteSchemaDefinition.ParseMigration($"{from}-to-{to}", script);

    private static SqliteSchemaDefinition Definition(string target, params SqliteMigration[] migrations) =>
        // The create script is only used for a file with no schema; every test here starts from a created 4.0.1 file.
        new(target, SqliteControlPlaneSchema.CreateScript, migrations);

    private static SqliteSchemaDefinition V401 => Definition("4.0.1");
    private static SqliteMigration M1 => Step("4.0.1", "4.0.2", AddColumnAndTable);
    private static SqliteMigration M2 => Step("4.0.2", "4.0.3", RebuildApiKeys);

    private static Task Ensure(SqliteSchemaDefinition definition, SqliteTestDb db) =>
        SqliteSchemaRunner.EnsureCurrentAsync(definition, db.Connection, NullLogger.Instance, CancellationToken.None);

    /// <summary>A created 4.0.1 file with a tenant, its API key and an alert rule.</summary>
    private static async Task<SqliteTestDb> V401DatabaseAsync()
    {
        var db = new SqliteTestDb();
        await Ensure(V401, db);
        await db.ExecAsync("""
            INSERT INTO tenants (name) VALUES ('t');
            INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (1, 'hash-1', 'key-1');
            """);
        return db;
    }

    [Fact]
    public async Task An_old_file_migrates_through_every_step_and_records_each_version()
    {
        using var db = await V401DatabaseAsync();
        var target = Definition("4.0.3", M1, M2);

        await Ensure(target, db);

        Assert.Equal(["4.0.1", "4.0.2", "4.0.3"], await db.VersionsAsync());
        Assert.Contains("note", await db.ColumnsOfAsync("tenants"));
        Assert.True(await db.HasTableAsync("extra"));
        Assert.Contains("label", await db.ColumnsOfAsync("api_keys"));
        Assert.Equal("hash-1", await db.ScalarAsync<string>("SELECT key_hash FROM api_keys WHERE id = 1"));
        Assert.True((await SqliteSchemaRunner.CheckAsync(target, db.Connection, CancellationToken.None)).IsCurrent);
        // A pooled connection from the library has foreign keys on.
        await using var conn = await db.Connection.OpenAsync(CancellationToken.None);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys";
        Assert.Equal(1L, await pragma.ExecuteScalarAsync());

        // The rebuilt table kept its foreign key: deleting the tenant cascades.
        await using var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM tenants WHERE id = 1";
        await delete.ExecuteNonQueryAsync();
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT COUNT(*) FROM api_keys"));
    }

    [Fact]
    public async Task A_backup_of_the_old_version_is_taken_before_migrating()
    {
        using var db = await V401DatabaseAsync();
        await Ensure(Definition("4.0.3", M1, M2), db);

        var backup = Assert.Single(Directory.GetFiles(db.Directory, "control-plane.db.bak-4.0.1-*"));
        await using var conn = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        await using var versions = conn.CreateCommand();
        versions.CommandText = "SELECT group_concat(version) FROM control_plane_schema_version";
        Assert.Equal("4.0.1", await versions.ExecuteScalarAsync());
        await using var rows = conn.CreateCommand();
        rows.CommandText = "SELECT COUNT(*) FROM api_keys";
        Assert.Equal(1L, await rows.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_file_already_at_a_later_step_runs_only_the_remaining_steps()
    {
        using var db = await V401DatabaseAsync();
        await Ensure(Definition("4.0.2", M1), db);
        Assert.Empty(Directory.GetFiles(db.Directory, "*.bak-4.0.2-*"));

        // Re-running 4.0.1 -> 4.0.2 would fail ("table extra already exists"), so success shows only the last step ran.
        await Ensure(Definition("4.0.3", M1, M2), db);

        Assert.Equal(["4.0.1", "4.0.2", "4.0.3"], await db.VersionsAsync());
        Assert.Single(Directory.GetFiles(db.Directory, "control-plane.db.bak-4.0.2-*"));
    }

    [Fact]
    public async Task A_gap_in_the_chain_fails_before_touching_the_file()
    {
        using var db = await V401DatabaseAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(Definition("4.0.3", M2), db));

        Assert.Contains("no migration from version 4.0.1", ex.Message);
        Assert.Equal(["4.0.1"], await db.VersionsAsync());
        Assert.Empty(Directory.GetFiles(db.Directory, "*.bak-*"));
    }

    [Fact]
    public void Two_scripts_from_the_same_version_or_one_going_backwards_are_rejected()
    {
        var duplicate = Assert.Throws<InvalidOperationException>(() => Definition("4.0.3", M1, Step("4.0.1", "4.0.3", "SELECT 1;")));
        Assert.Contains("4.0.1-to-4.0.2", duplicate.Message);
        Assert.Contains("4.0.1-to-4.0.3", duplicate.Message);
        Assert.Throws<InvalidOperationException>(() => Definition("4.0.3", Step("4.0.2", "4.0.1", "SELECT 1;")));
        Assert.Throws<InvalidOperationException>(() => SqliteSchemaDefinition.ParseMigration("upgrade", "SELECT 1;"));
    }

    [Fact]
    public async Task A_migration_going_past_this_builds_version_is_refused()
    {
        using var db = await V401DatabaseAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(Definition("4.0.2", Step("4.0.1", "4.0.5", "SELECT 1;")), db));
        Assert.Contains("goes past", ex.Message);
        Assert.Equal(["4.0.1"], await db.VersionsAsync());
    }

    [Fact]
    public async Task A_failing_step_rolls_everything_back_and_keeps_the_backup()
    {
        using var db = await V401DatabaseAsync();
        var failing = Step("4.0.2", "4.0.3", "CREATE TABLE half_done (id INTEGER) STRICT; INSERT INTO does_not_exist VALUES (1);");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(Definition("4.0.3", M1, failing), db));

        Assert.Contains("4.0.2-to-4.0.3", ex.Message);
        Assert.Contains("version 4.0.1", ex.Message);
        Assert.Equal(["4.0.1"], await db.VersionsAsync());
        Assert.DoesNotContain("note", await db.ColumnsOfAsync("tenants"));
        Assert.False(await db.HasTableAsync("extra"));
        Assert.False(await db.HasTableAsync("half_done"));
        var backup = Assert.Single(Directory.GetFiles(db.Directory, "control-plane.db.bak-4.0.1-*"));
        Assert.Contains(backup, ex.Message);
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
    }

    [Fact]
    public async Task A_step_that_leaves_a_dangling_foreign_key_is_rolled_back()
    {
        using var db = await V401DatabaseAsync();
        // Foreign keys are off during a migration, so this insert succeeds; the check before the commit is what catches it.
        var dangling = Step("4.0.1", "4.0.2", "INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (9999, 'orphan', 'orphan');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(Definition("4.0.2", dangling), db));

        Assert.Contains("api_keys", ex.Message);
        Assert.Equal(["4.0.1"], await db.VersionsAsync());
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT COUNT(*) FROM api_keys WHERE key_hash = 'orphan'"));
    }

    [Fact]
    public async Task A_newer_file_is_refused_by_a_definition_with_migrations()
    {
        using var db = await V401DatabaseAsync();
        await Ensure(Definition("4.0.3", M1, M2), db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Ensure(Definition("4.0.2", M1), db));
        Assert.Contains("4.0.3", ex.Message);
        Assert.Contains("4.0.2", ex.Message);
    }

    [Fact]
    public async Task Instances_migrating_the_same_file_together_apply_each_step_once()
    {
        using var db = await V401DatabaseAsync();
        var target = Definition("4.0.3", M1, M2);

        // A second run of M1 would fail on "CREATE TABLE extra", so every task succeeding shows each step ran once.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Ensure(target, db))));

        Assert.Equal(["4.0.1", "4.0.2", "4.0.3"], await db.VersionsAsync());
        Assert.True((await SqliteSchemaRunner.CheckAsync(target, db.Connection, CancellationToken.None)).IsCurrent);
    }

    // ---- the shipped migrations ----------------------------------------------------------------------------------------

    /// <summary>The rules every migration script follows; the runner owns the transaction, the foreign-key setting and the version row.</summary>
    internal static IEnumerable<string> RuleViolations(string name, string script)
    {
        var sql = Regex.Replace(Regex.Replace(script, @"--[^\n]*", string.Empty), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        if (Regex.IsMatch(sql, @"\b(BEGIN|COMMIT|ROLLBACK|END\s+TRANSACTION|SAVEPOINT|RELEASE)\b", RegexOptions.IgnoreCase))
            yield return $"{name} manages a transaction";
        if (Regex.IsMatch(sql, @"PRAGMA\s+foreign_keys", RegexOptions.IgnoreCase))
            yield return $"{name} sets PRAGMA foreign_keys";
        if (Regex.IsMatch(sql, @"control_plane_schema_version", RegexOptions.IgnoreCase))
            yield return $"{name} writes the version row";
    }

    [Fact]
    public void The_rule_check_catches_each_kind_of_violation()
    {
        Assert.Single(RuleViolations("a", "BEGIN; SELECT 1; COMMIT;"), v => v.Contains("transaction"));
        Assert.Contains("a sets PRAGMA foreign_keys", RuleViolations("a", "PRAGMA foreign_keys = OFF;"));
        Assert.Contains("a writes the version row", RuleViolations("a", "INSERT INTO control_plane_schema_version (version) VALUES ('x');"));
        Assert.Empty(RuleViolations("a", "-- BEGIN is only a word here\nALTER TABLE tenants ADD COLUMN x TEXT;"));
    }

    [Fact]
    public void Every_shipped_migration_follows_the_rules_and_the_chain_reaches_the_current_version()
    {
        foreach (var name in SqliteControlPlaneSchema.MigrationNames)
        {
            var migration = SqliteControlPlaneSchema.Current.Migrations.Single(m => m.Name == name);
            Assert.Empty(RuleViolations(name, migration.Script));
            Assert.True(migration.To > migration.From);
        }

        // Every version that has a migration must reach the current one without a gap.
        var current = SqliteControlPlaneSchema.Current;
        foreach (var migration in current.Migrations)
        {
            var chain = current.ChainFrom(migration.From, out var problem);
            Assert.True(chain is not null, problem);
        }
    }

    /// <summary>
    /// For each version that has a migration chain to the current one, a database built from that version's create script
    /// (<c>SqliteSchemas/&lt;version&gt;.sql</c>) plus the chain has the same schema as one built from the current create script.
    /// Nothing to compare until the first migration exists.
    /// </summary>
    [Fact]
    public async Task Fresh_equals_migrated()
    {
        var current = SqliteControlPlaneSchema.Current;
        foreach (var start in current.Migrations.Select(m => m.From).Distinct())
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "SqliteSchemas", $"{start}.sql");
            Assert.True(File.Exists(fixture),
                $"Add tests/Keryhe.Telemetry.IntegrationTests/SqliteSchemas/{start}.sql: the create script as it was at {start}, so the migration chain from it can be compared.");

            using var migrated = new SqliteTestDb();
            await SqliteSchemaRunner.EnsureCurrentAsync(
                new SqliteSchemaDefinition(start.ToString(), await File.ReadAllTextAsync(fixture)), migrated.Connection, NullLogger.Instance, CancellationToken.None);
            await SqliteSchemaRunner.EnsureCurrentAsync(current, migrated.Connection, NullLogger.Instance, CancellationToken.None);

            using var fresh = new SqliteTestDb();
            await SqliteSchemaRunner.EnsureCurrentAsync(current, fresh.Connection, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(await Normalized(fresh), await Normalized(migrated));
        }

        static async Task<List<string>> Normalized(SqliteTestDb db)
        {
            // Names and SQL of tables, indexes and triggers; a rebuilt table keeps its definition text but not the creation order.
            var rows = await db.ColumnAsync("SELECT type || ' ' || name || ' ' || replace(replace(coalesce(sql, ''), char(10), ' '), '  ', ' ') FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name");
            return rows;
        }
    }
}
