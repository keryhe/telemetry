using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlApiKeyLookupTests(MySqlFixture fixture) : ApiKeyLookupTestsBase(fixture)
{}
