using Keryhe.Telemetry.Sqlite;
using Microsoft.Data.Sqlite;

namespace Keryhe.Telemetry.IntegrationTests.Sqlite;

/// <summary>A SQLite control-plane file in its own temp directory, with helpers for raw SQL against it (no pooling, so nothing keeps the file open).</summary>
internal sealed class SqliteTestDb : IDisposable
{
    public SqliteTestDb(bool createDirectory = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "keryhe-sqlite-test-" + Guid.NewGuid().ToString("N"));
        if (createDirectory) System.IO.Directory.CreateDirectory(Directory);
        File = Path.Combine(Directory, "control-plane.db");
    }

    public string Directory { get; }
    public string File { get; }
    public string ConnectionString => $"Data Source={File}";
    public SqliteControlPlaneConnection Connection => new(ConnectionString);

    private SqliteConnection Open(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(Connection.ConnectionString(mode, pooling: false, foreignKeys: true));
        connection.Open();
        return connection;
    }

    public async Task ExecAsync(string sql)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        await using var connection = Open(mode);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    public async Task<List<string>> ColumnAsync(string sql)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }

    public Task<List<string>> VersionsAsync() => ColumnAsync("SELECT version FROM control_plane_schema_version ORDER BY version");

    public Task<List<string>> ColumnsOfAsync(string table) => ColumnAsync($"SELECT name FROM pragma_table_info('{table}')");

    public Task<bool> HasTableAsync(string table) =>
        ScalarAsync<long>($"SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = '{table}'").ContinueWith(t => t.Result > 0);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
