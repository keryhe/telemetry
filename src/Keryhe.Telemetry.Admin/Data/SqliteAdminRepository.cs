using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Keryhe.Telemetry.Admin.Data;

/// <summary>
/// The SQLite control-plane administrator. It opens the file without permission to create it (Microsoft.Data.Sqlite creates a missing
/// file by default) and checks the schema version first (<see cref="SqliteSchemaGate"/>): only the API host creates or migrates the
/// database. Foreign keys are forced on so deleting a tenant cascades, and timestamps are written in the control plane's one UTC text
/// layout (<c>yyyy-MM-dd HH:mm:ss.fffffff</c>).
/// </summary>
public sealed class SqliteAdminRepository : AdminRepositoryBase
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

    private readonly string _dataSource;
    private readonly string _connectionString;

    public SqliteAdminRepository(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathRooted(builder.DataSource))
            throw new InvalidOperationException(
                "ConnectionStrings:Admin must name the SQLite database file by an absolute path (for example 'Data Source=/var/lib/keryhe/control-plane.db'), the same file the hosts use.");

        _dataSource = Path.GetFullPath(builder.DataSource);
        builder.DataSource = _dataSource;
        builder.Mode = SqliteOpenMode.ReadWrite; // never ReadWriteCreate
        builder.ForeignKeys = true;
        if (!builder.ContainsKey("Default Timeout")) builder.DefaultTimeout = 30;
        _connectionString = builder.ConnectionString;
    }

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public override async Task CheckConnectivityAsync(CancellationToken ct)
    {
        var problem = await SqliteSchemaGate.CheckAsync(_dataSource, _connectionString, ct);
        if (problem is not null) throw new InvalidOperationException(problem);
        await base.CheckConnectivityAsync(ct);
    }

    protected override string InsertTenantSql =>
        "INSERT INTO tenants (name) VALUES (@name) RETURNING id";

    protected override string InsertApiKeySql =>
        "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt) RETURNING id";

    protected override object? ExpiresAtParam(DateTime? expiresAtUtc) =>
        expiresAtUtc is { } at
            ? (at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at).ToString(TimestampFormat, CultureInfo.InvariantCulture)
            : null;

    // SQLITE_CONSTRAINT (19) with the extended code SQLITE_CONSTRAINT_UNIQUE (2067): uk_tenant_name.
    protected override bool IsUniqueViolation(DbException ex) =>
        ex is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 };

    public override (string Server, string Database) DescribeTarget() => ("(local file)", _dataSource);

    // The control-plane defaults are UTC (strftime 'now'), and Dapper hands the text back as DateTime.
    public override string CreatedAtColumnHeader => "Created (UTC)";
}
