using Npgsql;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleRollupWriteTests(TimescaleFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
