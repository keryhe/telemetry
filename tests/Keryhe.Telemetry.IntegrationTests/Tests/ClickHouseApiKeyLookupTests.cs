using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>ClickHouse has no key lookup of its own: its key table is the PostgreSQL control plane's, so the shared cases run against that.</summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseApiKeyLookupTests(ClickHouseFixture fixture) : ApiKeyLookupTestsBase(fixture);
