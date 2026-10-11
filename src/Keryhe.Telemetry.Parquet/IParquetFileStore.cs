namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// The only code that touches the file system (README U4, phase 1 decision 2). A local implementation exists now;
/// object storage later implements the same operations. Names passed in are file names inside a partition's folder.
/// </summary>
public interface IParquetFileStore
{
    string Root { get; }

    /// <summary>
    /// The local path DuckDB writes a new file to before it is committed: inside the partition's <c>.tmp</c> folder, so
    /// a reader never sees a partial file and a crash leaves only temp files.
    /// </summary>
    string NewTempPath(ParquetPartition partition, string writerId, string name);

    /// <summary>The local path of a committed file, for DuckDB to read.</summary>
    string PathOf(ParquetPartition partition, string name);

    /// <summary>Makes a finished temp file visible under <paramref name="finalName"/> (rename; replaces a file of that name).</summary>
    Task CommitFileAsync(ParquetPartition partition, string tempPath, string finalName, CancellationToken ct);

    /// <summary>The manifest file names in the partition (<c>_manifest.*</c> and <c>_compacted.*</c>); empty if the folder does not exist.</summary>
    Task<IReadOnlyList<string>> ListManifestNamesAsync(ParquetPartition partition, CancellationToken ct);

    /// <summary>The manifest named so, or null if it does not exist.</summary>
    Task<ParquetManifest?> ReadManifestAsync(ParquetPartition partition, string manifestName, CancellationToken ct);

    /// <summary>Replaces a writer's own manifest atomically (temp + rename, overwriting).</summary>
    Task WriteManifestAsync(ParquetPartition partition, string manifestName, ParquetManifest manifest, CancellationToken ct);

    /// <summary>
    /// Publishes a manifest that must not already exist (a compacted generation): temp + rename without overwrite.
    /// False when another process published that name first.
    /// </summary>
    Task<bool> TryPublishManifestAsync(ParquetPartition partition, string manifestName, ParquetManifest manifest, CancellationToken ct);

    Task DeleteFileAsync(ParquetPartition partition, string name, CancellationToken ct);

    /// <summary>The data files (<c>*.parquet</c>) in the partition's folder with their sizes and modification times; the compactor's orphan and grace rules read these.</summary>
    Task<IReadOnlyList<ParquetFileInfo>> ListFilesAsync(ParquetPartition partition, CancellationToken ct);

    /// <summary>The files in the partition's <c>.tmp</c> folder (a crash between COPY and rename leaves them there).</summary>
    Task<IReadOnlyList<ParquetFileInfo>> ListTempFilesAsync(ParquetPartition partition, CancellationToken ct);

    Task DeleteTempFileAsync(ParquetPartition partition, string name, CancellationToken ct);

    /// <summary>A small text file at a root-relative path (the compactor's lease), or null if it does not exist.</summary>
    Task<string?> ReadTextAsync(string relativePath, CancellationToken ct);

    /// <summary>Replaces a small text file at a root-relative path atomically (temp + rename).</summary>
    Task WriteTextAsync(string relativePath, string text, CancellationToken ct);

    /// <summary>Every partition of a table (optionally one tenant) whose date is within the inclusive range.</summary>
    IEnumerable<ParquetPartition> EnumeratePartitions(string table, long? tenantId, DateOnly from, DateOnly to);

    /// <summary>Deletes a whole day folder of one tenant (retention); returns whether it existed.</summary>
    Task<bool> DeleteDayAsync(string table, long tenantId, DateOnly date, CancellationToken ct);

    /// <summary>Deletes the temp files a writer left behind (a crash between COPY and rename); returns how many.</summary>
    int DeleteTempFiles(string writerId);
}

/// <summary>A file in a partition's folder: its name, size and last modification (Unix milliseconds).</summary>
public sealed record ParquetFileInfo(string Name, long Bytes, long ModifiedUnixMs);
