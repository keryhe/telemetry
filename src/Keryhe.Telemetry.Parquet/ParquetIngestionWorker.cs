using Keryhe.Telemetry.Core.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// The Parquet provider's ingestion worker (plans/parquet-duckdb phase 2): the shared <see cref="PartitionedIngestionWorker"/>
/// with UTC-hour buffers. A sealed buffer is written as one file per (tenant, hour) by <see cref="ParquetBulkWriter"/>
/// under the buffer's token, which a retry reuses so the file is stored once (README D6).
/// </summary>
internal sealed class ParquetIngestionWorker(
    IParquetTokenWriter writer,
    TelemetryIngestionChannel channel,
    IOptions<ParquetOptions> options,
    IOptions<TelemetryIngestionOptions> sharedOptions,
    IngestionMetrics metrics,
    IRetentionWindows retention,
    TimeProvider time,
    ILogger<ParquetIngestionWorker> logger,
    IFlushErrorClassifier? classifier = null)
    : PartitionedIngestionWorker("Parquet", static nano => nano / NanosPerHour, writer, channel, options.Value, sharedOptions, metrics,
        retention, time, logger, classifier)
{
    internal const long NanosPerHour = 3_600_000_000_000L;
}
