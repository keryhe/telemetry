using Microsoft.Data.SqlClient;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerRetentionTests(SqlServerFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly SqlServerFixture _db = fixture;

    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new SqlConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            table == "metrics" ? "SELECT COUNT_BIG(*) FROM metrics" : $"SELECT COUNT_BIG(*) FROM {table} WHERE {timeColumn} >= @from AND {timeColumn} < @to", conn);
        cmd.Parameters.AddWithValue("@from", fromNano);
        cmd.Parameters.AddWithValue("@to", toNano);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
