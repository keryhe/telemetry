using System.Diagnostics;
using System.Text.RegularExpressions;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Sqlite;
using Keryhe.Telemetry.Sqlite;
using Keryhe.Telemetry.Sqlite.Services;
using Keryhe.Telemetry.TestInfrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The SQLite control plane's file handling: creating the schema, checking it, the API and collector startup behaviour, the settings
/// the connection forces, the timestamp layout and several processes sharing one file. No Docker.
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class SqliteSchemaTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Fact]
    public async Task A_fresh_file_gets_every_table_the_version_and_wal()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);

        foreach (var table in new[] { "tenants", "api_keys", "alert_rules", "alert_events", "control_plane_schema_version" })
            Assert.True(await db.HasTableAsync(table), table);
        Assert.Equal([SqliteControlPlaneSchema.TargetVersion], await db.VersionsAsync());
        Assert.Equal("wal", await db.ScalarAsync<string>("PRAGMA journal_mode"));
        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        var applied = await db.ScalarAsync<string>("SELECT applied_at FROM control_plane_schema_version");
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('keep-me')");
        await Task.Delay(30);

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);

        Assert.Equal(applied, await db.ScalarAsync<string>("SELECT applied_at FROM control_plane_schema_version"));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM control_plane_schema_version"));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
    }

    [Fact]
    public async Task Running_the_create_script_twice_is_harmless()
    {
        using var db = new SqliteTestDb();
        await db.ExecAsync(SqliteControlPlaneSchema.CreateScript);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('t')");
        await db.ExecAsync(SqliteControlPlaneSchema.CreateScript);
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
        Assert.Equal([SqliteControlPlaneSchema.TargetVersion], await db.VersionsAsync());
    }

    [Fact]
    public async Task A_newer_recorded_version_fails_naming_both_and_changes_nothing()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO control_plane_schema_version (version) VALUES ('9.1.0')");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log));
        Assert.Contains("9.1.0", ex.Message);
        Assert.Contains(SqliteControlPlaneSchema.TargetVersion, ex.Message);
        Assert.Equal(SqliteSchemaState.Newer, (await SqliteControlPlaneSchema.CheckAsync(db.Connection)).State);
        Assert.Empty(Directory.GetFiles(db.Directory, "*.bak-*"));
    }

    [Fact]
    public async Task An_older_version_with_no_migration_chain_fails_before_touching_the_file()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '3.9.0'");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log));
        Assert.Contains("3.9.0", ex.Message);
        Assert.Contains("no migration from version 3.9.0", ex.Message);
        Assert.Equal(["3.9.0"], await db.VersionsAsync());
        Assert.Empty(Directory.GetFiles(db.Directory, "*.bak-*"));
    }

    [Fact]
    public async Task A_missing_directory_is_created()
    {
        using var db = new SqliteTestDb(createDirectory: false);
        Assert.False(Directory.Exists(db.Directory));

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);

        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);
    }

    // ---- the fixed tenant ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_fixed_tenant_is_created_on_a_new_file_and_a_second_run_changes_nothing()
    {
        using var db = new SqliteTestDb(createDirectory: false);

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log, ensureTenantId: 5);
        var created = await db.ScalarAsync<string>("SELECT created_at FROM tenants WHERE id = 5");
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log, ensureTenantId: 5);

        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
        Assert.Equal("Default", await db.ScalarAsync<string>("SELECT name FROM tenants WHERE id = 5"));
        Assert.Equal(created, await db.ScalarAsync<string>("SELECT created_at FROM tenants WHERE id = 5"));
    }

    [Fact]
    public async Task A_fixed_tenant_is_added_to_a_current_file_and_later_tenants_get_higher_ids()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('first')");

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log, ensureTenantId: 40);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('later')");

        Assert.Equal("Default", await db.ScalarAsync<string>("SELECT name FROM tenants WHERE id = 40"));
        Assert.Equal(41, await db.ScalarAsync<long>("SELECT id FROM tenants WHERE name = 'later'"));
        Assert.Equal([SqliteControlPlaneSchema.TargetVersion], await db.VersionsAsync());
    }

    [Fact]
    public async Task An_existing_tenant_with_the_fixed_id_keeps_its_name()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('acme')");

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log, ensureTenantId: 1);

        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
        Assert.Equal("acme", await db.ScalarAsync<string>("SELECT name FROM tenants WHERE id = 1"));
    }

    [Fact]
    public async Task Several_instances_starting_together_leave_one_version_row()
    {
        using var db = new SqliteTestDb();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log))));
        Assert.Equal([SqliteControlPlaneSchema.TargetVersion], await db.VersionsAsync());
        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);
    }

    [Fact]
    public async Task The_check_reports_each_state_and_never_creates_the_file()
    {
        using var db = new SqliteTestDb();
        var missing = await SqliteControlPlaneSchema.CheckAsync(db.Connection);
        Assert.Equal(SqliteSchemaState.MissingFile, missing.State);
        Assert.Contains("API host", missing.Message);
        Assert.False(File.Exists(db.File));

        await File.WriteAllBytesAsync(db.File, []);
        Assert.Equal(SqliteSchemaState.NotInitialized, (await SqliteControlPlaneSchema.CheckAsync(db.Connection)).State);

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        Assert.Equal(SqliteSchemaState.Current, (await SqliteControlPlaneSchema.CheckAsync(db.Connection)).State);

        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '3.0.0'");
        var older = await SqliteControlPlaneSchema.CheckAsync(db.Connection);
        Assert.Equal(SqliteSchemaState.Older, older.State);
        Assert.Contains("migrate", older.Message);
    }

    // ---- hosted services -----------------------------------------------------------------------------------------------

    private static ServiceProvider Host(string file, bool collector, bool? applySchema = null, long? fixedTenantId = null)
    {
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:ControlPlane"] = $"Data Source={file}" };
        if (applySchema is { } apply) settings["Telemetry:ControlPlane:Sqlite:ApplySchema"] = apply.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new TestTenantContext()); // the API registers the real one
        if (collector) services.AddSqliteApiKeyLookup(configuration, fixedTenantId);
        else services.AddSqliteControlPlaneManagement(configuration, fixedTenantId);
        // As a Development host builds it: every registration must be resolvable, and no scoped service held by a singleton.
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static async Task StartingAsync(ServiceProvider host)
    {
        foreach (var service in host.GetServices<IHostedService>().OfType<IHostedLifecycleService>())
            await service.StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_api_host_creates_the_database_at_startup()
    {
        using var db = new SqliteTestDb();
        await using var host = Host(db.File, collector: false);
        await StartingAsync(host);
        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_host_creates_a_missing_file_and_directory_and_its_fixed_tenant(bool collector)
    {
        using var db = new SqliteTestDb(createDirectory: false);
        await using var host = Host(db.File, collector, fixedTenantId: 3);

        await StartingAsync(host);

        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);
        Assert.Equal("Default", await db.ScalarAsync<string>("SELECT name FROM tenants WHERE id = 3"));
    }

    [Fact]
    public async Task Without_a_fixed_tenant_no_tenant_is_created()
    {
        using var db = new SqliteTestDb();
        await using var host = Host(db.File, collector: true);
        await StartingAsync(host);
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
    }

    [Fact]
    public async Task A_collector_and_an_api_host_starting_together_leave_one_version_row_and_one_tenant()
    {
        using var db = new SqliteTestDb(createDirectory: false);
        await using var collector = Host(db.File, collector: true, fixedTenantId: 2);
        await using var api = Host(db.File, collector: false, fixedTenantId: 2);

        await Task.WhenAll(Task.Run(() => StartingAsync(collector)), Task.Run(() => StartingAsync(api)));

        Assert.Equal([SqliteControlPlaneSchema.TargetVersion], await db.VersionsAsync());
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
    }

    [Fact]
    public async Task The_schema_step_runs_before_hosted_services_registered_ahead_of_it()
    {
        var services = new ServiceCollection();
        services.AddHostedService<NoopHostedService>();
        services.AddSqliteApiKeyLookup(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlPlane"] = $"Data Source={Path.Combine(Path.GetTempPath(), "unused.db")}"
        }).Build());

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Equal(typeof(SqliteControlPlaneInitializer), hosted[0].ImplementationType);
        Assert.Equal(typeof(NoopHostedService), hosted[1].ImplementationType);
    }

    private sealed class NoopHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_collector_creates_an_empty_file_fails_on_another_version_and_starts_on_a_current_one()
    {
        using var db = new SqliteTestDb();
        await File.WriteAllBytesAsync(db.File, []);
        await using (var host = Host(db.File, collector: true))
            await StartingAsync(host);
        Assert.True((await SqliteControlPlaneSchema.CheckAsync(db.Connection)).IsCurrent);

        await using (var host = Host(db.File, collector: true))
            await StartingAsync(host);

        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '3.0.0'");
        await using (var host = Host(db.File, collector: true))
            Assert.Contains("migrate", (await Assert.ThrowsAsync<InvalidOperationException>(() => StartingAsync(host))).Message);

        await db.ExecAsync("UPDATE control_plane_schema_version SET version = '9.0.0'");
        await using (var host = Host(db.File, collector: true))
            Assert.Contains("newer", (await Assert.ThrowsAsync<InvalidOperationException>(() => StartingAsync(host))).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplySchema_off_makes_either_host_only_check_and_create_no_tenant(bool collector)
    {
        using var db = new SqliteTestDb();
        await using (var host = Host(db.File, collector, applySchema: false, fixedTenantId: 1))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartingAsync(host));
            Assert.Contains("does not exist", ex.Message);
            Assert.False(File.Exists(db.File));
        }

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await using (var host = Host(db.File, collector, applySchema: false, fixedTenantId: 1))
            await StartingAsync(host);
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT COUNT(*) FROM tenants"));
    }

    [Fact]
    public void A_bad_ApplySchema_value_fails_registration_naming_the_key()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ControlPlane"] = "Data Source=/tmp/keryhe-apply-schema-test.db",
            ["Telemetry:ControlPlane:Sqlite:ApplySchema"] = "maybe"
        }).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSqliteControlPlaneManagement(configuration));
        Assert.Contains("Telemetry:ControlPlane:Sqlite:ApplySchema", ex.Message);
    }

    [Theory]
    [InlineData("Data Source=relative/control-plane.db")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=")]
    [InlineData("not a connection string")]
    public void A_relative_memory_or_malformed_data_source_fails_registration(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:ControlPlane"] = connectionString }).Build();
        foreach (var register in new Action<IServiceCollection>[]
                 { s => s.AddSqliteApiKeyLookup(configuration), s => s.AddSqliteControlPlaneManagement(configuration) })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => register(new ServiceCollection()));
            Assert.Contains("ConnectionStrings:ControlPlane", ex.Message);
        }
    }

    // ---- connection settings -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Foreign_keys_are_on_even_when_the_configured_string_says_off()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        var connection = new SqliteControlPlaneConnection($"Data Source={db.File};Foreign Keys=False;Pooling=False");

        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('t'); INSERT INTO api_keys (tenant_id, key_hash, name) VALUES (1, 'h', 'k')");
        await using (var conn = await connection.OpenAsync(CancellationToken.None))
        {
            await using var command = conn.CreateCommand();
            command.CommandText = "DELETE FROM tenants WHERE id = 1";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(0, await db.ScalarAsync<long>("SELECT COUNT(*) FROM api_keys"));
    }

    [Fact]
    public async Task A_deleted_tenants_id_is_never_reused()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('a'), ('b')");
        var highest = await db.ScalarAsync<long>("SELECT MAX(id) FROM tenants");
        await db.ExecAsync($"DELETE FROM tenants WHERE id = {highest}");
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('c')");
        Assert.True(await db.ScalarAsync<long>("SELECT MAX(id) FROM tenants") > highest);
    }

    [Fact]
    public async Task Column_checks_hold_the_same_rows_valid_as_the_other_providers()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('t')");
        await Assert.ThrowsAsync<SqliteException>(() => db.ExecAsync($"INSERT INTO tenants (name) VALUES ('{new string('x', 256)}')"));
        await Assert.ThrowsAsync<SqliteException>(() => db.ExecAsync("INSERT INTO alert_rules (tenant_id, name, type, condition_json, webhook_url) VALUES (1, 'n', 'ErrorRate', 'not json', 'u')"));
        await Assert.ThrowsAsync<SqliteException>(() => db.ExecAsync("INSERT INTO api_keys (tenant_id, key_hash, name, is_active) VALUES (1, 'h', 'k', 2)"));
        await Assert.ThrowsAsync<SqliteException>(() => db.ExecAsync("INSERT INTO tenants (name) VALUES ('t')"));
    }

    // ---- timestamps ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Timestamps_are_written_in_one_fixed_layout_in_utc()
    {
        var instant = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("2026-10-24 12:00:00.0000000", SqliteTime.ToColumn(instant));
        Assert.Equal("2026-10-24 12:00:00.1234567", SqliteTime.ToColumn(instant.AddTicks(1_234_567)));
        // An offset is converted to UTC; no "+05:00" ever reaches a column.
        Assert.Equal("2026-10-24 07:00:00.0000000", SqliteTime.ToColumn(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(5))));
        Assert.Equal(instant.AddTicks(1_234_567), SqliteTime.FromColumn(SqliteTime.ToColumn(instant.AddTicks(1_234_567))));
        Assert.Equal(DateTimeKind.Utc, SqliteTime.FromColumn("2026-10-24 12:00:00.123").Kind);
    }

    [Fact]
    public async Task A_column_default_has_the_same_layout_as_a_value_written_from_dotnet()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('t')");
        var stored = await db.ScalarAsync<string>("SELECT created_at FROM tenants");
        Assert.Matches(new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{7}$"), stored!);
        Assert.Equal(SqliteTime.ToColumn(DateTime.UtcNow).Length, stored!.Length);
        Assert.InRange((DateTime.UtcNow - SqliteTime.FromColumn(stored)).TotalSeconds, -2, 5);
    }

    [Fact]
    public async Task The_cooldown_claim_refuses_inside_the_window_and_claims_after_it()
    {
        using var db = new SqliteTestDb();
        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name) VALUES ('t')");
        var tenantContext = new TestTenantContext();
        tenantContext.SetTenantId(1);
        var rules = new SqliteAlertRuleRepository(db.Connection, tenantContext);
        var rule = await rules.CreateRuleAsync(new Keryhe.Telemetry.Core.Models.AlertRule
        {
            Name = "r", Type = Keryhe.Telemetry.Core.Models.AlertRuleType.ErrorRate, ConditionJson = "{}", WebhookUrl = "https://example.test", CooldownMinutes = 1, Enabled = true
        });

        Assert.True(await rules.TryClaimFireAsync(rule.Id, 1, cooldownMinutes: 1));
        Assert.False(await rules.TryClaimFireAsync(rule.Id, 1, cooldownMinutes: 1));

        await db.ExecAsync($"UPDATE alert_rules SET last_fired_at = '{SqliteTime.ToColumn(DateTime.UtcNow.AddSeconds(-59))}'");
        Assert.False(await rules.TryClaimFireAsync(rule.Id, 1, cooldownMinutes: 1));
        await db.ExecAsync($"UPDATE alert_rules SET last_fired_at = '{SqliteTime.ToColumn(DateTime.UtcNow.AddSeconds(-61))}'");
        Assert.True(await rules.TryClaimFireAsync(rule.Id, 1, cooldownMinutes: 1));
    }

    // ---- the collector's retention cache ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_retention_cache_reads_a_sqlite_control_plane_read_only_and_never_creates_the_file()
    {
        using var db = new SqliteTestDb();
        var missing = new RetentionWindowCache(Config(db.File), 60, TimeProvider.System, NullLogger<RetentionWindowCache>.Instance);
        await missing.RefreshAsync(CancellationToken.None);
        Assert.Null(missing.OldestAllowedUtc("traces", 1));
        Assert.False(File.Exists(db.File));

        await SqliteControlPlaneSchema.EnsureCurrentAsync(db.Connection, Log);
        await db.ExecAsync("INSERT INTO tenants (name, trace_retention_days) VALUES ('t', 7)");
        var cache = new RetentionWindowCache(Config(db.File), 60, TimeProvider.System, NullLogger<RetentionWindowCache>.Instance);
        await cache.RefreshAsync(CancellationToken.None);
        var cutoff = cache.OldestAllowedUtc("traces", 1);
        Assert.NotNull(cutoff);
        Assert.InRange((DateTime.UtcNow.AddDays(-7) - cutoff!.Value).TotalMinutes, -1, 1);

        static IConfiguration Config(string file) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ProviderKeys.CollectorControlPlane] = "Sqlite",
            ["ConnectionStrings:ControlPlane"] = $"Data Source={file}"
        }).Build();
    }

    // ---- several processes -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The single-machine promise: another process (the sqlite3 CLI) writes the file in a loop while this one does the collector's
    /// lookup and touch and the API's claim. Nothing may fail with SQLITE_BUSY past the busy wait, and both sides' writes land.
    /// Needs the sqlite3 CLI; without it the test has nothing to run against and returns.
    /// </summary>
    [Fact]
    public async Task Another_process_writing_the_file_does_not_disturb_the_collector_and_api_operations()
    {
        var sqlite3 = new[] { "/usr/bin/sqlite3", "/usr/local/bin/sqlite3", "/opt/homebrew/bin/sqlite3" }.FirstOrDefault(File.Exists);
        if (sqlite3 is null) return;

        await using var cp = new SqliteControlPlane();
        await cp.StartAsync();
        const string key = "multi-process-key";
        var hash = Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash(key);
        var tenantId = await cp.InsertTenantWithHashedKeyAsync("multi", "k", hash, null);
        // The other process writes its own tenant's row, so both sides' final values can be checked.
        var otherTenantId = await cp.InsertTenantWithHashedKeyAsync("other", "k", "other-hash", null);

        const int iterations = 60;
        var script = $"for i in $(seq 1 {iterations}); do \"{sqlite3}\" \"{cp.FilePath}\" \".timeout 30000\" \"UPDATE tenants SET trace_retention_days = $i WHERE id = {otherTenantId};\" >/dev/null || exit 1; done";
        using var other = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", script]) { RedirectStandardError = true })!;

        var lookup = new SqliteApiKeyLookup(cp.Connection);
        var touch = new SqliteApiKeyTouchStore(cp.Connection);
        var settings = new SqliteRetentionSettingsRepository(cp.Connection);
        var tenantContext = new TestTenantContext();
        tenantContext.SetTenantId(tenantId);
        var rules = new SqliteAlertRuleRepository(cp.Connection, tenantContext);
        var rule = await rules.CreateRuleAsync(new Keryhe.Telemetry.Core.Models.AlertRule
        {
            Name = "r", Type = Keryhe.Telemetry.Core.Models.AlertRuleType.ErrorRate, ConditionJson = "{}", WebhookUrl = "https://example.test", CooldownMinutes = 0, Enabled = true
        });

        var ours = 0;
        while (!other.HasExited || ours < iterations)
        {
            Assert.NotNull(await lookup.LookupAsync(hash, CancellationToken.None));
            await touch.TouchAsync([hash], CancellationToken.None);
            await settings.UpdateSettingsAsync(tenantId, new Keryhe.Telemetry.Core.Models.RetentionSettings { TraceRetentionDays = 30, LogRetentionDays = 30, MetricRetentionDays = 30 });
            await rules.TryClaimFireAsync(rule.Id, tenantId, cooldownMinutes: 0);
            ours++;
            if (ours > 5000) break;
        }
        await other.WaitForExitAsync();
        Assert.True(other.ExitCode == 0, await other.StandardError.ReadToEndAsync());

        // Both sides' writes landed.
        Assert.Equal(iterations, (await settings.GetSettingsAsync(otherTenantId))!.TraceRetentionDays);
        Assert.Equal(30, (await settings.GetSettingsAsync(tenantId))!.TraceRetentionDays);
        Assert.NotNull(await cp.ScalarAsync("SELECT last_used_at FROM api_keys WHERE key_hash = '" + hash + "'"));
        Assert.NotNull(await cp.ScalarAsync("SELECT last_fired_at FROM alert_rules WHERE id = " + rule.Id));
    }
}
