namespace Keryhe.Telemetry.Parquet;

/// <summary>A file a reader (or the compactor) sees: the folder it lives in (an hour or a day partition) and its manifest entry.</summary>
public sealed record ParquetResolvedFile(ParquetPartition Partition, ParquetManifestFile File)
{
    public string PathIn(IParquetFileStore store) => store.PathOf(Partition, File.Name);
}

/// <summary>
/// One hour partition as the manifests describe it at a moment: the writers' manifests, the newest compacted generation of
/// the hour and the newest generation of its day (README "Layout").
/// </summary>
public sealed class ParquetPartitionState(
    ParquetPartition partition, IReadOnlyList<ParquetManifest> writers, ParquetManifest? generation, ParquetManifest? day)
{
    public ParquetPartition Partition { get; } = partition;
    public IReadOnlyList<ParquetManifest> Writers { get; } = writers;

    /// <summary>The hour's newest compacted generation, null if it was never compacted.</summary>
    public ParquetManifest? Generation { get; } = generation;

    /// <summary>The newest generation of the hour's day (<c>_day</c> folder), null if the day was never merged.</summary>
    public ParquetManifest? Day { get; } = day;

    /// <summary>The day has absorbed this hour: its compacted files are the day's, and the hour's own generation is ignored.</summary>
    public bool CoveredByDay => Day is not null && Day.HourThrough.ContainsKey(Partition.Hour);

    /// <summary>The hour's compacted files; empty when the day holds them.</summary>
    public IReadOnlyList<ParquetManifestFile> CompactedFiles => CoveredByDay ? [] : Generation?.Files ?? [];

    /// <summary>The highest sequence of <paramref name="writer"/> already folded into compacted files that a reader reads for this hour.</summary>
    public long ThroughOf(string writer)
    {
        if (CoveredByDay) return Day!.HourThrough[Partition.Hour].GetValueOrDefault(writer);
        return Generation?.Through.GetValueOrDefault(writer) ?? 0;
    }

    /// <summary>The writers' files above their writers' <c>through</c>: committed, not yet folded.</summary>
    public IEnumerable<(string Writer, ParquetManifestFile File)> UncompactedFiles
        => Writers.SelectMany(w => w.Files.Where(f => f.Sequence > ThroughOf(w.Writer)).Select(f => (w.Writer, f)));

    /// <summary>The files this hour contributes itself: its compacted files and the uncompacted writer files (the day's files are separate).</summary>
    public IEnumerable<ParquetResolvedFile> HourFiles
        => CompactedFiles.Concat(UncompactedFiles.Select(u => u.File)).Select(f => new ParquetResolvedFile(Partition, f));
}

/// <summary>
/// Which files of a partition a reader sees, and the generation helpers (README "Layout", phase 4). A reader's file set is
/// the newest compacted generation's files plus each writer's files above that writer's <c>through</c>; the writers'
/// manifests are read <i>first</i>, then the partition is listed again for the newest generation (the
/// opposite order loses files under concurrent commits and compactions). A day that was merged serves its closed hours from
/// the <c>_day</c> folder, read in the same second step.
/// </summary>
public static class ParquetManifests
{
    /// <summary>The newest generation manifest of a partition (hour or day folder), or null if it has none.</summary>
    public static async Task<ParquetManifest?> NewestGenerationAsync(IParquetFileStore store, ParquetPartition partition, CancellationToken ct)
    {
        var generations = new List<(int Generation, string Name)>();
        foreach (var name in await store.ListManifestNamesAsync(partition, ct))
            if (ParquetManifest.TryParseGeneration(name, out var generation)) generations.Add((generation, name));
        foreach (var (_, name) in generations.OrderByDescending(g => g.Generation))
            if (await store.ReadManifestAsync(partition, name, ct) is { } manifest) return manifest;
        return null;
    }

    /// <summary>Every generation manifest of a partition, oldest first, with its file name.</summary>
    public static async Task<IReadOnlyList<(string Name, ParquetManifest Manifest)>> AllGenerationsAsync(
        IParquetFileStore store, ParquetPartition partition, CancellationToken ct)
    {
        var list = new List<(string, ParquetManifest)>();
        foreach (var name in await store.ListManifestNamesAsync(partition, ct))
            if (ParquetManifest.TryParseGeneration(name, out _) && await store.ReadManifestAsync(partition, name, ct) is { } manifest)
                list.Add((name, manifest));
        return [.. list.OrderBy(g => g.Item2.Generation)];
    }

    /// <summary>The writers' manifests of an hour partition.</summary>
    public static async Task<IReadOnlyList<ParquetManifest>> ReadWriterManifestsAsync(IParquetFileStore store, ParquetPartition partition, CancellationToken ct)
    {
        var writers = new List<ParquetManifest>();
        foreach (var name in await store.ListManifestNamesAsync(partition, ct))
            if (name.StartsWith("_manifest.", StringComparison.Ordinal) && await store.ReadManifestAsync(partition, name, ct) is { } manifest)
                writers.Add(manifest);
        return writers;
    }

    /// <summary>
    /// The highest sequence of <paramref name="writer"/> that a compacted generation of this hour (or its day) already holds:
    /// what the writer may drop from its own manifest.
    /// </summary>
    public static async Task<long> ThroughAsync(IParquetFileStore store, ParquetPartition partition, string writer, CancellationToken ct)
    {
        var day = await NewestGenerationAsync(store, partition.DayPartition, ct);
        if (day is not null && day.HourThrough.TryGetValue(partition.Hour, out var hourThrough)) return hourThrough.GetValueOrDefault(writer);
        var generation = await NewestGenerationAsync(store, partition, ct);
        return generation?.Through.GetValueOrDefault(writer) ?? 0;
    }

    /// <summary>
    /// Reads the state of each hour partition (all of one table and tenant, in any order): every writers' manifest first, then
    /// the generations (README "Layout").
    /// </summary>
    public static async Task<IReadOnlyList<ParquetPartitionState>> ReadStatesAsync(
        IParquetFileStore store, IReadOnlyList<ParquetPartition> hours, CancellationToken ct)
    {
        var writers = new List<IReadOnlyList<ParquetManifest>>(hours.Count);
        foreach (var hour in hours) writers.Add(await ReadWriterManifestsAsync(store, hour, ct));

        var days = new Dictionary<(string, long, DateOnly), ParquetManifest?>();
        var states = new List<ParquetPartitionState>(hours.Count);
        for (var i = 0; i < hours.Count; i++)
        {
            var hour = hours[i];
            var key = (hour.Table, hour.TenantId, hour.Date);
            if (!days.TryGetValue(key, out var day))
                days[key] = day = await NewestGenerationAsync(store, hour.DayPartition, ct);
            var covered = day is not null && day.HourThrough.ContainsKey(hour.Hour);
            var generation = covered ? null : await NewestGenerationAsync(store, hour, ct);
            states.Add(new ParquetPartitionState(hour, writers[i], generation, day));
        }
        return states;
    }

    /// <summary>
    /// The files a reader opens for these hour partitions: each hour's compacted and uncompacted files, and the files of the
    /// day generation that overlap a covered hour of the list (each once).
    /// </summary>
    public static IReadOnlyList<ParquetResolvedFile> Resolve(IReadOnlyList<ParquetPartitionState> states)
    {
        var files = new List<ParquetResolvedFile>();
        foreach (var state in states) files.AddRange(state.HourFiles);

        foreach (var day in states.Where(s => s.CoveredByDay).GroupBy(s => (s.Partition.Table, s.Partition.TenantId, s.Partition.Date)))
        {
            var generation = day.First().Day!;
            var dayPartition = day.First().Partition.DayPartition;
            var covered = day.Select(s => s.Partition).ToList();
            foreach (var file in generation.Files)
                if (covered.Any(h => Overlaps(file, h)))
                    files.Add(new ParquetResolvedFile(dayPartition, file));
        }
        return files;
    }

    private static bool Overlaps(ParquetManifestFile file, ParquetPartition hour)
        => (file.MinTime == 0 && file.MaxTime == 0) || (file.MaxTime >= hour.StartNano && file.MinTime < hour.EndNano);

    public static async Task<IReadOnlyList<ParquetResolvedFile>> ResolveAsync(IParquetFileStore store, IReadOnlyList<ParquetPartition> hours, CancellationToken ct)
        => Resolve(await ReadStatesAsync(store, hours, ct));
}
