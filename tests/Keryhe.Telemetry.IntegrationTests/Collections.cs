using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests;

/// <summary>
/// One xUnit collection per provider, each owning that provider's <see cref="ProviderFixture"/>
/// for the whole test run — the container starts once (first test that touches the collection)
/// and is shared by every test class declared under the same <c>[Collection]</c> name.
/// </summary>
[CollectionDefinition(ProviderNames.PostgreSql)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;

[CollectionDefinition(ProviderNames.Timescale)]
public sealed class TimescaleCollection : ICollectionFixture<TimescaleFixture>;

[CollectionDefinition(ProviderNames.SqlServer)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

[CollectionDefinition(ProviderNames.MySql)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;

[CollectionDefinition(ProviderNames.ClickHouse)]
public sealed class ClickHouseCollection : ICollectionFixture<ClickHouseFixture>;
