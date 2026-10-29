using Keryhe.Telemetry.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keryhe.Telemetry.TestInfrastructure.Sqlite;

/// <summary>
/// A SQLite control plane in a directory: no server, no container. The schema is applied through
/// <see cref="SqliteControlPlaneSchema.EnsureCurrentAsync"/>, the same call the API host makes at startup, and tenants and API keys
/// are seeded straight into the file. Used by the Parquet container (so Parquet runs need no Docker) and by the SQLite
/// control-plane tests.
/// </summary>
public sealed class SqliteControlPlane : IAsyncDisposable
{
    /// <summary>The <c>Telemetry:{Collector|Api}:Provider:ControlPlane</c> value.</summary>
    public const string ProviderName = "Sqlite";

    private readonly string _directory;
    private readonly bool _ownsDirectory;

    /// <param name="directory">Where the database file lives; null creates (and later deletes) a fresh temp directory.</param>
    public SqliteControlPlane(string? directory = null)
    {
        _ownsDirectory = directory is null;
        _directory = directory ?? Path.Combine(Path.GetTempPath(), "keryhe-sqlite-cp-" + Guid.NewGuid().ToString("N"));
        FilePath = Path.GetFullPath(Path.Combine(_directory, "control-plane.db"));
    }

    /// <summary>The database file's absolute path.</summary>
    public string FilePath { get; }

    /// <summary>The value for <c>ConnectionStrings:ControlPlane</c>.</summary>
    public string ConnectionString => $"Data Source={FilePath}";

    public SqliteControlPlaneConnection Connection => new(ConnectionString);

    /// <summary>Creates the directory and the database with the real schema.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await SqliteControlPlaneSchema.EnsureCurrentAsync(Connection, NullLogger.Instance, cancellationToken);
    }

    /// <summary>Inserts a tenant and an API key given its hash (optionally expiring at a UTC instant) and returns the tenant id.</summary>
    public async Task<long> InsertTenantWithHashedKeyAsync(
        string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = await Connection.OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction(deferred: false);

        await using var tenant = conn.CreateCommand();
        tenant.Transaction = tx;
        tenant.CommandText = "INSERT INTO tenants (name) VALUES (@name) RETURNING id";
        tenant.Parameters.AddWithValue("@name", tenantName);
        var tenantId = Convert.ToInt64(await tenant.ExecuteScalarAsync(cancellationToken));

        await using var key = conn.CreateCommand();
        key.Transaction = tx;
        key.CommandText = "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt)";
        key.Parameters.AddWithValue("@tenantId", tenantId);
        key.Parameters.AddWithValue("@keyHash", keyHash);
        key.Parameters.AddWithValue("@name", apiKeyName);
        key.Parameters.AddWithValue("@expiresAt", expiresAtUtc is { } expires ? SqliteTime.ToColumn(expires) : DBNull.Value);
        await key.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return tenantId;
    }

    /// <summary>The first column of the first row of <paramref name="sql"/> (null for no row or NULL), for tests that look at the file directly.</summary>
    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var conn = await Connection.OpenAsync(cancellationToken);
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    public ValueTask DisposeAsync()
    {
        // Pooled connections hold the file open (and the -wal/-shm beside it).
        SqliteConnection.ClearAllPools();
        if (_ownsDirectory)
        {
            try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
        }
        return ValueTask.CompletedTask;
    }
}
