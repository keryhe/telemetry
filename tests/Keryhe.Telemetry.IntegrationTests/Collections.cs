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

[CollectionDefinition(ProviderNames.SqlServer)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

[CollectionDefinition(ProviderNames.MySql)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;

[CollectionDefinition(ProviderNames.ClickHouse)]
public sealed class ClickHouseCollection : ICollectionFixture<ClickHouseFixture>;

/// <summary>
/// Test classes that count the process-wide <c>Keryhe.Telemetry.Ingestion</c> meter with a <c>MeterListener</c>. A listener sees
/// every <c>IngestionMetrics</c> instance in the process, so these classes must not run at the same time as one another.
/// </summary>
[CollectionDefinition(IngestionMeterCollection.Name, DisableParallelization = false)]
public sealed class IngestionMeterCollection
{
    public const string Name = "IngestionMeter";
}
