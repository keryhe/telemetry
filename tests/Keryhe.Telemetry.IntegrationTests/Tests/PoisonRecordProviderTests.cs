using System.Data.Common;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlPoisonRecordTests(PostgreSqlFixture fixture) : PoisonRecordTestsBase(fixture)
{
    protected override async Task<DbConnection> OpenAsync() { var c = new NpgsqlConnection(fixture.DatabaseConnectionString); await c.OpenAsync(); return c; }
}

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerPoisonRecordTests(SqlServerFixture fixture) : PoisonRecordTestsBase(fixture)
{
    protected override async Task<DbConnection> OpenAsync() { var c = new SqlConnection(fixture.DatabaseConnectionString); await c.OpenAsync(); return c; }
}

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlPoisonRecordTests(MySqlFixture fixture) : PoisonRecordTestsBase(fixture)
{
    protected override async Task<DbConnection> OpenAsync() { var c = new MySqlConnection(fixture.DatabaseConnectionString); await c.OpenAsync(); return c; }
}
