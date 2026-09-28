using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleMetricPhase5Tests(TimescaleFixture fixture) : MetricPhase5TestsBase(fixture);
