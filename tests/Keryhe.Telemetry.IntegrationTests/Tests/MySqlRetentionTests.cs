using MySqlConnector;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlRetentionTests(MySqlFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly MySqlFixture _db = fixture;

    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new MySqlConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            table == "metrics" ? "SELECT COUNT(*) FROM metrics" : $"SELECT COUNT(*) FROM {table} WHERE {timeColumn} >= @from AND {timeColumn} < @to", conn);
        cmd.Parameters.AddWithValue("@from", fromNano);
        cmd.Parameters.AddWithValue("@to", toNano);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
