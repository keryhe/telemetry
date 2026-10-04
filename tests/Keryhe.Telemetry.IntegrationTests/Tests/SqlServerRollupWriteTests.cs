using Microsoft.Data.SqlClient;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerRollupWriteTests(SqlServerFixture fixture) : RollupWriteTestsBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenAsync()
    {
        var conn = new SqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return conn;
    }
}
