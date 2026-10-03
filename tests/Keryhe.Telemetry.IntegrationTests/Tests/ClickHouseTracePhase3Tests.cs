using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseTracePhase3Tests(ClickHouseFixture fixture) : TracePhase3TestsBase(fixture)
{
    // The hint stands in for the trace_index lookup.
    protected override bool HonorsStartHint => true;
}
