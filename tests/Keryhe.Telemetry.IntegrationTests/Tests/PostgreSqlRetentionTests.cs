using Npgsql;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlRetentionTests(PostgreSqlFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly PostgreSqlFixture _db = fixture;

    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new NpgsqlConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            table == "metrics" ? "SELECT COUNT(*) FROM metrics" : $"SELECT COUNT(*) FROM {table} WHERE {timeColumn} >= @from AND {timeColumn} < @to", conn);
        cmd.Parameters.AddWithValue("from", fromNano);
        cmd.Parameters.AddWithValue("to", toNano);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
