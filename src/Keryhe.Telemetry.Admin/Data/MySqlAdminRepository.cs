using System.Data.Common;
using MySqlConnector;

namespace Keryhe.Telemetry.Admin.Data;

public sealed class MySqlAdminRepository(string connectionString) : AdminRepositoryBase
{
    private readonly MySqlConnectionStringBuilder _builder = new(connectionString);

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    protected override string InsertTenantSql =>
        "INSERT INTO tenants (name) VALUES (@name); SELECT LAST_INSERT_ID()";

    protected override string InsertApiKeySql =>
        "INSERT INTO api_keys (tenant_id, key_hash, name, expires_at) VALUES (@tenantId, @keyHash, @name, @expiresAt); SELECT LAST_INSERT_ID()";

    // 1062 = duplicate entry for a unique key (uk_tenant_name).
    protected override bool IsUniqueViolation(DbException ex) =>
        ex is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    public override (string Server, string Database) DescribeTarget() =>
        (string.IsNullOrEmpty(_builder.Server) ? "(unspecified)" : _builder.Server,
         string.IsNullOrEmpty(_builder.Database) ? "(unspecified)" : _builder.Database);

    // created_at defaults to CURRENT_TIMESTAMP(6), the session's local time, and MySqlConnector returns DATETIME(6) as
    // DateTime with Kind = Unspecified: labeled honestly, never converted (as for SQL Server).
    public override string CreatedAtColumnHeader => "Created";
}
