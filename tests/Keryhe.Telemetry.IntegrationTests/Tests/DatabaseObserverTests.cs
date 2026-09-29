using System.Data.Common;
using ClickHouse.Client.ADO;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Keryhe.Telemetry.TestInfrastructure.Seeding;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Database observers (stress-test plan, Phase 4). The log parsers are pure; the per-provider cases start a
/// diagnostics container, create real lock contention and a real deadlock, and check that the observers see them,
/// so a wrong DMV/catalog query fails here rather than silently reporting zeros during a stress run.
/// </summary>
public class DatabaseObserverTests
{
    [Fact]
    public void Postgres_deadlock_report_is_extracted_with_its_detail_lines()
    {
        var log = string.Join('\n',
            "2026-09-28 21:10:50.100 UTC [61] LOG:  checkpoint complete",
            "2026-09-28 21:10:50.619 UTC [61] ERROR:  deadlock detected",
            "2026-09-28 21:10:50.619 UTC [61] DETAIL:  Process 61 waits for ShareLock on transaction 745; blocked by process 62.",
            "\tProcess 62 waits for ShareLock on transaction 744; blocked by process 61.",
            "\tProcess 61: UPDATE tenants SET name = name WHERE id = 2",
            "2026-09-28 21:10:50.619 UTC [61] HINT:  See server log for query details.",
            "2026-09-28 21:10:50.619 UTC [61] STATEMENT:  UPDATE tenants SET name = name WHERE id = 2",
            "2026-09-28 21:10:51.000 UTC [61] LOG:  connection received");
        var block = Assert.Single(ServerLogParsers.PostgresDeadlocks(log));
        Assert.Contains("deadlock detected", block);
        Assert.Contains("Process 62 waits", block);
        Assert.Contains("STATEMENT:", block);
        Assert.DoesNotContain("connection received", block);
    }

    [Fact]
    public void Postgres_lock_wait_log_lines_are_picked_out()
    {
        var log = "x LOG:  process 61 still waiting for ShareLock on transaction 745 after 1000.101 ms\n" +
                  "x LOG:  process 61 acquired ShareLock on transaction 745 after 2001.412 ms\n" +
                  "x LOG:  unrelated";
        Assert.Equal(2, ServerLogParsers.PostgresLockWaits(log).Count);
    }

    [Fact]
    public void MySql_deadlock_dump_is_the_run_of_bare_lines_between_timestamped_entries()
    {
        var log = string.Join('\n',
            "2026-09-28T21:38:37.437724Z 0 [System] [MY-010931] [Server] ready for connections.",
            "TRANSACTION 1820, ACTIVE 1 sec starting index read",
            "update x set v=2 where id=1",
            "",
            "TRANSACTION 1819, ACTIVE 2 sec starting index read",
            "update x set v=1 where id=2",
            "2026-09-28T21:38:40.000000Z 12 [Warning] [MY-000000] [Server] later entry");
        var block = Assert.Single(ServerLogParsers.MySqlDeadlocks(log));
        Assert.StartsWith("TRANSACTION 1820", block);
        Assert.Contains("TRANSACTION 1819", block);
        Assert.DoesNotContain("later entry", block);
        Assert.DoesNotContain("ready for connections", block);
    }

    public static IEnumerable<object[]> RelationalProviders() =>
    [
        [ProviderNames.PostgreSql],
        [ProviderNames.Timescale],
        [ProviderNames.SqlServer],
        [ProviderNames.MySql],
    ];

    private static DbConnection Open(string provider, string connectionString, string? applicationName = null)
    {
        DbConnection conn = provider switch
        {
            ProviderNames.PostgreSql or ProviderNames.Timescale => new NpgsqlConnection(connectionString),
            ProviderNames.SqlServer => new SqlConnection(applicationName is null ? connectionString
                : new SqlConnectionStringBuilder(connectionString) { ApplicationName = applicationName }.ConnectionString),
            ProviderNames.MySql => new MySqlConnection(connectionString),
            _ => new ClickHouseConnection(connectionString),
        };
        conn.Open();
        return conn;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            value = await read();
        }
        return value;
    }

    [Theory]
    [MemberData(nameof(RelationalProviders))]
    public async Task Observers_see_a_real_lock_wait_a_real_deadlock_statements_and_tables(string provider)
    {
        await using var container = ProviderContainerFactory.Create(provider);
        await container.StartAsync(new ContainerOptions(Diagnostics: true));
        var tenants = await TenantSeeder.SeedAsync(container, 2);
        var (a, b) = (tenants[0].Id, tenants[1].Id);
        var update = (long id) => $"UPDATE tenants SET name = name WHERE id = {id}";

        await using var session = await DatabaseObserverSession.StartAsync(provider, container);

        // A child-row insert fires a foreign-key check on tenants (on Postgres, a nested SELECT ... FOR KEY SHARE).
        using (var conn = Open(provider, container.ConnectionString))
            await ExecAsync(conn, $"INSERT INTO api_keys (tenant_id, key_hash, name) VALUES ({a}, '{new string('f', 64)}', 'observer-fk')");

        // Contention: one open transaction holds tenant a's row while a second statement waits for it.
        using (var holder = Open(provider, container.ConnectionString))
        using (var waiter = Open(provider, container.ConnectionString))
        {
            await using var holderTx = await holder.BeginTransactionAsync();
            await ExecAsync(holder, update(a), holderTx);
            await using var waiterTx = await waiter.BeginTransactionAsync();
            var blocked = ExecAsync(waiter, update(a), waiterTx);
            await Task.Delay(2500); // long enough for two 1s samples and for Postgres's 1s log_lock_waits
            await holderTx.CommitAsync();
            await blocked;
            await waiterTx.CommitAsync();
        }

        // Deadlock: each transaction holds one tenant and then reaches for the other's.
        using (var one = Open(provider, container.ConnectionString))
        using (var two = Open(provider, container.ConnectionString))
        {
            await using var txOne = await one.BeginTransactionAsync();
            await using var txTwo = await two.BeginTransactionAsync();
            await ExecAsync(one, update(a), txOne);
            await ExecAsync(two, update(b), txTwo);
            var first = Attempt(() => ExecAsync(one, update(b), txOne));
            await Task.Delay(500);
            var second = Attempt(() => ExecAsync(two, update(a), txTwo));
            var results = await Task.WhenAll(first, second);
            Assert.Single(results, failed => failed); // exactly one is chosen as the victim
            await Attempt(() => txOne.RollbackAsync());
            await Attempt(() => txTwo.RollbackAsync());
        }

        // Some engines write deadlock detail (log, ring buffer) a moment after the fact; the session's own
        // StopAsync reads it once, so give it time to land before stopping.
        await Task.Delay(provider == ProviderNames.SqlServer ? 8000 : 2000);
        var observation = await session.StopAsync();

        Assert.True(observation.LockSamples.Count >= 2);
        Assert.DoesNotContain(observation.LockSamples, s => s.Error is not null);
        var wait = observation.LockSamples.SelectMany(s => s.Waits).FirstOrDefault(w => w.BlockedQuery?.Contains("tenants", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(wait);
        Assert.NotNull(wait.BlockingId);
        Assert.NotNull(wait.Resource);
        Assert.True(wait.WaitMs >= 0);

        Assert.Equal(1, observation.Locks.Deadlocks);
        if (provider != ProviderNames.SqlServer)
            Assert.NotEmpty(observation.Locks.DeadlockDetails);
        else
            Assert.NotEmpty(observation.Locks.Artifacts);
        if (provider is ProviderNames.PostgreSql or ProviderNames.Timescale)
            Assert.NotEmpty(observation.Locks.LockWaitLogLines);
        if (provider == ProviderNames.MySql)
            Assert.True(observation.Locks.CounterDeltas["row_lock_waits"] >= 1);

        Assert.Contains(observation.Statements.ByTotal, s => s.Query.Contains("tenants", StringComparison.OrdinalIgnoreCase) && s.Calls > 0 && s.TotalMs >= 0);
        var table = Assert.Single(observation.Tables, t => t.Table == "tenants");
        Assert.True(table.Bytes > 0);
        Assert.True(observation.Tables.Count >= 10);
        if (!table.RowsApproximate) Assert.Equal(2, table.Rows);

        Assert.NotEmpty(observation.ContainerStats);
        Assert.All(observation.ContainerStats, c => Assert.True(c.MemoryBytes > 0));

        if (provider == ProviderNames.Timescale)
            Assert.Contains(observation.LockSamples.SelectMany(s => s.Gauges), g => g.Name == "chunks");

        AssertSettingsAndDiagnostics(provider, observation);
    }

    /// <summary>Effective settings are recorded (decision 8) and every diagnostic section was collected, not errored (database-performance plan, Phase 0).</summary>
    private static void AssertSettingsAndDiagnostics(string provider, DatabaseObservation observation)
    {
        var settings = Assert.IsAssignableFrom<IReadOnlyList<ServerSetting>>(observation.Settings).ToDictionary(x => x.Name, x => x.Value);
        Assert.DoesNotContain("(error)", settings.Keys);
        string[] expected = provider switch
        {
            ProviderNames.PostgreSql or ProviderNames.Timescale => ["server_version", "shared_buffers", "max_wal_size", "checkpoint_timeout", "pg_stat_statements.track"],
            ProviderNames.SqlServer => ["version", "max server memory (MB)", "physical_memory_mb", "recovery_model", "tempdb_data_files"],
            ProviderNames.MySql => ["version", "innodb_buffer_pool_size", "log_bin", "innodb_flush_log_at_trx_commit", "transaction_isolation"],
            _ => ["version", "max_threads", "max_server_memory_usage"],
        };
        Assert.All(expected, name => Assert.Contains(name, settings.Keys));

        var diagnostics = Assert.IsAssignableFrom<IReadOnlyList<DiagnosticSection>>(observation.Diagnostics);
        Assert.NotEmpty(diagnostics);
        Assert.All(diagnostics, d => Assert.True(d.Error is null, $"{d.Name}: {d.Error}"));
        Assert.All(diagnostics, d => Assert.All(d.Rows, r => Assert.Equal(d.Columns.Count, r.Count)));

        if (provider is ProviderNames.PostgreSql or ProviderNames.Timescale)
        {
            Assert.Equal("all", settings["pg_stat_statements.track"]);
            var fk = Assert.Single(diagnostics, d => d.Name.StartsWith("Foreign-key checks"));
            Assert.Contains(fk.Rows, r => r[0]!.Contains("tenants") && long.Parse(r[1]!) >= 1);
            var checkpoints = Assert.Single(diagnostics, d => d.Name == "Checkpoints and WAL");
            Assert.Contains(checkpoints.Rows, r => r[0] == "wal_bytes");
        }
        if (provider == ProviderNames.Timescale)
            Assert.NotEmpty(Assert.Single(diagnostics, d => d.Name == "Timescale background jobs").Rows);
        if (provider == ProviderNames.SqlServer)
            Assert.NotEmpty(Assert.Single(diagnostics, d => d.Name == "spans index usage").Rows);
        if (provider == ProviderNames.MySql)
        {
            Assert.Contains(Assert.Single(diagnostics, d => d.Name.StartsWith("InnoDB buffer pool")).Rows, r => r[0] == "buffer_pool_hit_ratio");
            Assert.Contains(observation.LockSamples.SelectMany(s => s.Gauges), g => g.Name == "history_list_length");
        }
    }

    private static async Task<bool> Attempt(Func<Task> action)
    {
        try { await action(); return false; }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return true; }
    }

    [Fact]
    public async Task SqlServer_check_passes_for_snapshot_readers_and_fails_for_any_other_reader()
    {
        await using var container = ProviderContainerFactory.Create(ProviderNames.SqlServer);
        await container.StartAsync(new ContainerOptions(Diagnostics: true));

        // The check looks at running requests, so each reader holds one open for a few seconds.
        async Task<ObserverCheck> CheckWithReaderAsync(string isolation)
        {
            using var reader = Open(ProviderNames.SqlServer, container.ConnectionString, SqlServerObserver.ReadApplicationName);
            await ExecAsync(reader, $"SET TRANSACTION ISOLATION LEVEL {isolation}");
            await using var session = await DatabaseObserverSession.StartAsync(ProviderNames.SqlServer, container, TimeSpan.FromMilliseconds(300));
            await ExecAsync(reader, "WAITFOR DELAY '00:00:02'");
            return Assert.Single((await session.StopAsync()).Locks.Checks);
        }

        var ok = await CheckWithReaderAsync("SNAPSHOT");
        Assert.Equal(CheckOutcome.Passed, ok.Outcome);
        var bad = await CheckWithReaderAsync("READ COMMITTED");
        Assert.Equal(CheckOutcome.Failed, bad.Outcome);
        Assert.Contains("not under SNAPSHOT", bad.Detail);

        // An idle pooled session at the default level must not read as a violation.
        await using var idleSession = await DatabaseObserverSession.StartAsync(ProviderNames.SqlServer, container, TimeSpan.FromMilliseconds(300));
        using var idle = Open(ProviderNames.SqlServer, container.ConnectionString, SqlServerObserver.ReadApplicationName);
        await Task.Delay(1000);
        var none = Assert.Single((await idleSession.StopAsync()).Locks.Checks);
        Assert.Equal(CheckOutcome.NotChecked, none.Outcome); // unknown, not a failure
        Assert.Contains("not checked", none.Detail);
    }

    [Fact]
    public async Task ClickHouse_observer_reports_parts_pressure_counters_statements_and_table_sizes()
    {
        await using var container = ProviderContainerFactory.Create(ProviderNames.ClickHouse);
        await container.StartAsync(new ContainerOptions(Diagnostics: true));
        await TenantSeeder.SeedAsync(container, 2);

        await using var session = await DatabaseObserverSession.StartAsync(ProviderNames.ClickHouse, container);
        using (var conn = Open(ProviderNames.ClickHouse, container.ConnectionString))
        {
            for (var i = 0; i < 6; i++)
                await ExecAsync(conn, $"INSERT INTO tenants (id, name) VALUES ({100 + i}, 'observer-{i}')");
            await ExecAsync(conn, "ALTER TABLE tenants UPDATE name = 'renamed' WHERE id = 100");
            await Task.Delay(2500);
        }

        var observation = await session.StopAsync();

        Assert.DoesNotContain(observation.LockSamples, s => s.Error is not null);
        var gauges = observation.LockSamples.SelectMany(s => s.Gauges).ToList();
        Assert.Contains(gauges, g => g.Name == "parts_active" && g.Label == "tenants" && g.Value >= 1);
        Assert.Contains(gauges, g => g.Name == "parts_max_per_partition" && g.Label == "tenants");
        Assert.Contains(gauges, g => g.Name == "merges_running");

        Assert.Equal(0, observation.Locks.Deadlocks);
        Assert.Contains("DelayedInserts", observation.Locks.CounterDeltas.Keys);
        Assert.Contains("RejectedInserts", observation.Locks.CounterDeltas.Keys);
        Assert.Contains("TOO_MANY_PARTS", observation.Locks.CounterDeltas.Keys);

        Assert.Equal("system.query_log", observation.Statements.Source);
        Assert.Contains(observation.Statements.ByTotal, s => s.Query.Contains("INSERT INTO tenants") && s.Calls >= 6);
        var table = Assert.Single(observation.Tables, t => t.Table == "tenants");
        Assert.True(table.Rows >= 8);
        Assert.True(table.Bytes > 0);
        Assert.NotEmpty(observation.ContainerStats);

        AssertSettingsAndDiagnostics(ProviderNames.ClickHouse, observation);
        var reads = Assert.Single(observation.Diagnostics!, d => d.Name == "Rows and bytes read per query shape");
        Assert.NotEmpty(reads.Rows);
        Assert.Contains(Assert.Single(observation.Diagnostics!, d => d.Name == "Mutations").Rows, r => r[0] == "tenants");
    }
}
