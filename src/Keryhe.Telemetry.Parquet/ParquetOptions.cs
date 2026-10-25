using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Settings of the Parquet provider, bound from <c>Telemetry:Parquet</c> (plans/parquet-duckdb README, "Configuration").
/// The collector and the API host must see the same <see cref="RootPath"/> on one machine's file system.
/// </summary>
public sealed class ParquetOptions : IPartitionedIngestionOptions
{
    public const string SectionName = "Telemetry:Parquet";

    /// <summary>The root directory of the data; required.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>A collector's stable id, unique per instance on one root. Unset: machine name plus a suffix saved under <c>_writers/</c>.</summary>
    public string? WriterId { get; set; }

    /// <summary>How long the open hour's buffer collects records before it is written (used by the ingestion worker).</summary>
    public int LingerMilliseconds { get; set; } = 5_000;

    /// <summary>The same for a buffer of an earlier hour.</summary>
    public int LateLingerMilliseconds { get; set; } = 120_000;

    /// <summary>A buffer is sealed and written as soon as it holds this many records (spans, log records or metrics).</summary>
    public int MaxBufferRecords { get; set; } = 50_000;

    /// <summary>With more earlier-hour buffers than this, the oldest is written early.</summary>
    public int MaxLateBuffers { get; set; } = 24;

    /// <summary>How often the retention windows used to drop out-of-retention records are re-read from the control plane.</summary>
    public int RetentionRefreshSeconds { get; set; } = 300;

    int IPartitionedIngestionOptions.MaxSpanBatchRecords => MaxBufferRecords;
    int IPartitionedIngestionOptions.MaxLogBatchRecords => MaxBufferRecords;
    int IPartitionedIngestionOptions.MaxMetricBatchRecords => MaxBufferRecords;

    /// <summary>Reserved: the gate's <c>MaxQueuedBytesPerSignal</c> already bounds resident bytes; a size seal on bytes is not applied yet.</summary>
    public long MaxBufferBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>A metric or series that keeps reporting has its catalog/series row re-written at most this often.</summary>
    public int CatalogRefreshSeconds { get; set; } = 300;

    /// <summary>Series and metrics the writer remembers between flushes; past it the tracker forgets (a forgotten one is simply re-written).</summary>
    public int MaxTrackedSeries { get; set; } = 1_000_000;

    /// <summary>Parquet row group size; DuckDB's default.</summary>
    public int RowGroupRows { get; set; } = 122_880;

    /// <summary><c>zstd</c>, <c>snappy</c>, <c>gzip</c> or <c>uncompressed</c>.</summary>
    public string Compression { get; set; } = "zstd";

    /// <summary>The API host's read database.</summary>
    public DuckDbOptions DuckDb { get; set; } = new() { MemoryLimit = "2GB" };

    public WriterOptions Writer { get; set; } = new();

    /// <summary>The compactor that runs on the API host (plans/parquet-duckdb phase 4).</summary>
    public CompactionOptions Compaction { get; set; } = new();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
            throw new InvalidOperationException($"{SectionName}:RootPath is required (the directory the Parquet files live in).");
        if (WriterId is not null && !ParquetWriterIdentity.IsValidId(WriterId))
            throw new InvalidOperationException($"{SectionName}:WriterId must be 1-64 letters, digits, '_' or '-' (was '{WriterId}').");

        Positive(LingerMilliseconds, nameof(LingerMilliseconds));
        Positive(LateLingerMilliseconds, nameof(LateLingerMilliseconds));
        Positive(MaxBufferRecords, nameof(MaxBufferRecords));
        Positive(MaxBufferBytes, nameof(MaxBufferBytes));
        Positive(MaxLateBuffers, nameof(MaxLateBuffers));
        Positive(RetentionRefreshSeconds, nameof(RetentionRefreshSeconds));
        Positive(RowGroupRows, nameof(RowGroupRows));
        Positive(CatalogRefreshSeconds, nameof(CatalogRefreshSeconds));
        Positive(MaxTrackedSeries, nameof(MaxTrackedSeries));
        if (!AllowedCompressions.Contains(Compression, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{SectionName}:Compression must be one of {string.Join(", ", AllowedCompressions)} (was '{Compression}').");
        DuckDb.Validate($"{SectionName}:DuckDb");
        Writer.DuckDb.Validate($"{SectionName}:Writer:DuckDb");
        Compaction.Validate($"{SectionName}:Compaction");
    }

    private static readonly string[] AllowedCompressions = ["zstd", "snappy", "gzip", "uncompressed"];

    private static void Positive(long value, string name)
    {
        if (value <= 0) throw new InvalidOperationException($"{SectionName}:{name} must be greater than 0 (was {value}).");
    }
}

/// <summary>Resource limits of one in-process DuckDB database.</summary>
public sealed class DuckDbOptions
{
    /// <summary>DuckDB size text, for example <c>2GB</c> or <c>512MB</c>.</summary>
    public string MemoryLimit { get; set; } = "1GB";

    /// <summary>Unset: the processor count.</summary>
    public int? Threads { get; set; }

    /// <summary>Where DuckDB spills; unset: <c>&lt;RootPath&gt;/_tmp/&lt;host&gt;</c>.</summary>
    public string? TempDirectory { get; set; }

    internal void Validate(string path)
    {
        // The value goes into a SET statement, so only a plain size is accepted.
        if (!System.Text.RegularExpressions.Regex.IsMatch(MemoryLimit ?? "", @"^\d+(\.\d+)?\s?(KB|MB|GB|TB|KiB|MiB|GiB|TiB)$"))
            throw new InvalidOperationException($"{path}:MemoryLimit must be a size such as 2GB or 512MB (was '{MemoryLimit}').");
        if (Threads is <= 0)
            throw new InvalidOperationException($"{path}:Threads must be greater than 0 (was {Threads}).");
    }
}

public sealed class WriterOptions
{
    /// <summary>The collector's file-writing database.</summary>
    public DuckDbOptions DuckDb { get; set; } = new() { MemoryLimit = "1GB", Threads = 2 };
}

/// <summary>
/// The compactor's settings (<c>Telemetry:Parquet:Compaction</c>, phase 4 decisions 1-9). It runs only on the API host, one
/// instance at a time under a lease, and bounds its own memory.
/// </summary>
public sealed class CompactionOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the worker wakes: to renew its lease and, while it holds it, to run a pass.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>Another instance takes the lease over once it has not been renewed for this long.</summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>
    /// A partition with more uncompacted writer files than this is folded.
    /// </summary>
    public int MinorFileThreshold { get; set; } = 30;

    /// <summary>Uncompacted writer files older than this are folded even below the threshold.</summary>
    public int MinorAgeSeconds { get; set; } = 60;

    /// <summary>An hour is rewritten into one file set once it has been over this long.</summary>
    public int CloseHourGraceMinutes { get; set; } = 15;

    /// <summary>A day is merged into its <c>_day</c> folder once it has been over this long.</summary>
    public int CloseDayGraceHours { get; set; } = 6;

    /// <summary>A compacted file is split at this many rows.</summary>
    public long MaxFileRows { get; set; } = 10_000_000;

    /// <summary>One sorted <c>COPY</c> takes at most this many rows; a bigger partition is cut on the first column of the sort key.</summary>
    public long MaxSortRows { get; set; } = 20_000_000;

    /// <summary>
    /// A superseded file is deleted this long after it was superseded. It must exceed the longest query: an export holds the file
    /// list it resolved and is unbounded, so a file deleted under it ends that export with an error.
    /// </summary>
    public int GraceSeconds { get; set; } = 600;

    public int MaxConcurrentCompactions { get; set; } = 2;

    /// <summary>A pass looks at the partitions of the last this-many UTC days (late data is the reason it is more than today)...</summary>
    public int ScanDays { get; set; } = 2;

    /// <summary>...and, every this-many minutes, at every partition.</summary>
    public int FullScanMinutes { get; set; } = 60;

    /// <summary>The compactor's own DuckDB database (dictionaries are not spilled, so the memory limit bounds a sort).</summary>
    public DuckDbOptions DuckDb { get; set; } = new() { MemoryLimit = "3GB", Threads = 4 };

    internal void Validate(string path)
    {
        Positive(IntervalSeconds, nameof(IntervalSeconds), path);
        Positive(LeaseSeconds, nameof(LeaseSeconds), path);
        Positive(MinorFileThreshold, nameof(MinorFileThreshold), path);
        Positive(MaxFileRows, nameof(MaxFileRows), path);
        Positive(MaxSortRows, nameof(MaxSortRows), path);
        Positive(MaxConcurrentCompactions, nameof(MaxConcurrentCompactions), path);
        Positive(ScanDays, nameof(ScanDays), path);
        Positive(FullScanMinutes, nameof(FullScanMinutes), path);
        NotNegative(MinorAgeSeconds, nameof(MinorAgeSeconds), path);
        NotNegative(CloseHourGraceMinutes, nameof(CloseHourGraceMinutes), path);
        NotNegative(CloseDayGraceHours, nameof(CloseDayGraceHours), path);
        NotNegative(GraceSeconds, nameof(GraceSeconds), path);
        DuckDb.Validate($"{path}:DuckDb");
    }

    private static void Positive(long value, string name, string path)
    {
        if (value <= 0) throw new InvalidOperationException($"{path}:{name} must be greater than 0 (was {value}).");
    }

    private static void NotNegative(long value, string name, string path)
    {
        if (value < 0) throw new InvalidOperationException($"{path}:{name} cannot be negative (was {value}).");
    }
}
