using System.Data.Common;
using Npgsql;

namespace Keryhe.Telemetry.Admin.Data;

/// <summary>
/// Covers both PostgreSQL and Timescale — Timescale is Postgres, and this tool never touches
/// anything Timescale-specific (hypertables, compression, retention policies), so one
/// implementation serves both. See plans/admin-tui.md, sections 1 and 5.
/// </summary>
public sealed class NpgsqlAdminRepository(string connectionString) : AdminRepositoryBase
{
    private readonly NpgsqlConnectionStringBuilder _builder = new(connectionString);

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    protected override string InsertTenantSql =>
        "INSERT INTO tenants (name) VALUES (@name) RETURNING id";

    protected override string InsertApiKeySql =>
        "INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (@tenantId, @keyHash, @name) RETURNING id";

    protected override bool IsUniqueViolation(DbException ex) =>
        ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public override (string Server, string Database) DescribeTarget() =>
        (_builder.Host ?? "(unspecified)", _builder.Database ?? "(unspecified)");

    // Npgsql returns TIMESTAMPTZ as DateTime with Kind = Utc.
    public override string CreatedAtColumnHeader => "Created (UTC)";
}
