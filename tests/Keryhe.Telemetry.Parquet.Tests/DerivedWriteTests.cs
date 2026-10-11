using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

public class DerivedWriteTests
{
    private static SpanModel S(ResourceModel r, string trace, string span, long start, long durationNs, SpanKind kind = SpanKind.SERVER,
        SpanStatusCode status = SpanStatusCode.OK, string name = "op")
    {
        var s = Span(r, start, name, trace, span);
        s.EndTimeUnixNano = start + durationNs;
        s.Kind = kind;
        s.StatusCode = status;
        return s;
    }

    [Fact]
    public async Task TraceIndexAndRequestRollup_MatchALinqRestatementOfTheSeededSpans()
    {
        using var fx = new ParquetFixture();
        var a = Resource(1, "svc-a");
        var b = Resource(1, "svc-b");
        var hour0 = BaseNano - BaseNano % Hour;
        var t1 = "00000000000000000000000000000001";
        var t2 = "ffffffffffffffff0000000000000002";
        var spans = new List<SpanModel>
        {
            S(a, t1, "0000000000000001", hour0 + 1_000, 5_000_000),
            S(b, t1, "0000000000000002", hour0 + 2_000, 7_000_000, SpanKind.CLIENT, SpanStatusCode.ERROR),
            S(b, t1, "0000000000000003", hour0 + Hour + 10, 3_000_000, SpanKind.CONSUMER),       // next hour: its own index row
            S(a, t2, "0000000000000004", hour0 + 61_000_000_000L, 250_000, name: "other"),       // a different minute and operation
            S(a, t2, "0000000000000005", hour0 + 61_000_000_000L + 5, 900_000_000, status: SpanStatusCode.ERROR, name: "other"),
        };
        await fx.Writer.FlushTracesAsync(spans);

        var index = fx.AllParquetFiles("trace_index");
        Assert.NotEmpty(index);
        // (trace, service, hour) rows: t1/a, t1/b (hour 0), t1/b (hour 1), t2/a
        Assert.Equal(4L, ParquetFixture.Scalar<long>(index, "count(*)"));
        Assert.Equal(3L, ParquetFixture.Scalar<long>(index, "CAST(sum(span_count) AS BIGINT)", "trace_hi = 0 AND trace_lo = 1"));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(index, "count(*)", "trace_hi = 0 AND trace_lo = 1 AND service_name = 'svc-b' AND has_error = 1 AND span_count = 1 AND start_min = " + (hour0 + 2_000)));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(index, "count(*)", "service_name = 'svc-b' AND start_min = " + (hour0 + Hour + 10)));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(index, "count(*)", $"trace_hi = {0xffffffffffffffffUL} AND trace_lo = 2 AND has_error = 1 AND span_count = 2"));

        // Request rollup: inbound spans only (SERVER, CONSUMER), per (service, minute, operation).
        var rollup = fx.AllParquetFiles("request_rollup_minute");
        var minute = 60_000_000_000L;
        var expected = spans.Where(s => s.Kind is SpanKind.SERVER or SpanKind.CONSUMER)
            .GroupBy(s => ((string)s.Resource!.Attributes["service.name"], s.StartTimeUnixNano - s.StartTimeUnixNano % minute, s.Name))
            .ToDictionary(g => g.Key, g => (Requests: (long)g.Count(), Errors: (long)g.Count(x => x.StatusCode == SpanStatusCode.ERROR),
                Sum: g.Sum(x => x.EndTimeUnixNano - x.StartTimeUnixNano), Max: g.Max(x => x.EndTimeUnixNano - x.StartTimeUnixNano)));
        Assert.Equal(expected.Count, ParquetFixture.Scalar<long>(rollup, "count(*)"));
        foreach (var (key, e) in expected)
        {
            var where = $"service_name = '{key.Item1}' AND bucket_start_unix_nano = {key.Item2} AND operation = '{key.Name}'";
            Assert.Equal(e.Requests, ParquetFixture.Scalar<long>(rollup, "request_count", where));
            Assert.Equal(e.Errors, ParquetFixture.Scalar<long>(rollup, "error_count", where));
            Assert.Equal(e.Sum, ParquetFixture.Scalar<long>(rollup, "sum_duration_nanos", where));
            Assert.Equal(e.Max, ParquetFixture.Scalar<long>(rollup, "max_duration_nanos", where));
        }
        // every duration landed in its band
        var bandSums = Enumerable.Range(0, DurationBands.Count).Select(i => $"h{i:00}");
        Assert.Equal(expected.Values.Sum(v => v.Requests), ParquetFixture.Scalar<long>(rollup, "CAST(sum(" + string.Join(" + ", bandSums) + ") AS BIGINT)"));
        var band = DurationBands.IndexOf(900_000_000);
        Assert.Equal(1L, ParquetFixture.Scalar<long>(rollup, $"CAST(sum(h{band:00}) AS BIGINT)", "operation = 'other'"));
    }

    [Fact]
    public async Task TraceIndexFilesAreSortedByTraceIdSoALookupPrunes()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var spans = Enumerable.Range(0, 50).Select(i => S(r, (50 - i).ToString("x32"), (i + 1).ToString("x16"), BaseNano + i, 1000)).ToList();
        await fx.Writer.FlushTracesAsync(spans);
        var ids = ParquetFixture.Scalar<string>(fx.AllParquetFiles("trace_index"), "string_agg(CAST(trace_lo AS VARCHAR), ',')");
        var values = ids.Split(',').Select(long.Parse).ToList();
        Assert.Equal(values.Order(), values);
    }

    [Fact]
    public async Task ARetriedFlushDoesNotDoubleCountTheRollupOrTheIndex()
    {
        using var fx = new ParquetFixture();
        var r = Resource(1, "svc");
        var spans = new List<SpanModel> { S(r, "00000000000000000000000000000009", "0000000000000009", BaseNano, 1_000_000) };
        await fx.Writer.FlushTracesAsync(spans, "same");
        await fx.Writer.FlushTracesAsync(spans, "same");
        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("request_rollup_minute"), "CAST(sum(request_count) AS BIGINT)"));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("trace_index"), "CAST(sum(span_count) AS BIGINT)"));
    }

    /// <summary>Fails every commit into a table's folder, to prove a derived failure keeps the raw rows and is counted.</summary>
    private sealed class FailingStore(LocalParquetFileStore inner, string failTable, int failures) : IParquetFileStore
    {
        public int Failures = failures;
        public int Attempts;
        public string Root => inner.Root;
        public string NewTempPath(ParquetPartition p, string w, string n) => inner.NewTempPath(p, w, n);
        public string PathOf(ParquetPartition p, string n) => inner.PathOf(p, n);
        public Task CommitFileAsync(ParquetPartition p, string t, string f, CancellationToken ct)
        {
            if (p.Table == failTable)
            {
                Interlocked.Increment(ref Attempts);
                if (Interlocked.Decrement(ref Failures) >= 0) throw new IOException("injected");
            }
            return inner.CommitFileAsync(p, t, f, ct);
        }
        public Task<IReadOnlyList<string>> ListManifestNamesAsync(ParquetPartition p, CancellationToken ct) => inner.ListManifestNamesAsync(p, ct);
        public Task<ParquetManifest?> ReadManifestAsync(ParquetPartition p, string n, CancellationToken ct) => inner.ReadManifestAsync(p, n, ct);
        public Task WriteManifestAsync(ParquetPartition p, string n, ParquetManifest m, CancellationToken ct) => inner.WriteManifestAsync(p, n, m, ct);
        public Task<bool> TryPublishManifestAsync(ParquetPartition p, string n, ParquetManifest m, CancellationToken ct) => inner.TryPublishManifestAsync(p, n, m, ct);
        public Task DeleteFileAsync(ParquetPartition p, string n, CancellationToken ct) => inner.DeleteFileAsync(p, n, ct);
        public IEnumerable<ParquetPartition> EnumeratePartitions(string t, long? tenant, DateOnly f, DateOnly to) => inner.EnumeratePartitions(t, tenant, f, to);
        public Task<bool> DeleteDayAsync(string t, long tenant, DateOnly d, CancellationToken ct) => inner.DeleteDayAsync(t, tenant, d, ct);
        public int DeleteTempFiles(string w) => inner.DeleteTempFiles(w);
        public Task<IReadOnlyList<ParquetFileInfo>> ListFilesAsync(ParquetPartition p, CancellationToken ct) => inner.ListFilesAsync(p, ct);
        public Task<IReadOnlyList<ParquetFileInfo>> ListTempFilesAsync(ParquetPartition p, CancellationToken ct) => inner.ListTempFilesAsync(p, ct);
        public Task DeleteTempFileAsync(ParquetPartition p, string n, CancellationToken ct) => inner.DeleteTempFileAsync(p, n, ct);
        public Task<string?> ReadTextAsync(string path, CancellationToken ct) => inner.ReadTextAsync(path, ct);
        public Task WriteTextAsync(string path, string text, CancellationToken ct) => inner.WriteTextAsync(path, text, ct);
    }

    private static ParquetBulkWriter WriterOver(ParquetFixture fx, IParquetFileStore store, IngestionMetrics metrics, int maxRetries)
        => new(store, fx.FileWriter, new ManifestWriter(store, "w9"), NullLogger<ParquetBulkWriter>.Instance, metrics,
            Options.Create(new TelemetryIngestionOptions { MaxFlushRetries = maxRetries, RetryBaseDelayMilliseconds = 1, RetryMaxDelayMilliseconds = 2 }));

    [Fact]
    public async Task ADerivedWriteIsRetriedWithTheSameToken_AndStoredOnce()
    {
        using var fx = new ParquetFixture();
        var store = new FailingStore(fx.Store, ParquetTables.TraceIndex, failures: 2);
        using var metrics = new IngestionMetrics();
        var writer = WriterOver(fx, store, metrics, maxRetries: 5);
        await writer.FlushTracesAsync([S(Resource(1, "svc"), "00000000000000000000000000000001", "0000000000000001", BaseNano, 1000)], "tok");

        Assert.Equal(3, store.Attempts);
        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("trace_index"), "CAST(sum(span_count) AS BIGINT)"));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("spans"), "count(*)"));
    }

    [Fact]
    public async Task ADerivedWriteThatKeepsFailing_KeepsTheSpans_AndIsCountedAsDropped()
    {
        using var fx = new ParquetFixture();
        var store = new FailingStore(fx.Store, ParquetTables.TraceIndex, failures: 1000);
        using var metrics = new IngestionMetrics();
        var dropped = new ConcurrentBag<(string Table, long Count)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name.EndsWith("derived_rows_dropped")) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            foreach (var t in tags) if (t.Key == "table") dropped.Add(((string)t.Value!, v));
        });
        listener.Start();

        var writer = WriterOver(fx, store, metrics, maxRetries: 2);
        await writer.FlushTracesAsync([S(Resource(1, "svc"), "00000000000000000000000000000001", "0000000000000001", BaseNano, 1000)]);

        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("spans"), "count(*)"));
        Assert.Empty(fx.AllParquetFiles("trace_index"));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(fx.AllParquetFiles("request_rollup_minute"), "CAST(sum(request_count) AS BIGINT)"));
        Assert.Contains(("trace_index", 1L), dropped);
    }
}
