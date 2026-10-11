using System.Collections.Concurrent;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// A collector's side of the manifests (README U6, phase 1 decision 7): commits a finished temp file into a partition
/// and records it in this writer's manifest. Commit is <i>rename the file, then rewrite the manifest</i>, so a crash
/// between the two leaves an unlisted file that no reader sees (the compactor's orphan sweep removes it, phase 4).
/// Sequence numbers are per writer per partition, assigned under a per-partition lock, so the manifest lists files
/// in commit order. A commit under a token the manifest already holds is a retry: it replaces that file in place and
/// keeps its sequence (README D6), so a retried flush is stored once.
/// </summary>
public sealed class ManifestWriter(IParquetFileStore store, string writerId)
{
    private readonly ConcurrentDictionary<ParquetPartition, SemaphoreSlim> _locks = new();

    public string WriterId { get; } = writerId;

    /// <summary>The manifest file name of this writer.</summary>
    public string ManifestName { get; } = ParquetManifest.FileNameFor(writerId);

    public async Task<ParquetManifestFile> CommitAsync(
        ParquetPartition partition, string tempPath, string token, long rows, long bytes, long minTime, long maxTime, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(partition, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var manifest = await store.ReadManifestAsync(partition, ManifestName, ct)
                ?? new ParquetManifest { Writer = WriterId };

            var existing = manifest.Files.FirstOrDefault(f => f.Token == token);
            var sequence = existing?.Sequence ?? manifest.NextSequence;
            var name = existing?.Name ?? $"part-{WriterId}-{sequence}-{token}.parquet";

            await store.CommitFileAsync(partition, tempPath, name, ct);

            var entry = new ParquetManifestFile
            {
                Name = name, Sequence = sequence, Token = token, Rows = rows, Bytes = bytes, MinTime = minTime, MaxTime = maxTime,
                CommittedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (existing is null)
            {
                manifest.Files.Add(entry);
                manifest.NextSequence = sequence + 1;
            }
            else
            {
                manifest.Files[manifest.Files.IndexOf(existing)] = entry;
            }

            // Files the compactor has folded in are listed there by its generation; dropping them here keeps this manifest
            // short (phase 4 decision 7). The generation is read after the file is in place, and a reader reads this manifest
            // before the generation, so a pruned entry is always covered by a generation the reader then sees.
            var through = await ParquetManifests.ThroughAsync(store, partition, WriterId, ct);
            manifest.Files.RemoveAll(f => f.Sequence <= through && f.Sequence != sequence);

            await store.WriteManifestAsync(partition, ManifestName, manifest, ct);
            return entry;
        }
        finally
        {
            gate.Release();
        }
    }
}
