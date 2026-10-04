using MySqlConnector;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlRollupWriteTests(MySqlFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var conn = new MySqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
