using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Sqlite;

/// <summary>
/// Creates, migrates and checks a SQLite control-plane file against a <see cref="SqliteSchemaDefinition"/>. All the version logic
/// lives here; <see cref="SqliteControlPlaneSchema"/> is its public face for the shipped definition and the hosted services only
/// call that.
/// </summary>
internal static class SqliteSchemaRunner
{
    private const string VersionTable = "control_plane_schema_version";

    /// <summary>Verifies only: never creates, migrates or writes (beyond what opening a WAL database does).</summary>
    public static async Task<SqliteSchemaCheck> CheckAsync(
        SqliteSchemaDefinition definition, SqliteControlPlaneConnection connection, CancellationToken ct)
    {
        SqliteSchemaCheck Result(SqliteSchemaState state, Version? recorded = null) =>
            new(state, connection.DataSource, recorded?.ToString(), definition.TargetVersion);

        // Opening a missing file ReadWrite is an error, not a creation, but "does not exist" deserves its own message.
        if (!File.Exists(connection.DataSource)) return Result(SqliteSchemaState.MissingFile);

        await using var db = connection.CreateDedicated(SqliteOpenMode.ReadWrite);
        await db.OpenAsync(ct);
        var recorded = await ReadRecordedVersionAsync(db, null, ct);
        if (recorded is null) return Result(SqliteSchemaState.NotInitialized);
        if (recorded == definition.Target) return Result(SqliteSchemaState.Current, recorded);
        return Result(recorded < definition.Target ? SqliteSchemaState.Older : SqliteSchemaState.Newer, recorded);
    }

    /// <summary>
    /// Brings the file to the definition's version: creates it when no version is recorded, applies the migration chain when an
    /// older one is, does nothing at the same version, and fails on a newer version or an older one with no complete chain.
    /// </summary>
    public static async Task EnsureCurrentAsync(
        SqliteSchemaDefinition definition, SqliteControlPlaneConnection connection, ILogger logger, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(connection.DataSource)!;
        if (!Directory.Exists(directory))
            throw new InvalidOperationException(
                $"The directory '{directory}' for the SQLite control-plane database does not exist; create it (the host creates the file, never its directory).");

        await using var db = connection.CreateDedicated(SqliteOpenMode.ReadWriteCreate);
        await db.OpenAsync(ct);
        // Stored in the file, and the one pragma that cannot run inside a transaction.
        await ExecuteAsync(db, null, "PRAGMA journal_mode = WAL", ct);

        var plan = Plan(definition, connection, await ReadRecordedVersionAsync(db, null, ct));
        if (plan.IsNothing) return;

        string? backup = null;
        if (plan.Chain is { Count: > 0 })
        {
            backup = $"{connection.DataSource}.bak-{plan.Recorded}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            logger.LogInformation("Backing up the SQLite control plane at version {Version} to {Backup} before migrating", plan.Recorded, backup);
            // VACUUM INTO cannot run inside a transaction; the new file is a consistent copy even with the hosts running.
            await ExecuteAsync(db, null, "VACUUM INTO @file", ct, ("@file", backup));
        }

        // Foreign keys must be off while a migration rebuilds a table, and the pragma cannot change inside a transaction.
        await ExecuteAsync(db, null, "PRAGMA foreign_keys = OFF", ct);
        SqliteTransaction? tx = null;
        var step = "reading the version";
        try
        {
            // IMMEDIATE takes the write lock up front, so two API processes starting together serialize here.
            tx = db.BeginTransaction(deferred: false);
            // Another instance may have created or migrated the file since the unlocked read.
            plan = Plan(definition, connection, await ReadRecordedVersionAsync(db, tx, ct));
            if (!plan.IsNothing)
            {
                if (plan.Chain is null)
                {
                    step = "creating the schema";
                    await ExecuteAsync(db, tx, definition.CreateScript, ct);
                    logger.LogInformation("Created the SQLite control-plane schema {Version} in {File}", definition.TargetVersion, connection.DataSource);
                }
                else
                {
                    foreach (var migration in plan.Chain)
                    {
                        step = $"migration '{migration.Name}'";
                        await ExecuteAsync(db, tx, migration.Script, ct);
                        await ExecuteAsync(db, tx,
                            $"INSERT INTO {VersionTable} (version, applied_at) VALUES (@version, {SqliteTime.NowSql}) " +
                            "ON CONFLICT (version) DO UPDATE SET applied_at = excluded.applied_at",
                            ct, ("@version", migration.To.ToString()));
                        logger.LogInformation("Migrated the SQLite control plane {From} -> {To} ({Name})", migration.From, migration.To, migration.Name);
                    }
                }

                step = "the foreign key check";
                await using var check = Command(db, tx, "PRAGMA foreign_key_check");
                await using var reader = await check.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    throw new InvalidOperationException($"rows in table '{reader.GetString(0)}' reference a missing parent row");
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            try { tx?.Rollback(); } catch { /* the original failure is the one to report */ }
            if (ex is OperationCanceledException) throw;
            throw new InvalidOperationException(
                $"Preparing the SQLite control-plane database '{connection.DataSource}' failed at {step}; it was left unchanged at " +
                $"{(plan.Recorded is null ? "no schema" : "version " + plan.Recorded)}." +
                (backup is null ? string.Empty : $" A backup taken before the migration is at '{backup}'.") +
                $" {ex.Message}", ex);
        }
        finally
        {
            tx?.Dispose();
            try { await ExecuteAsync(db, null, "PRAGMA foreign_keys = ON", CancellationToken.None); } catch { /* the connection is discarded next */ }
        }
    }

    private sealed record SchemaPlan(Version? Recorded, IReadOnlyList<SqliteMigration>? Chain, bool IsNothing);

    /// <summary>What to do for a file that records <paramref name="recorded"/>; throws on a newer version or a missing step.</summary>
    private static SchemaPlan Plan(SqliteSchemaDefinition definition, SqliteControlPlaneConnection connection, Version? recorded)
    {
        if (recorded is null) return new SchemaPlan(null, null, false);
        if (recorded == definition.Target) return new SchemaPlan(recorded, null, true);
        if (recorded > definition.Target)
            throw new InvalidOperationException(new SqliteSchemaCheck(SqliteSchemaState.Newer, connection.DataSource, recorded.ToString(), definition.TargetVersion).Message);

        var chain = definition.ChainFrom(recorded, out var problem);
        if (chain is null)
            throw new InvalidOperationException(
                $"The SQLite control-plane database '{connection.DataSource}' is at schema version {recorded}, older than this build's {definition.TargetVersion}, " +
                $"and cannot be migrated: {problem}.");
        return new SchemaPlan(recorded, chain, false);
    }

    /// <summary>The highest version row, or null when the table is missing or empty.</summary>
    private static async Task<Version?> ReadRecordedVersionAsync(SqliteConnection db, SqliteTransaction? tx, CancellationToken ct)
    {
        await using (var exists = Command(db, tx, "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = @name"))
        {
            exists.Parameters.AddWithValue("@name", VersionTable);
            if (await exists.ExecuteScalarAsync(ct) is null) return null;
        }

        Version? highest = null;
        var rows = 0;
        await using var versions = Command(db, tx, $"SELECT version FROM {VersionTable}");
        await using var reader = await versions.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows++;
            if (Version.TryParse(reader.GetString(0), out var version) && (highest is null || version > highest)) highest = version;
        }
        if (rows > 0 && highest is null)
            throw new InvalidOperationException($"{VersionTable} holds rows but none is a version number; this is not a database this build created.");
        return highest;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return command;
    }

    private static async Task ExecuteAsync(
        SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(db, tx, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct);
    }
}
