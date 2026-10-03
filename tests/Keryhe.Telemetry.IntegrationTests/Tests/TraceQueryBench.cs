using System.Diagnostics;
using System.Text;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Opt-in lab benchmark for the trace list and trace detail reads (trace-list-detail-performance plan, Phase 0): seeds
/// a stress-harness-sized volume (default 60,000 traces of ~10 spans = ~600k spans over six hours) into the provider's
/// container and times the repository's real queries (summary, first page, keyset next page, errors, slow, search,
/// samples, detail), writing one table to the file named by <c>TRACE_BENCH_OUT</c> (a no-op unless it is set), e.g.
/// <c>TRACE_BENCH_OUT=/tmp/bench.txt TRACE_BENCH_LABEL=phase2 dotnet test --filter TraceQueryBench</c>.
/// <c>TRACE_BENCH_TRACES</c> overrides the trace count, <c>TRACE_BENCH_RUNS</c> the timed runs per operation (default 7).
///
/// It is a lab tool, not a stress test: one process, one tenant, no concurrent writes or readers. Use it to attribute a
/// query change (the numbers are repeatable to a few percent), then confirm on the stress harness. The harness remains the
/// measure of record because it carries concurrent ingest and reads.
/// </summary>
public abstract class TraceQueryBenchBase(ProviderFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Services =
    [
        "web-frontend", "api-gateway", "user-service", "catalog-service", "cart-service", "order-service",
        "inventory-service", "payment-service", "email-service", "search-service", "recommendation-service", "auth-service"
    ];

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bench()
    {
        var output = Environment.GetEnvironmentVariable("TRACE_BENCH_OUT");
        if (string.IsNullOrEmpty(output)) return;
        var label = Environment.GetEnvironmentVariable("TRACE_BENCH_LABEL") ?? "run";
        var traceCount = int.TryParse(Environment.GetEnvironmentVariable("TRACE_BENCH_TRACES"), out var tc) ? tc : 60_000;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("TRACE_BENCH_RUNS"), out var r) ? r : 7;

        // Data ends "now" and spans six hours back, so windows are relative to the data like a real list page.
        // TRACE_BENCH_SPREAD_HOURS spreads the same traces over a longer history (default 6 h): the point of a multi-day table
        // is the by-trace-id probe of every Timescale chunk. The list windows stay the last hour and six hours of it.
        var spreadHours = int.TryParse(Environment.GetEnvironmentVariable("TRACE_BENCH_SPREAD_HOURS"), out var sh) && sh >= 1 ? sh : 6;
        var dataEnd = DateTime.UtcNow;
        var dataStart = dataEnd.AddHours(-spreadHours);
        var seedWatch = Stopwatch.StartNew();
        var (traceIds, traceStarts, traceEnds) = await SeedAsync(traceCount, dataStart, dataEnd);
        await AfterSeedAsync();
        var seedMs = seedWatch.ElapsedMilliseconds;

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        var asOf = DateTime.UtcNow.AddMinutes(5);
        var end = dataEnd;
        var rows = new List<(string Op, double P50, double Max, string Note)>();

        async Task Time(string op, Func<Task<string>> call)
        {
            await call(); // warm-up: plan cache, buffer pool
            var times = new List<double>(runs);
            var note = "";
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                note = await call();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            rows.Add((op, times[times.Count / 2], times[^1], note));
        }

        foreach (var window in new[] { TimeSpan.FromHours(1), TimeSpan.FromHours(6) })
        {
            var start = end - window;
            var w = $"{window.TotalHours:0}h";
            await Time($"summary {w}", async () =>
                (await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = start, End = end, AsOf = asOf, BucketCount = 60 })).ListTotal.ToString());
            await Time($"summary {w} service", async () =>
                (await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = start, End = end, AsOf = asOf, BucketCount = 60, Service = "order-service" })).ListTotal.ToString());
            await Time($"page first {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100 })).Items.Count.ToString());
            await Time($"page first {w} service", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Service = "order-service" })).Items.Count.ToString());
            var first = await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100 });
            await Time($"page next {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = first.AsOf, Size = 100, Nav = "next", Cursor = first.NextCursor })).Items.Count.ToString());
            await Time($"page last {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Nav = "last" })).Items.Count.ToString());
            await Time($"errors summary {w}", async () =>
                (await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = start, End = end, AsOf = asOf, BucketCount = 60, Mode = "errors" })).ListTotal.ToString());
            await Time($"errors page {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Mode = "errors" })).Items.Count.ToString());
            await Time($"errors summary {w} service", async () =>
                (await repo.GetTraceSummaryAsync(new TraceSummaryQuery { Start = start, End = end, AsOf = asOf, BucketCount = 60, Mode = "errors", Service = "order-service" })).ListTotal.ToString());
            await Time($"errors page {w} service", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Mode = "errors", Service = "order-service" })).Items.Count.ToString());
            await Time($"slow page {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Mode = "slow", MinDurationMs = 500 })).Items.Count.ToString());
            await Time($"search page {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Search = "inventory" })).Items.Count.ToString());
            await Time($"operation page {w}", async () =>
                (await repo.GetTracePageAsync(new TraceQuery { Start = start, End = end, AsOf = asOf, Size = 100, Service = "order-service", Operation = "POST /orders" })).Items.Count.ToString());
            await Time($"samples errors {w}", async () =>
                (await repo.GetTraceSamplesAsync(new TraceSamplesQuery { Start = start, End = end, Kind = "errors", Limit = 5 })).Items.Count.ToString());
            await Time($"samples slowest {w}", async () =>
                (await repo.GetTraceSamplesAsync(new TraceSamplesQuery { Start = start, End = end, Kind = "slowest", Limit = 5 })).Items.Count.ToString());
        }

        // Detail: 25 traces spread across the whole retention (old ones included), one timing = all 25 lookups.
        var sample = Enumerable.Range(0, 25).Select(i => traceIds[(int)((long)i * traceIds.Count / 25)]).ToList();
        await Time("detail x25", async () =>
        {
            var spans = 0;
            foreach (var id in sample) spans += (await repo.GetTraceByIdAsync(id)).Count;
            return spans.ToString();
        });

        // The same lookups with the start-time hint the trace list passes (the trace's own start).
        var hintedIndexes = Enumerable.Range(0, 25).Select(i => (int)((long)i * traceIds.Count / 25)).ToList();
        await Time("detail x25 hinted", async () =>
        {
            var spans = 0;
            foreach (var i in hintedIndexes) spans += (await repo.GetTraceByIdAsync(traceIds[i], new TraceTimeHint(traceStarts[i], traceEnds[i]))).Count;
            return spans.ToString();
        });

        // Large traces (TRACE_BENCH_LARGE=<spans>, e.g. 20000): one wide trace (a few dozen mid-level spans with the rest under
        // them, twelve services, eight attributes a span, events on every fifth), read through the repository and serialized as the
        // API sends it. The cost of the detail page's server side at the sizes the unit-sized seed above never reaches.
        if (int.TryParse(Environment.GetEnvironmentVariable("TRACE_BENCH_LARGE"), out var largeSpans) && largeSpans > 0)
        {
            var (largeId, largeStart) = await SeedLargeTraceAsync(largeSpans, dataEnd.AddMinutes(-30));
            var bytes = 0;
            await Time($"large detail {largeSpans}", async () =>
            {
                var spans = await repo.GetTraceByIdAsync(largeId, new TraceTimeHint(largeStart, largeStart.AddSeconds(5)));
                return spans.Count.ToString();
            });
            await Time($"large serialize {largeSpans}", async () =>
            {
                var spans = await repo.GetTraceByIdAsync(largeId, new TraceTimeHint(largeStart, largeStart.AddSeconds(5)));
                var sw2 = Stopwatch.StartNew();
                var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Keryhe.Telemetry.Api.Models.TraceDetailResponse.From(spans),
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                bytes = json.Length;
                return $"{json.Length / 1_000_000.0:0.0} MB (serialize {sw2.ElapsedMilliseconds} ms)";
            });
        }

        var sb = new StringBuilder();
        sb.AppendLine($"==== {fixture.ProviderName} [{label}] traces={traceCount:N0} spread={spreadHours}h seed={seedMs / 1000.0:0.0}s runs={runs} (ms: median / max, note = rows or count)");
        foreach (var (op, p50, max, note) in rows)
            sb.AppendLine($"  {op,-26} {p50,9:0.0} / {max,9:0.0}   {note}");
        sb.AppendLine();

        // What one connection open costs by itself (the ClickHouse driver issues a version/timezone query on every open).
        await Time("open connection x20", async () =>
        {
            for (var i = 0; i < 20; i++) { await using var c = await OpenRawAsync(); }
            return "20";
        });
        sb.AppendLine($"  {"open connection x20",-26} {rows[^1].P50,9:0.0} / {rows[^1].Max,9:0.0}   (per open: {rows[^1].P50 / 20:0.0} ms)");
        sb.AppendLine();

        var variants = Environment.GetEnvironmentVariable("TRACE_BENCH_SQL");
        if (!string.IsNullOrEmpty(variants) && File.Exists(variants))
            await RunVariantsAsync(variants, dataEnd, runs, sb, traceIds[traceIds.Count / 2], SeededDataBuilder.ToUnixNano(traceStarts[traceIds.Count / 2]));

        await File.AppendAllTextAsync(output, sb.ToString());
    }

    /// <summary>Opens a raw connection to the provider's test database, for the SQL variant experiments.</summary>
    protected abstract Task<System.Data.Common.DbConnection> OpenRawAsync();

    /// <summary>
    /// Experiment mode (<c>TRACE_BENCH_SQL</c>): runs each SQL variant in the file (variants start at a line <c>-- ### name</c>; a
    /// provider-specific file is chosen by the caller) over the seeded data, timing it and reporting the row count. Parameters
    /// available to the SQL: tenantId, start/end (the last hour, in unix nanos), anchorFrom (start minus five minutes),
    /// sliceStart (end minus 60 s), asOf, service.
    /// </summary>
    private async Task RunVariantsAsync(string file, DateTime dataEnd, int runs, StringBuilder sb, string sampleTraceId, long sampleTraceStart)
    {
        var text = await File.ReadAllTextAsync(file);
        var end = SeededDataBuilder.ToUnixNano(dataEnd);
        var start = end - 3_600_000_000_000L;
        var parameters = new Dictionary<string, object>
        {
            ["tenantId"] = fixture.TenantId, ["start"] = start, ["end"] = end, ["anchorFrom"] = start - 300_000_000_000L,
            ["sliceStart"] = end - 60_000_000_000L, ["asOf"] = DateTime.UtcNow.AddMinutes(5), ["service"] = "order-service",
            ["operation"] = "POST /orders", ["minDurationNano"] = 500_000_000L, ["traceId"] = sampleTraceId, ["traceStart"] = sampleTraceStart, ["traceLo"] = sampleTraceStart - 300_000_000_000L, ["traceHi"] = sampleTraceStart + 86_400_000_000_000L, ["traceEnd"] = sampleTraceStart + 5_000_000_000L, ["rangeFrom"] = start, ["rangeTo"] = end + 1
        };
        sb.AppendLine($"---- SQL variants ({fixture.ProviderName})");
        await using var conn = await OpenRawAsync();
        foreach (var chunk in text.Split("-- ### ", StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = chunk.IndexOf('\n');
            var name = chunk[..newline].Trim();
            var sql = chunk[(newline + 1)..];
            var dp = new Dapper.DynamicParameters();
            foreach (var (k, v) in parameters) if (sql.Contains("@" + k)) dp.Add(k, v);
            try
            {
                var times = new List<double>();
                var rowsReturned = 0;
                for (var i = 0; i < runs + 1; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var result = (await Dapper.SqlMapper.QueryAsync(conn, new Dapper.CommandDefinition(sql, dp, commandTimeout: 300))).ToList();
                    if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);
                    rowsReturned = result.Count;
                }
                times.Sort();
                sb.AppendLine($"  {name,-34} {times[times.Count / 2],9:0.0} / {times[^1],9:0.0}   rows={rowsReturned}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {name,-34} ERROR {ex.Message.Split('\n')[0]}");
            }
        }
        sb.AppendLine();
    }

    /// <summary>Provider hook after the seed (statistics, merges) so the plans match a settled database.</summary>
    protected virtual Task AfterSeedAsync() => Task.CompletedTask;

    private async Task<(string TraceId, DateTime Start)> SeedLargeTraceAsync(int spanCount, DateTime start)
    {
        var tenantId = fixture.TenantId;
        var scope = SeededDataBuilder.Scope();
        var resources = Services.Select((svc, i) => SeededDataBuilder.Resource(tenantId, svc, extra: new Dictionary<string, object>
            { ["k8s.pod.name"] = $"{svc}-pod-{i}", ["k8s.namespace.name"] = "prod", ["service.version"] = "1.2.3", ["host.name"] = $"node-{i}" })).ToArray();
        var rng = new Random(99);
        string Id(int length) { var b = new byte[length / 2]; rng.NextBytes(b); return Convert.ToHexString(b).ToLowerInvariant(); }
        var traceId = Id(32);
        var startNs = SeededDataBuilder.ToUnixNano(start);
        var spans = new List<SpanModel>(spanCount);
        for (var i = 0; i < spanCount; i++)
        {
            var parentIndex = i == 0 ? -1 : (i < 60 ? 0 : 1 + rng.Next(59));
            long offset = 0, duration = 4_000_000_000L;
            if (parentIndex >= 0)
            {
                var parent = spans[parentIndex];
                var parentDuration = parent.EndTimeUnixNano - parent.StartTimeUnixNano;
                offset = parent.StartTimeUnixNano - startNs + (long)(rng.NextDouble() * parentDuration * 0.5);
                duration = Math.Max(1000, (long)(rng.NextDouble() * parentDuration * 0.4));
            }
            var attributes = new Dictionary<string, object>();
            for (var a = 0; a < 8; a++) attributes[$"attr.key{a}"] = $"value-{(i * 7 + a) % 97}";
            spans.Add(new SpanModel
            {
                TraceIdHex = traceId, SpanIdHex = Id(16), ParentSpanIdHex = parentIndex < 0 ? null : spans[parentIndex].SpanIdHex,
                Name = i == 0 ? "GET /checkout" : $"op-{i % 40}", Kind = i == 0 ? SpanKind.SERVER : SpanKind.CLIENT,
                StartTimeUnixNano = startNs + offset, EndTimeUnixNano = startNs + offset + duration,
                StatusCode = i % 997 == 0 ? SpanStatusCode.ERROR : SpanStatusCode.OK, Attributes = attributes,
                Events = i % 5 == 0 ? [new SpanEventModel { Name = "cache.miss", TimeUnixNano = startNs + offset + duration / 2, Attributes = new Dictionary<string, object> { ["cache.key"] = $"k{i}" } }] : [],
                Resource = resources[i % resources.Length], InstrumentationScope = scope
            });
        }
        using var writeScope = fixture.Services.CreateScope();
        var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
        for (var from = 0; from < spans.Count; from += 5000)
            await writer.FlushTracesAsync(spans.Skip(from).Take(5000).ToList());
        return (traceId, start);
    }

    private async Task<(List<string> Ids, List<DateTime> Starts, List<DateTime> Ends)> SeedAsync(int traceCount, DateTime dataStart, DateTime dataEnd)
    {
        var tenantId = fixture.TenantId;
        var scope = SeededDataBuilder.Scope();
        var resources = Services.ToDictionary(s => s, s => SeededDataBuilder.Resource(tenantId, s));
        var rng = new Random(20261002);
        var spanSeconds = (dataEnd - dataStart).TotalSeconds - 10;
        var traceIds = new List<string>(traceCount);
        var traceStarts = new List<DateTime>(traceCount);
        var traceEnds = new List<DateTime>(traceCount);
        var batch = new List<SpanModel>(12_000);
        long counter = 0;

        string Id(int length)
        {
            var bytes = new byte[length / 2];
            rng.NextBytes(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        using var writeScope = fixture.Services.CreateScope();
        var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();

        for (var t = 0; t < traceCount; t++)
        {
            var traceId = Id(32);
            traceIds.Add(traceId);
            var traceStart = dataStart.AddSeconds(spanSeconds * t / traceCount);
            traceStarts.Add(traceStart);
            var startNano = SeededDataBuilder.ToUnixNano(traceStart);
            // Lognormal-ish root duration: median ~60 ms, ~3% over 500 ms.
            var rootMs = Math.Min(4000, Math.Exp(4.1 + rng.NextDouble() * 2.6 - 0.2 + (rng.NextDouble() < 0.03 ? 2 : 0)));
            var hasError = rng.NextDouble() < 0.03;
            traceEnds.Add(traceStart.AddMilliseconds(rootMs * 1.1 + 1));
            var spanTotal = 6 + rng.Next(0, 9);
            var errorSpan = hasError ? rng.Next(0, spanTotal) : -1;

            // A chain with some fan-out: span k's parent is a random earlier span; service rotates through the topology.
            var spanIds = new string[spanTotal];
            var kinds = new SpanKind[spanTotal];
            for (var k = 0; k < spanTotal; k++)
            {
                spanIds[k] = Id(16);
                var parent = k == 0 ? -1 : rng.Next(0, k);
                kinds[k] = k == 0 ? SpanKind.SERVER : (k % 2 == 1 ? SpanKind.CLIENT : SpanKind.SERVER);
                var service = k == 0 ? "web-frontend" : Services[1 + (k + t) % (Services.Length - 1)];
                var offsetMs = k == 0 ? 0 : rootMs * 0.05 * k / spanTotal;
                var durMs = k == 0 ? rootMs : Math.Max(0.5, rootMs * (0.8 - 0.5 * k / spanTotal) * rng.NextDouble());
                var name = k == 0 ? "GET /" : kinds[k] == SpanKind.CLIENT ? $"call {service}" : service switch
                {
                    "order-service" => "POST /orders",
                    "inventory-service" => "inventory.reserve",
                    _ => $"{service} handler"
                };
                batch.Add(new SpanModel
                {
                    TraceIdHex = traceId,
                    SpanIdHex = spanIds[k],
                    ParentSpanIdHex = parent < 0 ? null : spanIds[parent],
                    Name = name,
                    Kind = kinds[k],
                    StartTimeUnixNano = startNano + (long)(offsetMs * 1_000_000),
                    EndTimeUnixNano = startNano + (long)((offsetMs + durMs) * 1_000_000),
                    StatusCode = k == errorSpan ? SpanStatusCode.ERROR : SpanStatusCode.OK,
                    StatusMessage = k == errorSpan ? "upstream failure" : null,
                    Attributes = new Dictionary<string, object> { ["http.route"] = name, ["span.index"] = k },
                    Resource = resources[service],
                    InstrumentationScope = scope
                });
                counter++;
            }

            if (batch.Count >= 10_000)
            {
                await writer.FlushTracesAsync(batch);
                batch = new List<SpanModel>(12_000);
            }
        }
        if (batch.Count > 0) await writer.FlushTracesAsync(batch);
        return (traceIds, traceStarts, traceEnds);
    }
}

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlTraceQueryBench(PostgreSqlFixture fixture) : TraceQueryBenchBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenRawAsync()
    {
        var c = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await c.OpenAsync();
        return c;
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        // VACUUM too, not just ANALYZE: autovacuum would have set the visibility map in a running system, and
        // index-only scans depend on it.
        await using var cmd = new Npgsql.NpgsqlCommand("VACUUM (ANALYZE) spans", conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();
    }
}

[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleTraceQueryBench(TimescaleFixture fixture) : TraceQueryBenchBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenRawAsync()
    {
        var c = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await c.OpenAsync();
        return c;
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("VACUUM (ANALYZE) spans", conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();

        // TRACE_BENCH_COMPRESS=1: compress the chunks older than seven days, as the compression policy would, so a by-trace
        // probe of an old chunk pays for decompression.
        if (Environment.GetEnvironmentVariable("TRACE_BENCH_COMPRESS") == "1")
        {
            await using var compress = new Npgsql.NpgsqlCommand(
                "SELECT compress_chunk(c, if_not_compressed => true) FROM show_chunks('spans', older_than => (EXTRACT(EPOCH FROM NOW() - INTERVAL '7 days') * 1000000000)::bigint) c", conn) { CommandTimeout = 1200 };
            await compress.ExecuteNonQueryAsync();
        }
    }
}

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerTraceQueryBench(SqlServerFixture fixture) : TraceQueryBenchBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenRawAsync()
    {
        var c = new Microsoft.Data.SqlClient.SqlConnection(fixture.DatabaseConnectionString);
        await c.OpenAsync();
        return c;
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("UPDATE STATISTICS spans WITH FULLSCAN", conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();
    }
}

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlTraceQueryBench(MySqlFixture fixture) : TraceQueryBenchBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenRawAsync()
    {
        var c = new MySqlConnector.MySqlConnection(fixture.DatabaseConnectionString);
        await c.OpenAsync();
        return c;
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new MySqlConnector.MySqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlConnector.MySqlCommand("ANALYZE TABLE spans", conn) { CommandTimeout = 600 };
        await using var reader = await cmd.ExecuteReaderAsync();
    }
}

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseTraceQueryBench(ClickHouseFixture fixture) : TraceQueryBenchBase(fixture)
{
    protected override async Task<System.Data.Common.DbConnection> OpenRawAsync()
    {
        var c = new global::ClickHouse.Client.ADO.ClickHouseConnection(fixture.DatabaseConnectionString);
        await c.OpenAsync();
        return c;
    }

    protected override async Task AfterSeedAsync()
    {
        // Let the background merges the seed's many small parts would otherwise be waiting for finish, so the timings
        // describe a settled table rather than a part-count artefact.
        await using var conn = new global::ClickHouse.Client.ADO.ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "OPTIMIZE TABLE spans FINAL";
        await cmd.ExecuteNonQueryAsync();
    }
}
