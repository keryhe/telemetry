using System.Data.Common;
using Dapper;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// <see cref="TimedQuery"/> against each real driver, with a statement that sleeps past a 1 s budget. Two paths, because
/// production reached both:
/// <list type="bullet">
/// <item><b>Token path</b> — the command honours <see cref="TimedQuery"/>'s token, the normal case now that the driver
/// timeout outlasts the budget.</item>
/// <item><b>Driver-timeout path</b> — the command ignores the token and the driver's own command timeout ends it, the
/// way the 3.0.1 stress ramp's queries ended (the budget and the driver timeout were equal then). Each driver throws
/// its own type here — Npgsql an <c>NpgsqlException</c> wrapping a <c>TimeoutException</c>, SqlClient a
/// <c>SqlException</c>, MySqlConnector a <c>MySqlException</c> — which is exactly what the type-matching
/// <see cref="TimedQuery"/> let escape as a 500.</item>
/// </list>
/// Both then open a fresh connection and expect it to answer: after a timeout the aborted connection is never reused.
/// The exception each driver actually threw is written to the test output.
/// </summary>
public abstract class TimedQueryProviderTestsBase(ProviderFixture fixture, ITestOutputHelper output)
{
    /// <summary>
    /// A statement that runs well past the 1 s budget and the 2 s driver timeout, is bounded (so a driver that ignored
    /// both would still finish), and FAILS when interrupted. That last point rules out MySQL's <c>SLEEP()</c> and
    /// ClickHouse's <c>sleep()</c>: interrupted, they return normally, which is not what a real summary query does.
    /// </summary>
    protected abstract string SlowSql { get; }

    protected abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Whether the driver ends a command on its own <c>CommandTimeout</c>. ClickHouse.Client does not (measured: a
    /// ~10 s statement with <c>commandTimeout: 2</c> ran to completion), so on ClickHouse the driver timeout is no
    /// backstop at all and <see cref="TimedQuery"/>'s token is the only thing bounding a summary.
    /// </summary>
    protected virtual bool DriverEnforcesCommandTimeout => true;

    private async Task<DbConnection> OpenAsync()
    {
        var conn = CreateConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task TokenPath_SleepPastTheBudget_IsATimeout_AndAFreshConnectionAnswers()
    {
        Exception? seen = null;
        await using (var conn = await OpenAsync())
        {
            var (_, timedOut) = await TimedQuery.RunAsync(async (commandTimeout, ct) =>
            {
                try { return await conn.ExecuteAsync(new CommandDefinition(SlowSql, commandTimeout: commandTimeout, cancellationToken: ct)); }
                catch (Exception ex) { seen = ex; throw; }
            }, timeoutSeconds: 1);

            Assert.True(timedOut, $"expected a timeout; the statement {(seen == null ? "completed" : "threw " + Describe(seen))}");
        }
        output.WriteLine($"{fixture.ProviderName} token path: {(seen == null ? "(no exception)" : Describe(seen))}");
        await AssertFreshConnectionAnswersAsync();
    }

    [Fact]
    public async Task DriverTimeoutPath_DriversOwnTimeoutException_IsATimeout_AndAFreshConnectionAnswers()
    {
        Exception? seen = null;
        await using (var conn = await OpenAsync())
        {
            var (_, timedOut) = await TimedQuery.RunAsync(async (_, _) =>
            {
                // Ignores the token and sets its own 2 s command timeout, so the driver ends the command itself.
                try { return await conn.ExecuteAsync(new CommandDefinition(SlowSql, commandTimeout: 2)); }
                catch (Exception ex) { seen = ex; throw; }
            }, timeoutSeconds: 1);

            if (DriverEnforcesCommandTimeout)
                Assert.True(timedOut, $"expected a timeout; the statement {(seen == null ? "completed" : "threw " + Describe(seen))}");
            else
                // Characterization: the driver ignored its timeout and the statement completed. A late but valid result
                // is returned as a result, not a timeout. If a driver upgrade starts enforcing it, this flips.
                Assert.True(!timedOut && seen == null, $"the driver now ends commands on CommandTimeout ({(seen == null ? "timed out" : Describe(seen))}); set DriverEnforcesCommandTimeout");
        }
        output.WriteLine($"{fixture.ProviderName} driver-timeout path: {(seen == null ? "(no exception)" : Describe(seen))}");
        await AssertFreshConnectionAnswersAsync();
    }

    private async Task AssertFreshConnectionAnswersAsync()
    {
        await using var fresh = await OpenAsync();
        await using var cmd = fresh.CreateCommand();
        cmd.CommandText = "SELECT 1";
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    private static string Describe(Exception ex) =>
        ex.InnerException == null
            ? $"{ex.GetType().FullName}: {ex.Message}"
            : $"{ex.GetType().FullName}: {ex.Message} ---> {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";
}

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlTimedQueryTests(PostgreSqlFixture fixture, ITestOutputHelper output) : TimedQueryProviderTestsBase(fixture, output)
{
    protected override string SlowSql => "SELECT pg_sleep(3)";
    protected override DbConnection CreateConnection(string connectionString) => new Npgsql.NpgsqlConnection(connectionString);
}

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerTimedQueryTests(SqlServerFixture fixture, ITestOutputHelper output) : TimedQueryProviderTestsBase(fixture, output)
{
    protected override string SlowSql => "WAITFOR DELAY '00:00:03'";
    protected override DbConnection CreateConnection(string connectionString) => new Microsoft.Data.SqlClient.SqlConnection(connectionString);
}

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlTimedQueryTests(MySqlFixture fixture, ITestOutputHelper output) : TimedQueryProviderTestsBase(fixture, output)
{
    // A 10^8-row cross join: ~6 s uninterrupted on the reference machine, and interrupting it is an error ("Query
    // execution was interrupted"), as it is for a real summary query. SLEEP() and BENCHMARK() both return normally
    // when killed, so they cannot stand in for one.
    protected override string SlowSql => "WITH d AS (SELECT 0 AS x UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) SELECT COUNT(*) FROM d a, d b, d c, d e, d f, d g, d h, d i";
    protected override DbConnection CreateConnection(string connectionString) => new MySqlConnector.MySqlConnection(connectionString);
}

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseTimedQueryTests(ClickHouseFixture fixture, ITestOutputHelper output) : TimedQueryProviderTestsBase(fixture, output)
{
    // ~9.5 s uninterrupted on the reference machine, across many blocks, so cancellation can land between them.
    protected override string SlowSql => "SELECT sum(cityHash64(number)) FROM numbers(4000000000)";
    protected override bool DriverEnforcesCommandTimeout => false;
    protected override DbConnection CreateConnection(string connectionString) => new global::ClickHouse.Client.ADO.ClickHouseConnection(connectionString);
}
