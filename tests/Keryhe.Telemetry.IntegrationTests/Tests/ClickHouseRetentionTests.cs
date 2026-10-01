using ClickHouse.Client.ADO;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseRetentionTests(ClickHouseFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly ClickHouseFixture _db = fixture;

    // DROP PARTITION removes whole days: a row just past the cutoff survives until its day is wholly expired.
    protected override bool RowGranularRetention => false;

    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new ClickHouseConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = table == "metrics"
            ? "SELECT count() FROM metrics"
            : $"SELECT count() FROM {table} WHERE {timeColumn} >= {fromNano} AND {timeColumn} < {toNano}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
