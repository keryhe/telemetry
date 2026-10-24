using Keryhe.Telemetry.TestInfrastructure.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// The Parquet provider over a temp directory, with its control plane (tenants, keys, alert rules, retention settings) in a SQLite
/// file (Parquet tests default to it, so they need no Docker). The collector registration creates and verifies the root's schema file.
/// </summary>
public sealed class ParquetFixture : ProviderFixture
{
    private readonly ParquetProviderContainer _container = new();

    public override string ProviderName => ProviderNames.Parquet;

    protected override ProviderContainer Container => _container;

    /// <summary>The root directory the files live in.</summary>
    public string RootPath => _container.ConnectionString;

    protected override IReadOnlyDictionary<string, string?> ProviderSettings => new Dictionary<string, string?>
    {
        ["Telemetry:Parquet:RootPath"] = _container.ConnectionString,
        ["Telemetry:Parquet:WriterId"] = "integration-tests"
    };

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddParquetCollectorServices(configuration);
        services.AddParquetApiServices(configuration);
        services.AddSqliteApiKeyLookup(configuration);
        services.AddSqliteControlPlaneManagement(configuration);
    }

    public override Task ResetAsync()
    {
        // Empties the data tables; the schema file and the writer lock stay.
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('_')) continue;
            Directory.Delete(dir, recursive: true);
        }
        // The writer remembers which catalog and series rows it wrote; the files they were in are gone.
        ((global::Keryhe.Telemetry.Parquet.ParquetBulkWriter)Services.GetRequiredService<global::Keryhe.Telemetry.Core.ITelemetryBulkWriter>()).ResetCatalogTrackers();
        return Task.CompletedTask;
    }
}
