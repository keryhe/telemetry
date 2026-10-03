using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleApiKeyLookupTests(TimescaleFixture fixture) : ApiKeyLookupTestsBase(fixture)
{}
