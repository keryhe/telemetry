using ClickHouse.Client.ADO;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseRollupWriteTests(ClickHouseFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
