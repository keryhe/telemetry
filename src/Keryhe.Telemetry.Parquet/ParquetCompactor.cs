using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Parquet;

/// <summary>What one compaction pass did.</summary>
public sealed class CompactionPassResult
{
    private int _minor, _major, _day, _lost, _failures, _deleted;
    public int Minor => _minor;
    public int Major => _major;
    public int Day => _day;
    /// <summary>Compactions whose generation another compactor published first (their work was discarded).</summary>
    public int Lost => _lost;
    public int Failures => _failures;
    public int FilesDeleted => _deleted;

    internal void Add(string kind)
    {
        switch (kind)
        {
            case "minor": Interlocked.Increment(ref _minor); break;
            case "major": Interlocked.Increment(ref _major); break;
            default: Interlocked.Increment(ref _day); break;
        }
    }

    internal void AddLost() => Interlocked.Increment(ref _lost);
    internal void AddFailure() => Interlocked.Increment(ref _failures);
    internal void AddDeleted(int n) => Interlocked.Add(ref _deleted, n);
}

/// <summary>
/// Keeps the file count, and so read cost, bounded (plans/parquet-duckdb phase 4). A pass looks at the partitions of the
/// recent days (every partition once an hour) and, per tenant-day of a table:
/// <list type="bullet">
/// <item><b>Minor</b> (any hour, including the open one): the uncompacted writer files, when more than
/// <c>MinorFileThreshold</c> or older than <c>MinorAgeSeconds</c>, fold into one new compacted file.</item>
/// <item><b>Major</b>: an hour over for <c>CloseHourGraceMinutes</c> is rewritten into one file set (split at <c>MaxFileRows</c>).</item>
/// <item><b>Day</b>: a day over for <c>CloseDayGraceHours</c> is merged into its <c>_day</c> folder; the day's generation is the single
/// switch that moves its hours there (a late writer file above the day's <c>through</c> is merged by the next day pass).</item>
/// </list>
/// A compaction writes its files under names unique to this compactor, then publishes the next generation manifest by rename
/// <i>without overwrite</i>; if another compactor published that generation first, the work is discarded (README "Layout"). A
/// superseded file is deleted only after <c>GraceSeconds</c>, orphans and dead temp files after at least an hour. The tables are
/// generic: <see cref="ParquetTable"/> says how a derived table pre-sums.
/// </summary>
public sealed class ParquetCompactor : IDisposable
{
    private const long MillisPerMinute = 60_000;
    private readonly IParquetFileStore _store;
    private readonly ParquetOptions _parquet;
    private readonly CompactionOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger<ParquetCompactor> _log;
    private readonly ParquetMetrics _metrics;
    private readonly object _engineLock = new();
    private DuckDBConnection? _root;
    private readonly ConcurrentDictionary<ParquetPartition, Backoff> _backoff = new();

    /// <summary>Names this compactor's output files, so two compactors racing for one generation never write the same file.</summary>
    public string CompactorId { get; } = Guid.NewGuid().ToString("N")[..8];

    public ParquetCompactor(
        IParquetFileStore store, IOptions<ParquetOptions> options, TimeProvider time, ILogger<ParquetCompactor> log, ParquetMetrics metrics)
    {
        _store = store;
        _parquet = options.Value;
        _o = _parquet.Compaction;
        _time = time;
        _log = log;
        _metrics = metrics;
    }

    private sealed class Backoff { public int Failures; public DateTimeOffset NotBefore; }

    // ------------------------------------------------------------------ the pass

    /// <summary>One pass over the recent partitions (<paramref name="fullScan"/>: every partition).</summary>
    public async Task<CompactionPassResult> RunPassAsync(bool fullScan, CancellationToken ct)
    {
        var result = new CompactionPassResult();
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var from = fullScan ? DateOnly.MinValue : today.AddDays(-_o.ScanDays);

        var groups = ParquetTables.All
            .SelectMany(t => _store.EnumeratePartitions(t.Name, null, from, DateOnly.MaxValue))
            .GroupBy(p => (p.Table, p.TenantId, p.Date))
            .OrderByDescending(g => g.Key.Date)
            .ToList();

        await Parallel.ForEachAsync(groups,
            new ParallelOptions { MaxDegreeOfParallelism = _o.MaxConcurrentCompactions, CancellationToken = ct },
            async (group, token) =>
            {
                try { await ProcessDayAsync(ParquetTables.ByName(group.Key.Table), [.. group.OrderBy(p => p.Hour)], result, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    result.AddFailure();
                    _log.LogWarning(ex, "Compaction scan of {Table} tenant {Tenant} {Date} failed", group.Key.Table, group.Key.TenantId, group.Key.Date);
                }
            });

        _metrics.EndPass();
        return result;
    }

    private async Task ProcessDayAsync(ParquetTable table, IReadOnlyList<ParquetPartition> hours, CompactionPassResult result, CancellationToken ct)
    {
        var states = await ParquetManifests.ReadStatesAsync(_store, hours, ct);
        _metrics.ObserveUncompacted(states.Count == 0 ? 0 : states.Max(s => s.UncompactedFiles.Count()));
        var changed = false;

        foreach (var state in states)
        {
            if (state.CoveredByDay) continue;
            var kind = DecideHour(state);
            if (kind is null) continue;
            changed |= await GuardedAsync(state.Partition, kind, result, () => CompactHourAsync(table, state, kind, result, ct));
        }
        if (changed) states = await ParquetManifests.ReadStatesAsync(_store, hours, ct);

        var dayPartition = hours[0].DayPartition;
        var dayOver = _time.GetUtcNow().ToUnixTimeMilliseconds() >= NanoToMs(dayPartition.EndNano) + _o.CloseDayGraceHours * 3_600_000L;
        if (dayOver && NeedsDayMerge(states))
        {
            var merged = await GuardedAsync(dayPartition, "day", result, () => MergeDayAsync(table, states, result, ct));
            if (merged) states = await ParquetManifests.ReadStatesAsync(_store, hours, ct);
        }

        await CleanupAsync(states, dayPartition, result, ct);
    }

    private static long NanoToMs(long nano) => nano / 1_000_000;

    /// <summary>Runs an action; a failure is logged, counted and retried after a back-off, and never stops other partitions.</summary>
    private async Task<bool> GuardedAsync(ParquetPartition key, string kind, CompactionPassResult result, Func<Task<bool>> action)
    {
        var now = _time.GetUtcNow();
        if (_backoff.TryGetValue(key, out var backoff) && now < backoff.NotBefore) return false;
        try
        {
            var did = await action();
            _backoff.TryRemove(key, out _);
            return did;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.AddFailure();
            _metrics.RecordFailure(kind);
            var b = _backoff.GetOrAdd(key, _ => new Backoff());
            b.Failures++;
            b.NotBefore = now + TimeSpan.FromSeconds(Math.Min(1800, _o.IntervalSeconds * Math.Pow(2, Math.Min(b.Failures, 10))));
            _log.LogWarning(ex, "{Kind} compaction of {Partition} failed (attempt {Attempt}); retrying after {NotBefore:u}", kind, key, b.Failures, b.NotBefore);
            return false;
        }
    }

    // ------------------------------------------------------------------ decisions

    /// <summary>"major", "minor" or null for an hour the day has not absorbed.</summary>
    internal string? DecideHour(ParquetPartitionState state)
    {
        var compacted = state.CompactedFiles;
        var uncompacted = state.UncompactedFiles.Select(u => u.File).ToList();
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();

        var hourOver = nowMs >= NanoToMs(state.Partition.EndNano) + _o.CloseHourGraceMinutes * MillisPerMinute;
        var total = compacted.Count + uncompacted.Count;
        var rows = compacted.Sum(f => f.Rows) + uncompacted.Sum(f => f.Rows);
        if (hourOver && total > Math.Max(1, (rows + _o.MaxFileRows - 1) / _o.MaxFileRows)) return "major";

        if (uncompacted.Count > _o.MinorFileThreshold) return "minor";
        if (uncompacted.Count >= 2 && OlderThan(uncompacted, _o.MinorAgeSeconds, nowMs)) return "minor";
        return null;
    }

    /// <summary>Whether the oldest file has waited longer than <paramref name="seconds"/> (a file without a commit time counts as old).</summary>
    private static bool OlderThan(IEnumerable<ParquetManifestFile> files, int seconds, long nowMs)
        => files.Any(f => f.CommittedAtUnixMs == 0 || nowMs - f.CommittedAtUnixMs >= seconds * 1000L);

    private bool NeedsDayMerge(IReadOnlyList<ParquetPartitionState> states)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var state in states)
        {
            if (!state.CoveredByDay)
            {
                if (state.HourFiles.Any()) return true;
                continue;
            }
            var late = state.UncompactedFiles.Select(u => u.File).ToList();
            if (late.Count > 0 && OlderThan(late, _o.MinorAgeSeconds, nowMs)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ compactions

    internal async Task<bool> CompactHourAsync(ParquetTable table, ParquetPartitionState state, string kind, CompactionPassResult result, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var partition = state.Partition;
        var uncompacted = state.UncompactedFiles.ToList();
        var inputs = kind == "major" ? state.CompactedFiles.Concat(uncompacted.Select(u => u.File)).ToList() : uncompacted.Select(u => u.File).ToList();
        if (inputs.Count == 0) return false;

        var through = new Dictionary<string, long>(state.Generation?.Through ?? []);
        foreach (var (writer, file) in uncompacted) through[writer] = Math.Max(through.GetValueOrDefault(writer), file.Sequence);

        var generation = (state.Generation?.Generation ?? 0) + 1;
        var outputs = await WriteCompactedAsync(table, partition, generation, inputs.Select(f => _store.PathOf(partition, f.Name)).ToList(), inputs.Sum(f => f.Rows), ct);

        var manifest = NewGeneration(generation, kind == "major" ? outputs : [.. state.CompactedFiles, .. outputs]);
        manifest.Through = through;
        if (!await PublishAsync(partition, manifest, outputs, result, ct)) return false;

        result.Add(kind);
        _metrics.RecordCompaction(kind, _time.GetElapsedTime(started), inputs.Count);
        _log.LogInformation("{Kind} compaction of {Partition}: {Inputs} files -> {Outputs} (generation {Generation})", kind, partition, inputs.Count, outputs.Count, generation);
        return true;
    }

    internal async Task<bool> MergeDayAsync(ParquetTable table, IReadOnlyList<ParquetPartitionState> states, CompactionPassResult result, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var day = states[0].Day;
        var dayPartition = states[0].Partition.DayPartition;

        var inputPaths = new List<string>();
        long inputRows = 0;
        var inputCount = 0;
        var hourThrough = day is null ? [] : day.HourThrough.ToDictionary(h => h.Key, h => new Dictionary<string, long>(h.Value));
        foreach (var state in states)
        {
            var uncompacted = state.UncompactedFiles.ToList();
            var files = state.CoveredByDay ? uncompacted.Select(u => u.File).ToList() : state.CompactedFiles.Concat(uncompacted.Select(u => u.File)).ToList();
            foreach (var f in files) inputPaths.Add(_store.PathOf(state.Partition, f.Name));
            inputRows += files.Sum(f => f.Rows);
            inputCount += files.Count;

            var through = state.CoveredByDay
                ? hourThrough[state.Partition.Hour]
                : new Dictionary<string, long>(state.Generation?.Through ?? []);
            foreach (var (writer, file) in uncompacted) through[writer] = Math.Max(through.GetValueOrDefault(writer), file.Sequence);
            hourThrough[state.Partition.Hour] = through;
        }
        foreach (var f in day?.Files ?? [])
        {
            inputPaths.Add(_store.PathOf(dayPartition, f.Name));
            inputRows += f.Rows;
            inputCount++;
        }
        if (inputPaths.Count == 0) return false;

        var generation = (day?.Generation ?? 0) + 1;
        var outputs = await WriteCompactedAsync(table, dayPartition, generation, inputPaths, inputRows, ct);
        var manifest = NewGeneration(generation, outputs);
        manifest.HourThrough = hourThrough;
        if (!await PublishAsync(dayPartition, manifest, outputs, result, ct)) return false;

        result.Add("day");
        _metrics.RecordCompaction("day", _time.GetElapsedTime(started), inputCount);
        _log.LogInformation("Day merge of {Partition}: {Inputs} files -> {Outputs} (generation {Generation})", dayPartition, inputCount, outputs.Count, generation);
        return true;
    }

    private ParquetManifest NewGeneration(int generation, List<ParquetManifestFile> files) => new()
    {
        Writer = CompactorId,
        Generation = generation,
        CreatedAtUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds(),
        Files = files,
        NextSequence = 1
    };

    private async Task<bool> PublishAsync(
        ParquetPartition partition, ParquetManifest manifest, List<ParquetManifestFile> outputs, CompactionPassResult result, CancellationToken ct)
    {
        if (await _store.TryPublishManifestAsync(partition, ParquetManifest.GenerationFileName(manifest.Generation), manifest, ct)) return true;

        // Another compactor published this generation first: discard the work; the next pass starts from the newer one.
        foreach (var file in outputs)
            try { await _store.DeleteFileAsync(partition, file.Name, ct); }
            catch (IOException) { /* the orphan sweep takes it */ }
        result.AddLost();
        _log.LogInformation("Compaction of {Partition} lost generation {Generation} to another compactor; discarded {Files} files", partition, manifest.Generation, outputs.Count);
        return false;
    }

    // ------------------------------------------------------------------ the DuckDB side

    private DuckDBConnection Root()
    {
        lock (_engineLock)
        {
            if (_root is not null) return _root;
            var limits = _o.DuckDb;
            var temp = limits.TempDirectory ?? Path.Combine(_parquet.RootPath, "_tmp", Environment.MachineName + "-compactor");
            Directory.CreateDirectory(temp);
            var root = new DuckDBConnection("DataSource=:memory:");
            root.Open();
            root.Execute($"SET memory_limit='{limits.MemoryLimit}'");
            root.Execute($"SET threads={limits.Threads ?? Environment.ProcessorCount}");
            root.Execute($"SET temp_directory='{Escape(temp)}'");
            return _root = root;
        }
    }

    /// <summary>
    /// Writes <paramref name="inputPaths"/> as sorted compacted files into <paramref name="target"/> (committed under names unique to
    /// this compactor, listed by nobody yet) and returns their manifest entries. A derived table pre-sums; a bigger-than-
    /// <c>MaxSortRows</c> input is cut on the first column of the sort key into separately sorted ranges, so one sort stays bounded
    /// (phase 0 spike 2: dictionaries are not spilled, so memory, not time, is the limit); a range is split into files of
    /// <c>MaxFileRows</c>.
    /// </summary>
    internal async Task<List<ParquetManifestFile>> WriteCompactedAsync(
        ParquetTable table, ParquetPartition target, int generation, IReadOnlyList<string> inputPaths, long inputRows, CancellationToken ct)
        => await Task.Run(() =>
        {
            var outputs = new List<ParquetManifestFile>();
            using var conn = Root().Duplicate();
            conn.Open();
            var list = string.Join(", ", inputPaths.Select(p => $"'{Escape(p)}'"));
            var ranges = PlanRanges(conn, table, list, inputRows);
            var rowGroupsPerFile = Math.Max(1, (int)(_o.MaxFileRows / Math.Max(1, _parquet.RowGroupRows)));
            var committed = new List<string>();

            try
            {
                for (var r = 0; r < ranges.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    var directory = _store.NewTempPath(target, "compactor", $"c-{generation:D6}-{CompactorId}-{r}.d");
                    try
                    {
                        conn.Execute(
                            $"COPY ({SelectSql(table, list, ranges[r])}) TO '{Escape(directory)}' " +
                            $"(FORMAT parquet, COMPRESSION {_parquet.Compression.ToLowerInvariant()}, " +
                            $"ROW_GROUP_SIZE {_parquet.RowGroupRows.ToString(CultureInfo.InvariantCulture)}, " +
                            $"ROW_GROUPS_PER_FILE {rowGroupsPerFile.ToString(CultureInfo.InvariantCulture)})");

                        // The files of a range come out in sort order (data_0, data_1, ...): number them in that order.
                        var produced = Directory.GetFiles(directory, "*.parquet").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
                        foreach (var file in produced)
                        {
                            var name = $"c-{generation:D6}-{CompactorId}-{outputs.Count}.parquet";
                            var entry = Describe(conn, table, file, name);
                            if (entry.Rows == 0) continue;
                            _store.CommitFileAsync(target, file, name, ct).GetAwaiter().GetResult();
                            committed.Add(name);
                            outputs.Add(entry);
                        }
                    }
                    finally
                    {
                        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                        catch (IOException) { /* the temp sweep takes it */ }
                    }
                }
            }
            catch
            {
                foreach (var name in committed)
                    try { _store.DeleteFileAsync(target, name, CancellationToken.None).GetAwaiter().GetResult(); } catch (IOException) { }
                throw;
            }
            return outputs;
        }, ct);

    private ParquetManifestFile Describe(DuckDBConnection conn, ParquetTable table, string path, string name)
    {
        var column = table.PartitionTimeColumn;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*), min(\"{column}\"), max(\"{column}\") FROM read_parquet('{Escape(path)}')";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new ParquetManifestFile
        {
            Name = name,
            Rows = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Bytes = new FileInfo(path).Length,
            MinTime = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            MaxTime = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            CommittedAtUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds()
        };
    }

    /// <summary>Ranges (as SQL predicates on the sort key's first column) a sort is cut into: one open range unless the input is large.</summary>
    private List<string?> PlanRanges(DuckDBConnection conn, ParquetTable table, string fileList, long inputRows)
    {
        if (inputRows <= _o.MaxSortRows) return [null];
        var pieces = (int)Math.Min(1000, (inputRows + _o.MaxSortRows - 1) / _o.MaxSortRows);
        var column = table.FirstSortColumn;
        var quantiles = string.Join(", ", Enumerable.Range(1, pieces - 1).Select(i => ((double)i / pieces).ToString("R", CultureInfo.InvariantCulture)));
        var bounds = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT DISTINCT b FROM (SELECT unnest(quantile_disc(\"{column.Name}\", [{quantiles}])) AS b FROM read_parquet([{fileList}])) ORDER BY b";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) bounds.Add(Literal(column, reader.GetValue(0)));
        }
        if (bounds.Count == 0) return [null];

        var name = $"src.\"{column.Name}\"";
        var ranges = new List<string?> { $"{name} < {bounds[0]}" };
        for (var i = 0; i + 1 < bounds.Count; i++) ranges.Add($"{name} >= {bounds[i]} AND {name} < {bounds[i + 1]}");
        ranges.Add($"{name} >= {bounds[^1]}");
        return ranges;
    }

    private static string Literal(ParquetColumn column, object value)
        => column.Type == "VARCHAR"
            ? $"'{Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")}'"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";

    /// <summary>The sorted (and, for a derived table, pre-summed) rows of the inputs, optionally limited to a range of the first sort column.</summary>
    internal static string SelectSql(ParquetTable table, string fileList, string? rangePredicate)
    {
        var where = rangePredicate is null ? "" : $" WHERE {rangePredicate}";
        if (!table.PreSummed)
            return $"SELECT src.* FROM read_parquet([{fileList}]) src{where} ORDER BY {table.SortKey}";

        var select = string.Join(", ", table.Columns.Select(c => c.Merge switch
        {
            ParquetMerge.Sum => $"CAST(SUM(src.\"{c.Name}\") AS {c.Type}) AS \"{c.Name}\"",
            ParquetMerge.Max => $"MAX(src.\"{c.Name}\") AS \"{c.Name}\"",
            ParquetMerge.Min => $"MIN(src.\"{c.Name}\") AS \"{c.Name}\"",
            ParquetMerge.Any => $"ANY_VALUE(src.\"{c.Name}\") AS \"{c.Name}\"",
            _ => $"src.\"{c.Name}\" AS \"{c.Name}\""
        }));
        var groupBy = string.Join(", ", table.Columns.Where(c => c.Merge == ParquetMerge.Key).Select(c => $"src.\"{c.Name}\"")
            .Concat(table.MergeKeyExpressions ?? []));
        return $"SELECT {select} FROM read_parquet([{fileList}]) src{where} GROUP BY {groupBy} ORDER BY {table.SortKey}";
    }

    internal static string Escape(string value) => value.Replace("'", "''");

    // ------------------------------------------------------------------ deleting

    /// <summary>
    /// Deletes what is no longer read: files folded into a generation (or replaced by a later one) once that has been so for
    /// <c>GraceSeconds</c> (decision 7), files no manifest lists once they are older than the grace (at least an hour: a COPY can
    /// take minutes before its generation is published) and dead temp files; then generations older than the grace. Files go before
    /// the generations whose age proves them superseded.
    /// </summary>
    internal async Task CleanupAsync(IReadOnlyList<ParquetPartitionState> states, ParquetPartition dayPartition, CompactionPassResult result, CancellationToken ct)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var graceMs = _o.GraceSeconds * 1000L;
        var orphanMs = Math.Max(graceMs, 3_600_000L);
        var deleted = 0;

        var dayGens = await ParquetManifests.AllGenerationsAsync(_store, dayPartition, ct);
        foreach (var state in states)
        {
            var partition = state.Partition;
            var covered = state.CoveredByDay;
            var hourGens = await ParquetManifests.AllGenerationsAsync(_store, partition, ct);
            var writerManifests = await ParquetManifests.ReadWriterManifestsAsync(_store, partition, ct);
            var writers = writerManifests.Select(w => w.Writer).OrderByDescending(w => w.Length).ToList();
            var listed = writerManifests.SelectMany(w => w.Files.Select(f => f.Name)).ToHashSet(StringComparer.Ordinal);
            var newestFiles = (covered || hourGens.Count == 0 ? new List<ParquetManifestFile>() : hourGens[^1].Manifest.Files)
                .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var file in await _store.ListFilesAsync(partition, ct))
            {
                ct.ThrowIfCancellationRequested();
                bool delete;
                if (file.Name.StartsWith("part-", StringComparison.Ordinal))
                {
                    // Folded into a generation: gone once the first retained generation that holds it is past the grace (a writer that
                    // went idle never prunes its manifest, so being listed does not keep a folded file). Otherwise it is kept while a
                    // writer manifest lists it, and an orphan (a crash between the file's rename and its manifest entry) when none does.
                    var folded = default((string Name, ParquetManifest Manifest));
                    if (TryParseWriterFile(file.Name, writers, out var writer, out var sequence))
                        folded = covered
                            ? dayGens.FirstOrDefault(g => g.Manifest.HourThrough.TryGetValue(partition.Hour, out var t) && t.GetValueOrDefault(writer) >= sequence)
                            : hourGens.FirstOrDefault(g => g.Manifest.Through.GetValueOrDefault(writer) >= sequence);
                    if (folded.Manifest is not null) delete = folded.Manifest.CreatedAtUnixMs + graceMs <= nowMs;
                    else delete = !listed.Contains(file.Name) && file.ModifiedUnixMs + orphanMs <= nowMs;
                }
                else if (file.Name.StartsWith("c-", StringComparison.Ordinal))
                {
                    delete = covered
                        ? SupersededByDay(dayGens, partition.Hour, graceMs, nowMs)
                        : CompactedFileDeletable(file, hourGens, newestFiles, graceMs, orphanMs, nowMs);
                }
                else continue;

                if (delete && await TryDeleteAsync(partition, file.Name, ct)) deleted++;
            }

            deleted += await DeleteOldTempFilesAsync(partition, nowMs, orphanMs, ct);

            // Generations: an uncovered hour keeps its newest; a covered one keeps none once the day's switch is past the grace.
            foreach (var (name, gen) in hourGens)
            {
                var drop = covered
                    ? SupersededByDay(dayGens, partition.Hour, graceMs, nowMs)
                    : gen.Generation != hourGens[^1].Manifest.Generation && gen.CreatedAtUnixMs + graceMs <= nowMs;
                if (drop) await TryDeleteManifestAsync(partition, name, ct);
            }
        }

        // The day folder: compacted files outside the newest generation, and its older generations.
        if (dayGens.Count > 0)
        {
            var newest = dayGens[^1].Manifest;
            var newestFiles = newest.Files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var file in await _store.ListFilesAsync(dayPartition, ct))
            {
                if (!file.Name.StartsWith("c-", StringComparison.Ordinal)) continue;
                if (CompactedFileDeletable(file, dayGens, newestFiles, graceMs, orphanMs, nowMs) && await TryDeleteAsync(dayPartition, file.Name, ct)) deleted++;
            }
            foreach (var (name, gen) in dayGens)
                if (gen.Generation != newest.Generation && gen.CreatedAtUnixMs + graceMs <= nowMs)
                    await TryDeleteManifestAsync(dayPartition, name, ct);
            deleted += await DeleteOldTempFilesAsync(dayPartition, nowMs, orphanMs, ct);
        }

        if (deleted > 0)
        {
            result.AddDeleted(deleted);
            _metrics.RecordDeleted(deleted);
        }
    }

    private static bool SupersededByDay(IReadOnlyList<(string Name, ParquetManifest Manifest)> dayGens, int hour, long graceMs, long nowMs)
    {
        var first = dayGens.FirstOrDefault(g => g.Manifest.HourThrough.ContainsKey(hour));
        return first.Manifest is not null && first.Manifest.CreatedAtUnixMs + graceMs <= nowMs;
    }

    /// <summary>A compacted file outside the newest generation, once the generation that replaced it is past the grace; an unlisted one once it is an orphan.</summary>
    private static bool CompactedFileDeletable(
        ParquetFileInfo file, IReadOnlyList<(string Name, ParquetManifest Manifest)> generations, HashSet<string> newestFiles,
        long graceMs, long orphanMs, long nowMs)
    {
        if (newestFiles.Contains(file.Name)) return false;
        var lastListing = -1;
        for (var i = 0; i < generations.Count; i++)
            if (generations[i].Manifest.Files.Any(f => f.Name == file.Name)) lastListing = i;
        if (lastListing < 0) return file.ModifiedUnixMs + orphanMs <= nowMs;
        return lastListing + 1 < generations.Count && generations[lastListing + 1].Manifest.CreatedAtUnixMs + graceMs <= nowMs;
    }

    /// <summary>
    /// Splits <c>part-&lt;writer&gt;-&lt;seq&gt;-&lt;token&gt;.parquet</c>. Writer ids and tokens may both hold '-', so the writers whose
    /// manifests are in the folder (longest id first) say where the id ends.
    /// </summary>
    internal static bool TryParseWriterFile(string name, IReadOnlyList<string> writers, out string writer, out long sequence)
    {
        foreach (var candidate in writers)
        {
            var prefix = $"part-{candidate}-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = name.AsSpan(prefix.Length);
            var dash = rest.IndexOf('-');
            if (dash > 0 && long.TryParse(rest[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
            {
                writer = candidate;
                return true;
            }
        }
        writer = "";
        sequence = 0;
        return false;
    }

    private async Task<int> DeleteOldTempFilesAsync(ParquetPartition partition, long nowMs, long orphanMs, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var temp in await _store.ListTempFilesAsync(partition, ct))
        {
            if (temp.ModifiedUnixMs + orphanMs > nowMs) continue;
            try { await _store.DeleteTempFileAsync(partition, temp.Name, ct); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use or already gone */ }
        }
        return deleted;
    }

    private async Task<bool> TryDeleteAsync(ParquetPartition partition, string name, CancellationToken ct)
    {
        try { await _store.DeleteFileAsync(partition, name, ct); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not delete {Name} in {Partition}; it is tried again next pass", name, partition);
            return false;
        }
    }

    private async Task TryDeleteManifestAsync(ParquetPartition partition, string name, CancellationToken ct)
        => await TryDeleteAsync(partition, name, ct);

    public void Dispose()
    {
        lock (_engineLock) { _root?.Dispose(); _root = null; }
    }
}
