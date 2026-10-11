using System.Diagnostics.Metrics;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// The <c>Keryhe.Telemetry.Parquet</c> meter (phase 4 decision 10), readable out of process like the ingestion meter:
/// <c>compaction_duration</c> (ms, tagged by <c>kind</c>: minor, major, day), <c>files_compacted</c> (inputs folded),
/// <c>files_deleted</c>, <c>compaction_failures</c> and <c>uncompacted_files</c> (the largest count of committed, not yet folded
/// files in one partition at the last pass: the number readers pay for).
/// </summary>
public sealed class ParquetMetrics : IDisposable
{
    private readonly Meter _meter = new("Keryhe.Telemetry.Parquet", "1.0.0");
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _filesCompacted;
    private readonly Counter<long> _filesDeleted;
    private readonly Counter<long> _failures;
    private long _uncompacted;

    public ParquetMetrics()
    {
        _duration = _meter.CreateHistogram<double>("keryhe.telemetry.parquet.compaction_duration", unit: "ms",
            description: "Duration of one compaction of a partition, tagged by kind (minor, major, day).");
        _filesCompacted = _meter.CreateCounter<long>("keryhe.telemetry.parquet.files_compacted", unit: "{file}",
            description: "Input files folded into compacted files, tagged by kind.");
        _filesDeleted = _meter.CreateCounter<long>("keryhe.telemetry.parquet.files_deleted", unit: "{file}",
            description: "Superseded files, orphans and leftover temp files deleted.");
        _failures = _meter.CreateCounter<long>("keryhe.telemetry.parquet.compaction_failures", unit: "{failure}",
            description: "Compactions of a partition that failed (retried with back-off), tagged by kind.");
        _meter.CreateObservableGauge("keryhe.telemetry.parquet.uncompacted_files", () => Interlocked.Read(ref _uncompacted), unit: "{file}",
            description: "The largest number of uncompacted files in one partition at the last compaction pass.");
    }

    public Meter Meter => _meter;

    public void RecordCompaction(string kind, TimeSpan elapsed, int inputFiles)
    {
        _duration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("kind", kind));
        _filesCompacted.Add(inputFiles, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordDeleted(int files) { if (files > 0) _filesDeleted.Add(files); }

    public void RecordFailure(string kind) => _failures.Add(1, new KeyValuePair<string, object?>("kind", kind));

    /// <summary>The pass's largest uncompacted count so far; the gauge shows the last completed pass's value (see <see cref="EndPass"/>).</summary>
    internal void ObserveUncompacted(long files)
    {
        long current;
        do { current = Interlocked.Read(ref _pending); if (files <= current) return; }
        while (Interlocked.CompareExchange(ref _pending, files, current) != current);
    }

    private long _pending;

    internal void EndPass()
    {
        Interlocked.Exchange(ref _uncompacted, Interlocked.Exchange(ref _pending, 0));
    }

    public void Dispose() => _meter.Dispose();
}
