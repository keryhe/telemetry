using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The control-plane repositories against the real SQLite schema. The Parquet fixture's control plane is a SQLite file, so these
/// run on it: no Docker (<c>--filter Provider=Sqlite</c>).
/// </summary>
[Collection(ProviderNames.Parquet)]
[Trait("Provider", "Sqlite")]
public sealed class SqliteControlPlaneTests(ParquetFixture fixture) : ControlPlaneTestsBase(fixture);

[Collection(ProviderNames.Parquet)]
[Trait("Provider", "Sqlite")]
public sealed class SqliteApiKeyLookupTests(ParquetFixture fixture) : ApiKeyLookupTestsBase(fixture);
