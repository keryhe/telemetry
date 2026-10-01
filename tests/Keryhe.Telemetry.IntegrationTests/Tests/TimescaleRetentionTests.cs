using Npgsql;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleRetentionTests(TimescaleFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly TimescaleFixture _db = fixture;

    // drop_chunks removes whole chunks: a row just past the cutoff survives until its 6 h / 12-24 h chunk is wholly expired.
    protected override bool RowGranularRetention => false;

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
