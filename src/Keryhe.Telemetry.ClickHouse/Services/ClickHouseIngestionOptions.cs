namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Batching for <see cref="ClickHouseIngestionWorker"/>, bound from <c>Telemetry:ClickHouse:Ingestion</c>. On ClickHouse
/// the shared <c>Telemetry:Ingestion</c> gate sizes still apply (they bound resident records, including those waiting
/// in day buffers); its <c>FlushConcurrency</c>, flush batch sizes and linger do not. Retry settings are shared.
/// </summary>
public sealed class ClickHouseIngestionOptions
{
    public const string SectionName = "Telemetry:ClickHouse:Ingestion";

    /// <summary>How long the current day's buffer collects records after its first record before it is flushed.</summary>
    public int LingerMilliseconds { get; set; } = 2_500;

    /// <summary>The same for a buffer of an earlier day (late data): fewer, larger inserts.</summary>
    public int LateLingerMilliseconds { get; set; } = 30_000;

    /// <summary>A buffer flushes as soon as it holds this many spans.</summary>
    public int MaxSpanBatchRecords { get; set; } = 300_000;

    public int MaxLogBatchRecords { get; set; } = 300_000;

    /// <summary>Metrics are counted as <c>MetricModel</c>s (the gate's unit), not data points.</summary>
    public int MaxMetricBatchRecords { get; set; } = 300_000;

    /// <summary>With more late-day buffers than this, the oldest flushes early.</summary>
    public int MaxLateDayBuffers { get; set; } = 8;

    /// <summary>How often the retention windows used to drop out-of-retention records are re-read from the control plane.</summary>
    public int RetentionRefreshSeconds { get; set; } = 300;

    /// <summary>A metric or series that keeps reporting has its <c>metric_catalog</c>/<c>metric_series</c> row re-written at most this often.</summary>
    public int CatalogRefreshSeconds { get; set; } = 300;

    /// <summary>Series and metrics remembered between flushes; past it the tracker forgets (a forgotten one is simply re-written).</summary>
    public int MaxTrackedSeries { get; set; } = 1_000_000;

    /// <summary>
    /// A raw-table insert of at least this many rows is built in parallel (spans, logs) and sent as several concurrent
    /// inserts; a smaller one takes the single-insert path unchanged. Builds and serializes dominate a large flush
    /// (the database itself is mostly idle), so this is what lets the collector keep up at high rates.
    /// </summary>
    public int ParallelFlushMinRows { get; set; } = 50_000;

    /// <summary>The fewest rows a piece of a split insert holds, so a split never produces many small parts.</summary>
    public int InsertPieceRows { get; set; } = 25_000;

    /// <summary>The most concurrent inserts per table for one split batch, and the most threads building rows.</summary>
    public int MaxParallelInserts { get; set; } = 4;

    public void Validate()
    {
        static void Positive(int value, string name)
        {
            if (value <= 0) throw new InvalidOperationException($"{SectionName}:{name} must be greater than 0 (was {value}).");
        }
        Positive(LingerMilliseconds, nameof(LingerMilliseconds));
        Positive(LateLingerMilliseconds, nameof(LateLingerMilliseconds));
        Positive(MaxSpanBatchRecords, nameof(MaxSpanBatchRecords));
        Positive(MaxLogBatchRecords, nameof(MaxLogBatchRecords));
        Positive(MaxMetricBatchRecords, nameof(MaxMetricBatchRecords));
        Positive(MaxLateDayBuffers, nameof(MaxLateDayBuffers));
        Positive(RetentionRefreshSeconds, nameof(RetentionRefreshSeconds));
        Positive(CatalogRefreshSeconds, nameof(CatalogRefreshSeconds));
        Positive(MaxTrackedSeries, nameof(MaxTrackedSeries));
        Positive(ParallelFlushMinRows, nameof(ParallelFlushMinRows));
        Positive(InsertPieceRows, nameof(InsertPieceRows));
        Positive(MaxParallelInserts, nameof(MaxParallelInserts));
    }
}
