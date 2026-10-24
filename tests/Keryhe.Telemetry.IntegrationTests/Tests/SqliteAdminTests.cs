using Keryhe.Telemetry.Admin.Data;
using Keryhe.Telemetry.IntegrationTests.Sqlite;
using Keryhe.Telemetry.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The Admin tool's SQLite repository against a real control-plane file: tenants and keys through its six operations, the unique
/// violation, the cascade, and the version gate it copies from the library (it never creates or migrates the database).
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class SqliteAdminTests
{
    private static async Task<(SqliteTestDb Db, SqliteAdminRepository Admin)> OpenAsync()
    {
        var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, NullLogger.Instance);
        return (db, new SqliteAdminRepository(db.ConnectionString));
    }

    [Fact]
    public void The_copied_target_version_is_the_librarys()
    {
        Assert.Equal(SqliteControlPlaneSchema.TargetVersion, SqliteSchemaGate.TargetVersion);
    }

    [Fact]
    public async Task Tenants_and_keys_round_trip()
    {
        var (db, admin) = await OpenAsync();
        using var _ = db;
        await admin.CheckConnectivityAsync(CancellationToken.None);

        var tenantId = await admin.CreateTenantAsync("acme", CancellationToken.None);
        var expires = DateTime.UtcNow.AddDays(30);
        var forever = await admin.CreateApiKeyAsync(tenantId, "forever", "hash-forever", null, CancellationToken.None);
        var dated = await admin.CreateApiKeyAsync(tenantId, "dated", "hash-dated", expires, CancellationToken.None);

        var tenant = Assert.Single(await admin.GetTenantsAsync(CancellationToken.None));
        Assert.Equal(("acme", 2, 2), (tenant.Name, tenant.KeyCount, tenant.ActiveKeyCount));
        Assert.InRange((DateTime.UtcNow - tenant.CreatedAt).TotalSeconds, -2, 10);

        var keys = await admin.GetApiKeysAsync(tenantId, CancellationToken.None);
        Assert.Equal(2, keys.Count);
        var datedKey = keys.Single(k => k.Id == dated);
        Assert.InRange((datedKey.ExpiresAt!.Value - expires).TotalSeconds, -1, 1);
        Assert.Null(keys.Single(k => k.Id == forever).ExpiresAt);
        Assert.True(datedKey.IsActive);
        // Written in the control plane's fixed layout, which the collector's lookup reads.
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{7}$", (await db.ScalarAsync<string>("SELECT expires_at FROM api_keys WHERE name = 'dated'"))!);

        Assert.True(await admin.SetApiKeyActiveAsync(dated, false, CancellationToken.None));
        tenant = Assert.Single(await admin.GetTenantsAsync(CancellationToken.None));
        Assert.Equal(1, tenant.ActiveKeyCount);
        Assert.False((await admin.GetApiKeysAsync(tenantId, CancellationToken.None)).Single(k => k.Id == dated).IsActive);

        Assert.True(await admin.DeleteApiKeyAsync(forever, CancellationToken.None));
        Assert.False(await admin.DeleteApiKeyAsync(forever, CancellationToken.None));
        Assert.Single(await admin.GetApiKeysAsync(tenantId, CancellationToken.None));
    }

    [Fact]
    public async Task A_duplicate_tenant_name_is_a_unique_violation()
    {
        var (db, admin) = await OpenAsync();
        using var _ = db;
        await admin.CreateTenantAsync("dup", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => admin.CreateTenantAsync("dup", CancellationToken.None));
        Assert.Contains("dup", ex.Message);
    }

    [Fact]
    public async Task The_gate_refuses_a_missing_older_and_newer_database_without_creating_or_changing_it()
    {
        using var db = new SqliteTestDb();
        var admin = new SqliteAdminRepository(db.ConnectionString);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CheckConnectivityAsync(CancellationToken.None));
        Assert.Contains("does not exist", missing.Message);
        Assert.False(File.Exists(db.File));

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, NullLogger.Instance);
        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '3.0.0'");
        Assert.Contains("migrate", (await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CheckConnectivityAsync(CancellationToken.None))).Message);

        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '9.0.0'");
        Assert.Contains("newer build", (await Assert.ThrowsAsync<InvalidOperationException>(() => admin.CheckConnectivityAsync(CancellationToken.None))).Message);
        Assert.Equal(["9.0.0"], await db.VersionsAsync());
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => new SqliteAdminRepository("Data Source=control-plane.db"));
    }
}
