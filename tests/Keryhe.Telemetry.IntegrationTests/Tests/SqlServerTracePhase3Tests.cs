using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerTracePhase3Tests(SqlServerFixture fixture) : TracePhase3TestsBase(fixture);
