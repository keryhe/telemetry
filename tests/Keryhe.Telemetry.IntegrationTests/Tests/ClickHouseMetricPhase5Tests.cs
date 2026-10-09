using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseMetricPhase5Tests(ClickHouseFixture fixture) : MetricPhase5TestsBase(fixture)
{
    // The ClickHouse catalog is one row per (service, metric, type): README R6.
    protected override bool CatalogInstanceIsPerResource => false;
}
