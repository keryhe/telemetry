using System.Collections.Concurrent;
using DuckDB.NET.Data;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

/// <summary>The real clock plus an offset a test moves, so closed hours, ages and grace periods can be crossed without waiting.</summary>
internal sealed class ShiftedTime : TimeProvider
{
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
}

public class CompactionTests
{
    /// <summary>A flush of spans writes the spans, their trace index and their request rollup: a pass counts one compaction per table.</summary>
    private const int Tables = 3;

    private static long NowNano() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

    /// <summary>A compactor over the fixture's root. Defaults compact aggressively; <paramref name="tweak"/> restores the rule under test.</summary>
    private static ParquetCompactor CompactorFor(ParquetFixture fx, ShiftedTime? time = null, Action<CompactionOptions>? tweak = null, int rowGroupRows = 122_880)
    {
        var options = new ParquetOptions { RootPath = fx.Root, RowGroupRows = rowGroupRows };
        options.Compaction.MinorFileThreshold = 1;
        options.Compaction.MinorAgeSeconds = 0;
        options.Compaction.CloseHourGraceMinutes = 0;
        options.Compaction.CloseDayGraceHours = 0;
        options.Compaction.GraceSeconds = 0;
        options.Compaction.DuckDb = new DuckDbOptions { MemoryLimit = "1GB", Threads = 2 };
        tweak?.Invoke(options.Compaction);
        return new ParquetCompactor(fx.Store, Options.Create(options), time ?? new ShiftedTime(), NullLogger<ParquetCompactor>.Instance, new ParquetMetrics());
    }

    private static async Task WriteFlushesAsync(ParquetFixture fx, ResourceModel resource, long startNano, int flushes, int spansPerFlush, Func<int, int, string>? traceOf = null)
    {
        for (var i = 0; i < flushes; i++)
        {
            var spans = Enumerable.Range(0, spansPerFlush).Select(j =>
                Span(resource, startNano + i * 1_000L + j, "op", traceOf?.Invoke(i, j), Guid.NewGuid().ToString("N")[..16])).ToList();
            await fx.Writer.FlushTracesAsync(spans);
        }
    }

    private static List<ParquetPartition> Hours(ParquetFixture fx, string table, long tenant = 1)
        => [.. fx.Store.EnumeratePartitions(table, tenant, DateOnly.MinValue, DateOnly.MaxValue)];

    private static async Task<List<ParquetResolvedFile>> ResolvedAsync(ParquetFixture fx, string table, long tenant = 1)
        => [.. await ParquetManifests.ResolveAsync(fx.Store, Hours(fx, table, tenant), default)];

    private static async Task<long> RowsAsync(ParquetFixture fx, string table, string select = "count(*)")
    {
        var files = (await ResolvedAsync(fx, table)).Select(f => f.PathIn(fx.Store)).ToList();
        return files.Count == 0 ? 0 : ParquetFixture.Scalar<long>(files, $"CAST({select} AS BIGINT)");
    }

    private static int DiskFiles(ParquetFixture fx, string table) => fx.AllParquetFiles(table).Length;

    // -------------------------------------------------------------------- minor, major, day

    [Fact]
    public async Task MinorCompaction_FoldsTheOpenHoursWriterFilesIntoOne_WithTheSameRows()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, tweak: o => o.CloseHourGraceMinutes = 100_000);
        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), flushes: 5, spansPerFlush: 4);
        Assert.Equal(5, (await ResolvedAsync(fx, "spans")).Count);

        var result = await compactor.RunPassAsync(fullScan: true, default);

        Assert.Equal(Tables, result.Minor);
        Assert.Equal(0, result.Failures);
        var resolved = await ResolvedAsync(fx, "spans");
        Assert.Single(resolved);
        Assert.StartsWith("c-000001-", resolved[0].File.Name);
        Assert.Equal(20, resolved[0].File.Rows);
        Assert.Equal(20, await RowsAsync(fx, "spans"));
        Assert.Equal(20, await RowsAsync(fx, "spans", "count(DISTINCT span_id)"));

        var partition = Hours(fx, "spans").Single();
        var generation = (await ParquetManifests.NewestGenerationAsync(fx.Store, partition, default))!;
        Assert.Equal(1, generation.Generation);
        Assert.Equal(5, generation.Through["w1"]);
    }

    [Fact]
    public async Task MinorCompaction_LeavesALoneFileAlone_AndOnlyFoldsTheFilesAboveTheThroughNextTime()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, tweak: o => o.CloseHourGraceMinutes = 100_000);
        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), 1, 3);
        Assert.Equal(0, (await compactor.RunPassAsync(true, default)).Minor);

        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), 3, 3);
        Assert.Equal(Tables, (await compactor.RunPassAsync(true, default)).Minor);
        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), 2, 3);
        Assert.Equal(Tables, (await compactor.RunPassAsync(true, default)).Minor);

        // generation 2 adds a compacted file beside generation 1's: nothing was rewritten
        var resolved = await ResolvedAsync(fx, "spans");
        Assert.Equal(2, resolved.Count);
        Assert.Equal(18, await RowsAsync(fx, "spans"));
        Assert.Equal(2, (await ParquetManifests.NewestGenerationAsync(fx.Store, Hours(fx, "spans").Single(), default))!.Generation);
    }

    [Fact]
    public async Task MajorCompaction_RewritesAClosedHourIntoOneSortedFile_AndDeletesTheOldFilesAfterTheGrace()
    {
        var time = new ShiftedTime();
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, time, o => { o.CloseDayGraceHours = 100_000; o.GraceSeconds = 600; });
        var a = Resource(1, "svc-b");
        var b = Resource(1, "svc-a");
        await WriteFlushesAsync(fx, a, BaseNano, 4, 5);
        await WriteFlushesAsync(fx, b, BaseNano, 4, 5);
        Assert.Equal(8, DiskFiles(fx, "spans"));

        var result = await compactor.RunPassAsync(true, default);
        Assert.Equal(Tables, result.Major);
        var resolved = await ResolvedAsync(fx, "spans");
        Assert.Single(resolved);
        Assert.Equal(40, await RowsAsync(fx, "spans"));

        // sorted by the table's key: services in order, then start
        Assert.Equal(0L, OutOfOrderRows(resolved[0].PathIn(fx.Store)));

        // the folded writer files are still on disk inside the grace period ...
        Assert.Equal(9, DiskFiles(fx, "spans"));
        // ... and gone after it
        time.Offset = TimeSpan.FromSeconds(601);
        var cleanup = await compactor.RunPassAsync(true, default);
        Assert.Equal(Tables * 8, cleanup.FilesDeleted);
        Assert.Equal(1, DiskFiles(fx, "spans"));
        Assert.Equal(40, await RowsAsync(fx, "spans"));
    }

    [Fact]
    public async Task DayMerge_MovesAClosedDaysHoursIntoOneDayFileSet_WithTheSameRows()
    {
        var time = new ShiftedTime();
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, time, o => o.GraceSeconds = 600);
        var r = Resource(1, "svc");
        var hour0 = BaseNano - BaseNano % (24 * Hour);
        await WriteFlushesAsync(fx, r, hour0 + 10, 3, 4);
        await WriteFlushesAsync(fx, r, hour0 + Hour + 10, 3, 4);
        await WriteFlushesAsync(fx, r, hour0 + 2 * Hour + 10, 2, 4);
        Assert.Equal(3, Hours(fx, "spans").Count);

        var result = await compactor.RunPassAsync(true, default);

        Assert.Equal(Tables, result.Day);
        var resolved = await ResolvedAsync(fx, "spans");
        Assert.Single(resolved);
        Assert.True(resolved[0].Partition.IsDay);
        Assert.Equal(32, await RowsAsync(fx, "spans"));

        var day = (await ParquetManifests.NewestGenerationAsync(fx.Store, Hours(fx, "spans")[0].DayPartition, default))!;
        Assert.Equal([0, 1, 2], day.HourThrough.Keys.Order());

        // the hour folders' files go after the grace, the day's stay
        time.Offset = TimeSpan.FromSeconds(601);
        await compactor.RunPassAsync(true, default);
        Assert.Single(fx.AllParquetFiles("spans"));
        Assert.Equal(32, await RowsAsync(fx, "spans"));

        // a pass over a settled day does nothing
        var idle = await compactor.RunPassAsync(true, default);
        Assert.Equal(0, idle.Minor + idle.Major + idle.Day);
    }

    [Fact]
    public async Task ALateFileAfterADayMerge_IsReadAtOnce_AndMergedByTheNextDayPass()
    {
        var time = new ShiftedTime();
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, time);
        var r = Resource(1, "svc");
        await WriteFlushesAsync(fx, r, BaseNano, 3, 4);
        await compactor.RunPassAsync(true, default);
        Assert.Equal(12, await RowsAsync(fx, "spans"));

        await WriteFlushesAsync(fx, r, BaseNano + 5, 1, 7);
        var resolved = await ResolvedAsync(fx, "spans");
        Assert.Equal(2, resolved.Count);                       // the day's file and the late writer file
        Assert.Equal(19, await RowsAsync(fx, "spans"));

        var result = await compactor.RunPassAsync(true, default);
        Assert.Equal(Tables, result.Day);
        Assert.Single(await ResolvedAsync(fx, "spans"));
        Assert.Equal(19, await RowsAsync(fx, "spans"));
        Assert.Equal(2, (await ParquetManifests.NewestGenerationAsync(fx.Store, Hours(fx, "spans").Single().DayPartition, default))!.Generation);
    }

    // -------------------------------------------------------------------- derived tables

    [Fact]
    public async Task DerivedTables_ArePreSummed_ByKey_WithTheSameSums()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, tweak: o => o.CloseDayGraceHours = 100_000);
        var r = Resource(1, "svc");
        // two traces seen in three flushes each: the same (tenant, trace, service, hour) arrives as three partial index rows;
        // the spans of one operation in one minute arrive as three partial rollup rows.
        for (var i = 0; i < 3; i++)
        {
            var spans = new List<SpanModel>();
            foreach (var trace in new[] { "00000000000000000000000000000001", "00000000000000000000000000000002" })
            {
                var s = Span(r, BaseNano + i * 1_000L, "GET /x", trace, Guid.NewGuid().ToString("N")[..16]);
                s.EndTimeUnixNano = s.StartTimeUnixNano + 2_000_000;
                if (i == 2 && trace.EndsWith("2")) s.StatusCode = SpanStatusCode.ERROR;
                spans.Add(s);
            }
            await fx.Writer.FlushTracesAsync(spans);
        }

        var indexRows = await RowsAsync(fx, "trace_index");
        var indexSpans = await RowsAsync(fx, "trace_index", "sum(span_count)");
        var rollupRows = await RowsAsync(fx, "request_rollup_minute");
        var rollupRequests = await RowsAsync(fx, "request_rollup_minute", "sum(request_count)");
        var rollupErrors = await RowsAsync(fx, "request_rollup_minute", "sum(error_count)");
        var rollupBands = await RowsAsync(fx, "request_rollup_minute", "sum(h00 + h01 + h02 + h03 + h04 + h05 + h06 + h07 + h08 + h09 + h10 + h11 + h12 + h13 + h14 + h15 + h16 + h17 + h18 + h19 + h20 + h21 + h22 + h23)");
        Assert.Equal(6, indexRows);
        Assert.Equal(6, rollupRequests);

        await compactor.RunPassAsync(true, default);

        Assert.Equal(2, await RowsAsync(fx, "trace_index"));                 // one per trace
        Assert.Equal(indexSpans, await RowsAsync(fx, "trace_index", "sum(span_count)"));
        Assert.Equal(1, await RowsAsync(fx, "request_rollup_minute"));       // one per service, minute and operation
        Assert.Equal(rollupRequests, await RowsAsync(fx, "request_rollup_minute", "sum(request_count)"));
        Assert.Equal(rollupErrors, await RowsAsync(fx, "request_rollup_minute", "sum(error_count)"));
        Assert.Equal(rollupBands, await RowsAsync(fx, "request_rollup_minute", "sum(h00 + h01 + h02 + h03 + h04 + h05 + h06 + h07 + h08 + h09 + h10 + h11 + h12 + h13 + h14 + h15 + h16 + h17 + h18 + h19 + h20 + h21 + h22 + h23)"));
        Assert.Equal(1L, await RowsAsync(fx, "trace_index", "sum(has_error) FILTER (trace_lo = 2)"));
        Assert.True(indexRows > 2 && rollupRows > 1);
        Assert.Equal(BaseNano, await RowsAsync(fx, "trace_index", "min(start_min)"));
    }

    [Fact]
    public async Task ATraceIndexRowKeepsItsHour_WhenTheDayIsMerged()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx);
        var r = Resource(1, "svc");
        var hour0 = BaseNano - BaseNano % Hour;
        const string trace = "00000000000000000000000000000009";
        await fx.Writer.FlushTracesAsync([Span(r, hour0 + 10, "a", trace, "0000000000000001"), Span(r, hour0 + Hour + 10, "b", trace, "0000000000000002")]);
        Assert.Equal(2, await RowsAsync(fx, "trace_index"));

        await compactor.RunPassAsync(true, default);

        Assert.Equal(2, await RowsAsync(fx, "trace_index"));                 // an hour's row means the same before and after
    }

    // -------------------------------------------------------------------- bounds: file size and sort size

    [Fact]
    public async Task ACompactionSplitsAtMaxFileRows_InSortOrder_AndCutsALargeSortIntoRanges()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, tweak: o =>
        {
            o.CloseDayGraceHours = 100_000;
            o.MaxFileRows = 4_096;
            o.MaxSortRows = 5_000;
        }, rowGroupRows: 2_048);
        // 3 services x 3,000 spans = 9,000 rows in 9 files: two sort ranges, several files
        foreach (var service in new[] { "svc-a", "svc-b", "svc-c" })
            await WriteFlushesAsync(fx, Resource(1, service), BaseNano, 3, 1_000);

        var result = await compactor.RunPassAsync(true, default);

        Assert.Equal(Tables, result.Major);
        var files = await ResolvedAsync(fx, "spans");
        Assert.True(files.Count >= 3, $"expected the 9,000 rows split into several files, got {files.Count}");
        Assert.All(files, f => Assert.True(f.File.Rows <= 4_096));
        Assert.Equal(9_000, files.Sum(f => f.File.Rows));
        Assert.Equal(9_000, await RowsAsync(fx, "spans"));
        Assert.Equal(9_000, await RowsAsync(fx, "spans", "count(DISTINCT span_id)"));

        foreach (var file in files)
        {
            var path = file.PathIn(fx.Store);
            Assert.Equal(0L, OutOfOrderRows(path));
            Assert.Equal(file.File.Rows, ParquetFixture.Scalar<long>([path], "count(*)"));
        }

        // the files, in the order the manifest lists them, are one sorted run
        var ranges = files.Select(f => KeyRange(f.PathIn(fx.Store))).ToList();
        for (var i = 1; i < ranges.Count; i++)
            Assert.True(string.CompareOrdinal(ranges[i - 1].Max, ranges[i].Min) <= 0, $"file {i} starts before file {i - 1} ends");
    }

    /// <summary>The smallest and largest sort key (service, start) of a file.</summary>
    private static (string Min, string Max) KeyRange(string path)
    {
        const string key = "service_name || '|' || lpad(CAST(start_time_unix_nano AS VARCHAR), 20, '0')";
        return (ParquetFixture.Scalar<string>([path], $"min({key})"), ParquetFixture.Scalar<string>([path], $"max({key})"));
    }

    /// <summary>Rows that sort before the row above them in the file's own order (service, then start).</summary>
    private static long OutOfOrderRows(string path)
    {
        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        var p = path.Replace("'", "''");
        return conn.ExecuteScalar<long>(
            $"WITH t AS (SELECT service_name s, start_time_unix_nano t, row_number() OVER () rn FROM read_parquet('{p}')) " +
            "SELECT count(*) FROM t a JOIN t b ON b.rn = a.rn + 1 WHERE (b.s, b.t) < (a.s, a.t)");
    }

    // -------------------------------------------------------------------- publishing, races, leases

    [Fact]
    public async Task ACompactorThatPublishesAfterAnotherDiscardsItsWork()
    {
        using var fx = new ParquetFixture();
        // the grace keeps the files the stale compactor still reads
        using var first = CompactorFor(fx, tweak: o => { o.CloseDayGraceHours = 100_000; o.GraceSeconds = 600; });
        using var second = CompactorFor(fx, tweak: o => { o.CloseDayGraceHours = 100_000; o.GraceSeconds = 600; });
        await WriteFlushesAsync(fx, Resource(1, "svc"), BaseNano, 4, 5);
        var partition = Hours(fx, "spans").Single();
        var stale = (await ParquetManifests.ReadStatesAsync(fx.Store, [partition], default)).Single();   // both read generation 0

        Assert.Equal(Tables, (await first.RunPassAsync(true, default)).Major);
        var before = DiskFiles(fx, "spans");   // the writer files (still inside the grace) plus the winner's file

        var lost = new CompactionPassResult();
        Assert.False(await second.CompactHourAsync(ParquetTables.SpanTable, stale, "major", lost, default));

        Assert.Equal(1, lost.Lost);
        Assert.Equal(before, DiskFiles(fx, "spans"));                            // the loser's files are gone again
        Assert.Equal(20, await RowsAsync(fx, "spans"));
        Assert.Single(await ResolvedAsync(fx, "spans"));
    }

    [Fact]
    public async Task TheLeaseGoesToOneInstance_IsRenewed_ExpiresAndIsReleased()
    {
        using var fx = new ParquetFixture();
        var time = new ShiftedTime();
        var options = Options.Create(new ParquetOptions { RootPath = fx.Root });
        var a = new ParquetCompactionLease(fx.Store, options, time);
        var b = new ParquetCompactionLease(fx.Store, options, time);

        Assert.True(await a.TryAcquireOrRenewAsync(default));
        Assert.False(await b.TryAcquireOrRenewAsync(default));
        Assert.True(await a.TryAcquireOrRenewAsync(default));                    // renewing keeps it

        time.Offset = TimeSpan.FromSeconds(options.Value.Compaction.LeaseSeconds - 5);
        Assert.False(await b.TryAcquireOrRenewAsync(default));
        time.Offset = TimeSpan.FromSeconds(options.Value.Compaction.LeaseSeconds + 5);
        Assert.True(await b.TryAcquireOrRenewAsync(default));                    // a stopped renewing: b takes over
        Assert.False(await a.TryAcquireOrRenewAsync(default));

        await b.ReleaseAsync(default);
        Assert.True(await a.TryAcquireOrRenewAsync(default));                    // released: free at once
    }

    [Fact]
    public async Task AWriterDropsItsFoldedManifestEntriesOnItsNextCommit()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, tweak: o => o.CloseHourGraceMinutes = 100_000);
        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), 4, 3);
        await compactor.RunPassAsync(true, default);
        var partition = Hours(fx, "spans").Single();
        Assert.Equal(4, (await fx.Store.ReadManifestAsync(partition, "_manifest.w1.json", default))!.Files.Count);

        await WriteFlushesAsync(fx, Resource(1, "svc"), NowNano(), 1, 3);

        var manifest = (await fx.Store.ReadManifestAsync(partition, "_manifest.w1.json", default))!;
        Assert.Equal([5L], manifest.Files.Select(f => f.Sequence));
        Assert.Equal(6, manifest.NextSequence);                                   // a sequence number is never reused
        Assert.Equal(15, await RowsAsync(fx, "spans"));
    }

    [Fact]
    public async Task OrphansAndDeadTempFiles_AreDeletedOnlyOnceTheyAreOldEnough()
    {
        var time = new ShiftedTime();
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx, time, o => o.CloseDayGraceHours = 100_000);
        await WriteFlushesAsync(fx, Resource(1, "svc"), BaseNano, 2, 3);
        var partition = Hours(fx, "spans").Single();
        var dir = Path.GetDirectoryName(fx.Store.PathOf(partition, "x"))!;
        var orphan = Path.Combine(dir, "part-w1-99-deadbeef.parquet");
        File.Copy(fx.Store.PathOf(partition, (await fx.FilesAsync(partition))[0]), orphan);
        var tempDir = Path.Combine(dir, ".tmp");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, "w9.abc.part-x.parquet");
        File.WriteAllText(temp, "partial");

        await compactor.RunPassAsync(true, default);
        Assert.True(File.Exists(orphan));                                         // just written: a commit may be in flight
        Assert.True(File.Exists(temp));

        time.Offset = TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow - TimeSpan.FromHours(2));
        await compactor.RunPassAsync(true, default);

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(temp));
        Assert.Equal(6, await RowsAsync(fx, "spans"));
    }

    [Fact]
    public async Task TheRetentionSweepCountsCompactedRowsOnce()
    {
        using var fx = new ParquetFixture();
        using var compactor = CompactorFor(fx);
        await WriteFlushesAsync(fx, Resource(1, "svc"), BaseNano, 3, 4);
        await compactor.RunPassAsync(true, default);                              // day-merged
        await WriteFlushesAsync(fx, Resource(1, "svc"), BaseNano, 1, 5);          // plus a late file

        var removed = await new ParquetRetentionSweeper(fx.Store).DeleteOldTracesAsync(TimeSpan.FromDays(30));

        Assert.Equal(17, removed);
        Assert.Empty(fx.Store.EnumeratePartitions("spans", null, DateOnly.MinValue, DateOnly.MaxValue));
    }

    // -------------------------------------------------------------------- concurrency

    [Fact]
    public async Task WritersCompactorsAndReaders_NeverLoseOrDoubleCountARow()
    {
        using var fx = new ParquetFixture("w1");
        using var other = new ParquetFixture2(fx.Root, "w2");
        // open hour, so minor compactions fold under the writers; a short grace so deletes happen during the run
        Action<CompactionOptions> tweak = o => { o.CloseHourGraceMinutes = 100_000; o.CloseDayGraceHours = 100_000; o.GraceSeconds = 1; };
        using var c1 = CompactorFor(fx, tweak: tweak);
        using var c2 = CompactorFor(fx, tweak: tweak);

        long started = 0, committed = 0;
        var errors = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ct = stop.Token;
        var resource = Resource(1, "svc");

        Task Writer(ParquetBulkWriter writer) => Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var spans = Enumerable.Range(0, 5).Select(_ => Span(resource, NowNano(), "op")).ToList();
                    Interlocked.Add(ref started, 5);
                    await writer.FlushTracesAsync(spans);
                    Interlocked.Add(ref committed, 5);
                }
            }
            catch (Exception ex) { errors.Enqueue("writer: " + ex.Message); }
        });

        Task Compactor(ParquetCompactor c) => Task.Run(async () =>
        {
            try { while (!ct.IsCancellationRequested) { await c.RunPassAsync(true, default); await Task.Delay(50); } }
            catch (Exception ex) { errors.Enqueue("compactor: " + ex.Message); }
        });

        var reads = 0;
        Task Reader() => Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var before = Interlocked.Read(ref committed);
                    var files = (await ResolvedAsync(fx, "spans")).Select(f => f.PathIn(fx.Store)).ToList();
                    if (files.Count == 0) continue;
                    await Task.Delay(Random.Shared.Next(0, 30));
                    var count = ParquetFixture.Scalar<long>(files, "count(*)");
                    var distinct = ParquetFixture.Scalar<long>(files, "count(DISTINCT span_id)");
                    var after = Interlocked.Read(ref started);
                    Interlocked.Increment(ref reads);
                    if (count != distinct) errors.Enqueue($"double counted: {count} rows, {distinct} distinct");
                    if (count < before) errors.Enqueue($"lost rows: read {count}, {before} were committed before the read");
                    if (count > after) errors.Enqueue($"phantom rows: read {count}, only {after} were started");
                }
            }
            catch (Exception ex) { errors.Enqueue("reader: " + ex.Message); }
        });

        await Task.WhenAll(Writer(fx.Writer), Writer(other.Writer), Compactor(c1), Compactor(c2), Reader(), Reader());

        Assert.Empty(errors);
        Assert.True(reads > 20, $"only {reads} reads ran");
        Assert.Equal(Interlocked.Read(ref committed), await RowsAsync(fx, "spans"));
    }
}
