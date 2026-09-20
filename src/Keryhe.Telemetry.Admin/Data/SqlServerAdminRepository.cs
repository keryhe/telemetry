using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Keryhe.Telemetry.Admin.Data;

public sealed class SqlServerAdminRepository(string connectionString) : AdminRepositoryBase
{
    private readonly SqlConnectionStringBuilder _builder = new(connectionString);

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    protected override string InsertTenantSql =>
        "INSERT INTO tenants (name) OUTPUT INSERTED.id VALUES (@name)";

    protected override string InsertApiKeySql =>
        "INSERT INTO api_keys (tenant_id, key_hash, name) OUTPUT INSERTED.id VALUES (@tenantId, @keyHash, @name)";

    // 2627 = PK/unique index violation, 2601 = duplicate key on a unique index. Both surface here
    // because uk_tenant_name is declared as a UNIQUE constraint, which SQL Server backs with a
    // unique index — either number is possible depending on how the engine reports it.
    protected override bool IsUniqueViolation(DbException ex) =>
        ex is SqlException sqlEx && sqlEx.Errors.Cast<SqlError>().Any(e => e.Number is 2627 or 2601);

    public override (string Server, string Database) DescribeTarget() =>
        (_builder.DataSource ?? "(unspecified)", _builder.InitialCatalog ?? "(unspecified)");

    // SYSDATETIME() (this schema's default for created_at) is server-local time, and
    // Microsoft.Data.SqlClient returns DATETIME2 as DateTime with Kind = Unspecified — never
    // converted to local/UTC here, only labeled honestly. See plans/admin-tui.md, section 5.4.
    public override string CreatedAtColumnHeader => "Created";
}
