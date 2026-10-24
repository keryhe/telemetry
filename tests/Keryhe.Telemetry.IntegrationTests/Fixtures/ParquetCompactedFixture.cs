using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Parquet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// The Parquet provider whose every span flush is followed by a forced compaction of everything (plans/parquet-duckdb phase 4):
/// the compactor's clock is two days ahead and every threshold is zero, so each flush makes the hour a closed one, merges the day
/// into its <c>_day</c> folder (the next flush arrives as a late writer file the next pass merges again) and deletes what it
/// superseded at once. The shared trace suites then run over compacted data.
/// </summary>
public sealed class ParquetCompactedFixture : ProviderFixture
{
    private readonly global::Keryhe.Telemetry.TestInfrastructure.Containers.ParquetProviderContainer _container = new();

    public override string ProviderName => ProviderNames.ParquetCompacted;
    protected override global::Keryhe.Telemetry.TestInfrastructure.Containers.ProviderContainer Container => _container;
    public string RootPath => _container.ConnectionString;

    protected override IReadOnlyDictionary<string, string?> ProviderSettings => new Dictionary<string, string?>
    {
        ["Telemetry:Parquet:RootPath"] = _container.ConnectionString,
        ["Telemetry:Parquet:WriterId"] = "integration-compacted",
        ["Telemetry:Parquet:Compaction:Enabled"] = "false"
    };

    protected override void AddProviderServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddParquetCollectorServices(configuration);
        services.AddParquetApiServices(configuration);
        services.AddSqliteApiKeyLookup(configuration);
        services.AddSqliteControlPlaneManagement(configuration);

        // Every flush through ITelemetryBulkWriter is followed by a pass of a compactor that sees the data as two days old.
        var inner = services.Last(d => d.ServiceType == typeof(ITelemetryBulkWriter));
        services.Remove(inner);
        services.AddSingleton<ITelemetryBulkWriter>(sp =>
        {
            var options = new ParquetOptions { RootPath = _container.ConnectionString };
            var o = options.Compaction;
            (o.MinorFileThreshold, o.MinorAgeSeconds, o.CloseHourGraceMinutes, o.CloseDayGraceHours, o.GraceSeconds) = (1, 0, 0, 0, 0);
            var compactor = new ParquetCompactor(
                sp.GetRequiredService<IParquetFileStore>(), Options.Create(options), new TwoDaysAhead(), NullLogger<ParquetCompactor>.Instance, new ParquetMetrics());
            return new CompactingBulkWriter(sp.GetRequiredService<ParquetBulkWriter>(), compactor);
        });
    }

    public override Task ResetAsync()
    {
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            if (Path.GetFileName(dir).StartsWith('_')) continue;
            Directory.Delete(dir, recursive: true);
        }
        ((CompactingBulkWriter)Services.GetRequiredService<ITelemetryBulkWriter>()).Inner.ResetCatalogTrackers();
        return Task.CompletedTask;
    }

    private sealed class TwoDaysAhead : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(2);
    }

    private sealed class CompactingBulkWriter(ParquetBulkWriter inner, ParquetCompactor compactor) : ITelemetryBulkWriter
    {
        public ParquetBulkWriter Inner => inner;

        public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken cancellationToken = default)
        {
            await inner.FlushLogsAsync(records, cancellationToken);
            await CompactAsync(cancellationToken);
        }

        public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default)
        {
            await inner.FlushMetricsAsync(metrics, cancellationToken);
            await CompactAsync(cancellationToken);
        }

        public async Task FlushTracesAsync(List<SpanModel> spans, CancellationToken cancellationToken = default)
        {
            await inner.FlushTracesAsync(spans, cancellationToken);
            await CompactAsync(cancellationToken);
        }

        private async Task CompactAsync(CancellationToken cancellationToken)
        {
            var result = await compactor.RunPassAsync(fullScan: true, cancellationToken);
            if (result.Failures > 0) throw new InvalidOperationException($"The forced compaction failed {result.Failures} time(s); see the compactor's log.");
        }
    }
}
