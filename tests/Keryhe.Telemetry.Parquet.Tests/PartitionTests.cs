using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

public class PartitionTests
{
    [Fact]
    public async Task AFlushAcrossTwoHoursAndTwoTenantsWritesFourPartitionsWithOneFileEach()
    {
        using var fx = new ParquetFixture();
        var t1 = Resource(1, "svc");
        var t2 = Resource(2, "svc");
        var hour0 = BaseNano - BaseNano % Hour; // start of the hour BaseNano falls in
        await fx.Writer.FlushTracesAsync([
            Span(t1, hour0 + 10), Span(t1, hour0 + 20), Span(t1, hour0 + Hour + 5),
            Span(t2, hour0 + 30), Span(t2, hour0 + Hour + 6), Span(t2, hour0 + Hour + 7), Span(t2, hour0 + Hour + 8)]);

        var partitions = fx.Store.EnumeratePartitions("spans", null, DateOnly.MinValue, DateOnly.MaxValue)
            .OrderBy(p => p.TenantId).ThenBy(p => p.Date).ThenBy(p => p.Hour).ToList();
        Assert.Equal(4, partitions.Count);
        Assert.Equal([1L, 1, 2, 2], partitions.Select(p => p.TenantId));

        var expectedRows = new[] { 2L, 1, 1, 3 };
        for (var i = 0; i < 4; i++)
        {
            var names = await fx.Store.ListManifestNamesAsync(partitions[i], default);
            Assert.Equal(["_manifest.w1.json"], names);
            var manifest = (await fx.Store.ReadManifestAsync(partitions[i], names[0], default))!;
            var file = Assert.Single(manifest.Files);
            Assert.Equal(1, file.Sequence);
            Assert.Equal(expectedRows[i], file.Rows);
            Assert.True(file.Bytes > 0);
            Assert.True(file.MinTime <= file.MaxTime);
            Assert.True(File.Exists(fx.Store.PathOf(partitions[i], file.Name)));
            // The manifest's bounds are the file's actual bounds and fall inside the partition's hour.
            var path = fx.Store.PathOf(partitions[i], file.Name);
            Assert.Equal(file.MinTime, ParquetFixture.Scalar<long>([path], "min(start_time_unix_nano)"));
            Assert.Equal(file.MaxTime, ParquetFixture.Scalar<long>([path], "max(start_time_unix_nano)"));
            Assert.Equal(ParquetPartition.For("spans", partitions[i].TenantId, file.MinTime), partitions[i]);
            Assert.Equal(expectedRows[i], ParquetFixture.Scalar<long>([path], "count(*)"));
        }
        Assert.Equal(4, fx.AllParquetFiles("spans").Length);
    }

    [Fact]
    public async Task RetryingAFlushWithTheSameTokenStoresEachPartitionOnce_ANewTokenAddsFiles()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var hour0 = BaseNano - BaseNano % Hour;
        var spans = new List<Keryhe.Telemetry.Core.Models.SpanModel>
        {
            Span(r, hour0 + 10, spanId: "0000000000000001"), Span(r, hour0 + Hour + 10, spanId: "0000000000000002")
        };

        await fx.Writer.FlushTracesAsync(spans, "batch1");
        await fx.Writer.FlushTracesAsync(spans, "batch1");

        Assert.Equal(2, fx.AllParquetFiles("spans").Length);
        Assert.Equal(2L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("spans"), "count(*)"));
        foreach (var p in fx.Store.EnumeratePartitions("spans", 1, DateOnly.MinValue, DateOnly.MaxValue))
        {
            var files = await fx.FilesAsync(p);
            var name = Assert.Single(files);
            Assert.Contains("-1-batch1.parquet", name);
        }

        await fx.Writer.FlushTracesAsync(spans, "batch2");

        Assert.Equal(4, fx.AllParquetFiles("spans").Length);
        foreach (var p in fx.Store.EnumeratePartitions("spans", 1, DateOnly.MinValue, DateOnly.MaxValue))
        {
            var manifest = (await fx.Store.ReadManifestAsync(p, "_manifest.w1.json", default))!;
            Assert.Equal([1L, 2], manifest.Files.Select(f => f.Sequence));
            Assert.Equal(3, manifest.NextSequence);
            Assert.Equal(["batch1", "batch2"], manifest.Files.Select(f => f.Token));
        }
        Assert.Equal(4L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("spans"), "count(*)"));
    }

    [Fact]
    public async Task ARetryAfterAPartialFailureRewritesTheCommittedPartitionsAndCompletesTheRest()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var hour0 = BaseNano - BaseNano % Hour;
        var spans = new List<Keryhe.Telemetry.Core.Models.SpanModel> { Span(r, hour0 + 10), Span(r, hour0 + Hour + 10) };

        // The first partition commits; the flush then fails before the second (only the first is written).
        await fx.Writer.FlushTracesAsync([spans[0]], "tok");
        Assert.Single(fx.AllParquetFiles("spans"));

        await fx.Writer.FlushTracesAsync(spans, "tok");
        Assert.Equal(2, fx.AllParquetFiles("spans").Length);
        Assert.Equal(2L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("spans"), "count(*)"));
    }

    [Fact]
    public async Task AFileBeingWrittenOrCommittedWithoutAManifestEntryIsNotListed()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        await fx.Writer.FlushTracesAsync([Span(r, BaseNano)], "good");
        var partition = fx.Store.EnumeratePartitions("spans", 1, DateOnly.MinValue, DateOnly.MaxValue).Single();

        // A file being written sits in .tmp; a crash between the rename and the manifest rewrite leaves a committed,
        // unlisted file.
        var temp = fx.Store.NewTempPath(partition, "w1", "inflight.parquet");
        File.WriteAllText(temp, "partial");
        File.WriteAllText(fx.Store.PathOf(partition, "part-w1-2-orphan.parquet"), "orphan");

        var listed = await fx.FilesAsync(partition);
        Assert.Single(listed);
        Assert.Contains("good", listed[0]);

        // A restart removes this writer's temp files; the orphan is the compactor's to sweep (phase 4).
        Assert.True(fx.Store.DeleteTempFiles("w1") >= 1);
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(fx.Store.PathOf(partition, "part-w1-2-orphan.parquet")));
    }

    [Fact]
    public async Task TwoWritersKeepSeparateManifestsInOnePartition()
    {
        using var fx = new ParquetFixture("w1");
        using var other = new ParquetFixture2(fx.Root, "w2");
        var r = Resource(1, "svc");
        await fx.Writer.FlushTracesAsync([Span(r, BaseNano)]);
        await other.Writer.FlushTracesAsync([Span(r, BaseNano + 1)]);

        var partition = fx.Store.EnumeratePartitions("spans", 1, DateOnly.MinValue, DateOnly.MaxValue).Single();
        Assert.Equal(["_manifest.w1.json", "_manifest.w2.json"], await fx.Store.ListManifestNamesAsync(partition, default));
        Assert.Equal(2, (await fx.FilesAsync(partition)).Count);
    }

    [Fact]
    public async Task AnExclusiveManifestPublishLosesToTheFirstPublisher()
    {
        using var fx = new ParquetFixture();
        var p = new ParquetPartition("spans", 1, new DateOnly(2026, 1, 1), 0);
        Assert.True(await fx.Store.TryPublishManifestAsync(p, "_compacted.000001.json", new ParquetManifest { Writer = "a" }, default));
        Assert.False(await fx.Store.TryPublishManifestAsync(p, "_compacted.000001.json", new ParquetManifest { Writer = "b" }, default));
        Assert.Equal("a", (await fx.Store.ReadManifestAsync(p, "_compacted.000001.json", default))!.Writer);
    }

    [Fact]
    public async Task DeleteDayRemovesOnlyThatTenantsDay()
    {
        using var fx = new ParquetFixture();
        await fx.Writer.FlushTracesAsync([Span(Resource(1, "s"), BaseNano), Span(Resource(2, "s"), BaseNano)]);
        var p = ParquetPartition.For("spans", 1, BaseNano);
        Assert.True(await fx.Store.DeleteDayAsync("spans", 1, p.Date, default));
        Assert.False(await fx.Store.DeleteDayAsync("spans", 1, p.Date, default));
        Assert.Single(fx.Store.EnumeratePartitions("spans", null, DateOnly.MinValue, DateOnly.MaxValue));
    }
}

/// <summary>A second collector on an existing root.</summary>
internal sealed class ParquetFixture2 : IDisposable
{
    public ParquetBulkWriter Writer { get; }
    private readonly ParquetWriterIdentity _identity;
    private readonly ParquetFileWriter _files;

    public ParquetFixture2(string root, string writerId)
    {
        var store = new LocalParquetFileStore(root);
        _identity = ParquetWriterIdentity.Acquire(root, writerId);
        _files = new ParquetFileWriter(new ParquetOptions { RootPath = root }, Path.Combine(root, "_tmp2"));
        Writer = new ParquetBulkWriter(store, _files, new ManifestWriter(store, _identity.Id),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ParquetBulkWriter>.Instance);
    }

    public void Dispose()
    {
        _files.Dispose();
        _identity.Dispose();
    }
}
