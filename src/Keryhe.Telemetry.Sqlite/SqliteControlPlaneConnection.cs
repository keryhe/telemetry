using Keryhe.Telemetry.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Sqlite;

/// <summary>
/// The SQLite control-plane connection (<c>ConnectionStrings:ControlPlane</c>), parsed and validated once at registration.
/// Every repository and startup service opens its connections from here, so no class reads configuration itself and the
/// settings that must hold for the file to work are applied in one place:
/// <list type="bullet">
/// <item>The data source must be an <b>absolute path</b>: the collector and the API have different working directories, so a
/// relative path (or <c>:memory:</c>) would open different databases without any error.</item>
/// <item><c>Foreign Keys=True</c> and <c>Pooling=True</c> whatever the configured string says: <c>ON DELETE CASCADE</c> only works
/// with foreign keys on, which SQLite turns off for each new connection.</item>
/// <item><c>Default Timeout</c> (the busy wait) is 30 s unless the string sets it: writers serialize on the file's write lock, and a
/// busy statement should wait rather than fail.</item>
/// </list>
/// The open mode is chosen by the caller, never taken from the configured string: Microsoft.Data.Sqlite creates a missing file by
/// default, and only the API's schema step may do that.
/// </summary>
public sealed class SqliteControlPlaneConnection
{
    /// <summary>The busy wait used when the connection string does not set <c>Default Timeout</c>.</summary>
    public const int DefaultBusyTimeoutSeconds = 30;

    private readonly SqliteConnectionStringBuilder _builder;

    public SqliteControlPlaneConnection(string connectionString)
    {
        SqliteConnectionStringBuilder builder;
        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "ConnectionStrings:ControlPlane is not a valid SQLite connection string; expected 'Data Source=<absolute path to the database file>'.", ex);
        }

        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource)
            || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || builder.Mode == SqliteOpenMode.Memory
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(dataSource))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:ControlPlane must name the SQLite database file by an absolute path (for example 'Data Source=/var/lib/keryhe/control-plane.db'): " +
                "the collector and the API run from different directories, so a relative path or ':memory:' would open different databases.");
        }

        if (!builder.ContainsKey("Default Timeout")) builder.DefaultTimeout = DefaultBusyTimeoutSeconds;
        builder.Cache = SqliteCacheMode.Default;
        builder.ForeignKeys = true;
        builder.Pooling = true;
        _builder = builder;
        DataSource = Path.GetFullPath(dataSource);
    }

    /// <summary>The database file's absolute path.</summary>
    public string DataSource { get; }

    /// <summary>Reads <c>ConnectionStrings:ControlPlane</c>; throws, naming the key, when it is missing, blank or not an absolute file path.</summary>
    public static SqliteControlPlaneConnection FromConfiguration(IConfiguration configuration) =>
        new(ControlPlaneConnection.FromConfiguration(configuration).ConnectionString);

    /// <summary>The connection string for <paramref name="mode"/> (pooled, foreign keys on, unless overridden).</summary>
    public string ConnectionString(SqliteOpenMode mode = SqliteOpenMode.ReadWrite, bool pooling = true, bool foreignKeys = true)
    {
        var builder = new SqliteConnectionStringBuilder(_builder.ConnectionString)
        {
            Mode = mode,
            Pooling = pooling,
            ForeignKeys = foreignKeys
        };
        return builder.ConnectionString;
    }

    /// <summary>A new, closed connection to the file. The default <c>ReadWrite</c> mode never creates a missing file.</summary>
    public SqliteConnection Create(SqliteOpenMode mode = SqliteOpenMode.ReadWrite) => new(ConnectionString(mode));

    /// <summary>An open connection to the file (see <see cref="Create"/>).</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = Create(mode);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A connection outside the pool whose foreign-key setting the caller controls (the schema step switches it off around a
    /// migration, which a pooled connection's open-time pragma would undo).
    /// </summary>
    internal SqliteConnection CreateDedicated(SqliteOpenMode mode) => new(ConnectionString(mode, pooling: false, foreignKeys: false));
}
