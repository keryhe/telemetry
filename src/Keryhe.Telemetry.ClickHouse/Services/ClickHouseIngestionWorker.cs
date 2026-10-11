using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>The token overloads of <see cref="ClickHouseBulkWriter"/>, as a seam so the worker can be tested without a database.</summary>
internal interface IClickHouseTokenWriter : IPartitionedTokenWriter;

/// <summary>
/// ClickHouse's ingestion worker (plans/clickhouse-redesign phase 2): <see cref="PartitionedIngestionWorker"/> with UTC-day
/// partitions and <see cref="ClickHouseIngestionOptions"/>. All behaviour lives in the shared worker.
/// </summary>
internal sealed class ClickHouseIngestionWorker(
    IClickHouseTokenWriter writer,
    TelemetryIngestionChannel channel,
    IOptions<ClickHouseIngestionOptions> options,
    IOptions<TelemetryIngestionOptions> sharedOptions,
    IngestionMetrics metrics,
    IRetentionWindows retention,
    TimeProvider time,
    ILogger<ClickHouseIngestionWorker> logger,
    IFlushErrorClassifier? classifier = null)
    : PartitionedIngestionWorker("ClickHouse", static nano => nano / NanosPerDay, writer, channel, options.Value, sharedOptions, metrics,
        retention, time, logger, classifier)
{
    private const long NanosPerDay = 86_400_000_000_000L;
}
