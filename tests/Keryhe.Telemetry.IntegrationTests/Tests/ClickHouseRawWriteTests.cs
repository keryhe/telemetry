using ClickHouse.Client.ADO;
using Dapper;
using Keryhe.Telemetry.ClickHouse.Services;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The raw tables of the ClickHouse row model (plans/clickhouse-redesign phase 1): a flush through the real writer
/// is read back column by column with raw SQL. The shared read-path suites still fail on ClickHouse until phases 4
/// and 5, so this is the only coverage of the new layout until then.
/// </summary>
[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseRawWriteTests(ClickHouseFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private ClickHouseBulkWriter Writer => (ClickHouseBulkWriter)fixture.Services.GetRequiredService<ITelemetryBulkWriter>();

    private async Task<List<dynamic>> QueryAsync(string sql)
    {
        await using var conn = new ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        return (await conn.QueryAsync<dynamic>(sql)).ToList();
    }

    private async Task<long> CountAsync(string table) =>
        Convert.ToInt64((await QueryAsync($"SELECT count() AS c FROM {table}"))[0].c);

    // Nanoseconds whose last two digits the driver cannot carry (README R9).
    private static long Nanos(DateTime t, int extraNanos) => SeededDataBuilder.ToUnixNano(t) + extraNanos;

    private ResourceModel Resource(string service = "raw-svc") => new()
    {
        TenantId = fixture.TenantId,
        SchemaUrl = "https://schemas/resource",
        Attributes = new Dictionary<string, object>
        {
            ["service.name"] = service, ["service.version"] = "1.2.3", ["deployment.environment"] = "prod", ["host.name"] = "h1"
        }
    };

    private SpanModel Span(string traceHex, string spanHex, string? parentHex, int extraNanos = 0) => new()
    {
        TraceIdHex = traceHex, SpanIdHex = spanHex, ParentSpanIdHex = parentHex,
        Name = "GET /x", Kind = SpanKind.SERVER,
        StartTimeUnixNano = Nanos(T0, 456_789 + extraNanos),
        EndTimeUnixNano = Nanos(T0, 456_789 + extraNanos) + 1_234_567_891L,
        StatusCode = SpanStatusCode.ERROR, StatusMessage = "boom", TraceState = "a=b", Flags = 1,
        DroppedAttributesCount = 2, DroppedEventsCount = 3, DroppedLinksCount = 4,
        Attributes = new Dictionary<string, object>
        {
            ["http.status_code"] = 500, ["http.method"] = "GET", ["db.system"] = "postgresql",
            ["s"] = "text", ["i"] = 42L, ["d"] = 0.5, ["b"] = true, ["bytes"] = new byte[] { 1, 2, 3 },
            ["arr"] = new object[] { "a", 1L }, ["kv"] = new Dictionary<string, object> { ["k"] = "v" }
        },
        Events = [new SpanEventModel { Name = "e1", TimeUnixNano = Nanos(T0, 700_000), DroppedAttributesCount = 1, Attributes = new() { ["ea"] = "eb" } }, new SpanEventModel { Name = "e2", TimeUnixNano = Nanos(T0, 800_000) }],
        Links = [new SpanLinkModel { LinkedTraceIdHex = traceHex, LinkedSpanIdHex = "ffeeddccbbaa9988", TraceState = "ts", Flags = 1, Attributes = new() { ["la"] = "lb" } }],
        Resource = Resource(), InstrumentationScope = new InstrumentationScopeModel { Name = "scope", Version = "9.9", SchemaUrl = "https://schemas/scope", Attributes = new() { ["sa"] = "sb" } }
    };

    [Fact]
    public async Task Spans_RoundTrip_EveryColumn()
    {
        // high bit set in every id
        const string trace = "c6d086824097e4a395cfff46699c73c4";
        const string span = "a1a2a3a4a5a6a7a8";
        const string parent = "f0e1d2c3b4a59687";
        var spans = new List<SpanModel> { Span(trace, span, parent), Span(trace, "0102030405060708", null, 0) };
        await Writer.FlushTracesAsync(spans);

        var rows = await QueryAsync(
            "SELECT trace_id, span_id, parent_span_id, toUnixTimestamp64Nano(start_time) AS start_ns, duration_ns, toString(kind) AS kind, " +
            "toString(status_code) AS status, status_message, trace_state, flags, tenant_id, service_name, span_name, " +
            "attributes, dropped_attributes_count, dropped_events_count, dropped_links_count, " +
            "events.name AS ev_names, events.attributes AS ev_attrs, events.dropped_attributes_count AS ev_dropped, " +
            "arrayMap(t -> toUnixTimestamp64Nano(t), events.time) AS ev_times, " +
            "links.trace_id AS ln_trace, links.span_id AS ln_span, links.trace_state AS ln_state, links.attributes AS ln_attrs, " +
            "resource_attributes, resource_schema_url, scope_name, scope_version, scope_attributes, scope_schema_url, " +
            "service_version, deployment_environment, host_name, http_method, http_status_code, db_system " +
            "FROM spans ORDER BY parent_span_id DESC");
        Assert.Equal(2, rows.Count);
        var a = rows[0];

        Assert.Equal(trace, ClickHouseIds.GuidToTraceIdHex((Guid)a.trace_id));
        Assert.Equal(span, ClickHouseIds.UInt64ToSpanIdHex((ulong)a.span_id));
        Assert.Equal(parent, ClickHouseIds.UInt64ToSpanIdHex((ulong)a.parent_span_id));
        Assert.Equal(0UL, (ulong)rows[1].parent_span_id); // a root

        // start_time keeps 100 ns resolution; duration is computed from the original nanoseconds
        Assert.Equal(Nanos(T0, 456_700), (long)a.start_ns);
        Assert.Equal(1_234_567_891UL, (ulong)a.duration_ns);

        Assert.Equal("SERVER", (string)a.kind);
        Assert.Equal("ERROR", (string)a.status);
        Assert.Equal("boom", (string)a.status_message);
        Assert.Equal("a=b", (string)a.trace_state);
        Assert.Equal(1u, (uint)a.flags);
        Assert.Equal((ulong)fixture.TenantId, (ulong)a.tenant_id);
        Assert.Equal("raw-svc", (string)a.service_name);
        Assert.Equal("GET /x", (string)a.span_name);
        Assert.Equal((2u, 3u, 4u), ((uint)a.dropped_attributes_count, (uint)a.dropped_events_count, (uint)a.dropped_links_count));

        var attrs = (IDictionary<string, string>)a.attributes;
        Assert.Equal("text", attrs["s"]);
        Assert.Equal("42", attrs["i"]);
        Assert.Equal("0.5", attrs["d"]);
        Assert.Equal("true", attrs["b"]);
        Assert.Equal("500", attrs["http.status_code"]);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), attrs["bytes"]);
        Assert.Equal("[\"a\",1]", attrs["arr"]);
        Assert.Equal("{\"k\":\"v\"}", attrs["kv"]);

        Assert.Equal(["e1", "e2"], ((IEnumerable<string>)a.ev_names).ToArray());
        Assert.Equal("eb", ((IEnumerable<IDictionary<string, string>>)a.ev_attrs).First()["ea"]);
        Assert.Equal([1u, 0u], ((IEnumerable<uint>)a.ev_dropped).ToArray());
        Assert.Equal([Nanos(T0, 700_000), Nanos(T0, 800_000)], ((IEnumerable<long>)a.ev_times).ToArray());
        Assert.Equal(trace, ClickHouseIds.GuidToTraceIdHex(((IEnumerable<Guid>)a.ln_trace).Single()));
        Assert.Equal("ffeeddccbbaa9988", ClickHouseIds.UInt64ToSpanIdHex(((IEnumerable<ulong>)a.ln_span).Single()));
        Assert.Equal("ts", ((IEnumerable<string>)a.ln_state).Single());
        Assert.Equal("lb", ((IEnumerable<IDictionary<string, string>>)a.ln_attrs).Single()["la"]);

        Assert.Equal("1.2.3", ((IDictionary<string, string>)a.resource_attributes)["service.version"]);
        Assert.Equal("https://schemas/resource", (string)a.resource_schema_url);
        Assert.Equal(("scope", "9.9", "https://schemas/scope"), ((string)a.scope_name, (string)a.scope_version, (string)a.scope_schema_url));
        Assert.Equal("sb", ((IDictionary<string, string>)a.scope_attributes)["sa"]);

        // promoted columns fill from the maps, including the fallback keys, and are never sent by the writer
        Assert.Equal(("1.2.3", "prod", "h1"), ((string)a.service_version, (string)a.deployment_environment, (string)a.host_name));
        Assert.Equal(("GET", (ushort)500, "postgresql"), ((string)a.http_method, (ushort)a.http_status_code, (string)a.db_system));
    }

    [Fact]
    public async Task Logs_RoundTrip_EveryColumn()
    {
        const string trace = "8f000000000000000000000000000001";
        var logs = new List<LogRecordModel>
        {
            new()
            {
                TimeUnixNano = Nanos(T0, 123_456), ObservedTimeUnixNano = Nanos(T0, 999_900), SeverityNumber = 17, SeverityText = "ERROR",
                EventName = "ev", BodyType = AttributeType.STRING, BodyValue = "hello world", Flags = 1, DroppedAttributesCount = 5,
                TraceIdHex = trace, SpanIdHex = "9000000000000001",
                Attributes = new() { ["exception.type"] = "System.Boom", ["n"] = 7L },
                Resource = Resource(), InstrumentationScope = new InstrumentationScopeModel { Name = "scope" }
            },
            // no timestamp: falls back to observed; a non-string body is its JSON text; ids absent
            new()
            {
                ObservedTimeUnixNano = Nanos(T0, 5_000_000), BodyType = AttributeType.KVLIST, BodyValue = "{\"a\":1}",
                Resource = Resource(), InstrumentationScope = new InstrumentationScopeModel { Name = "scope" }
            }
        };
        await Writer.FlushLogsAsync(logs);

        var rows = await QueryAsync(
            "SELECT toUnixTimestamp64Nano(timestamp) AS ts, toUnixTimestamp64Nano(observed_timestamp) AS obs, trace_id, span_id, flags, " +
            "severity_number, severity_text, event_name, body, attributes, dropped_attributes_count, service_name, tenant_id, " +
            "resource_attributes, scope_name, exception_type, service_version FROM log_records ORDER BY timestamp");
        Assert.Equal(2, rows.Count);

        var first = rows[0];
        Assert.Equal(Nanos(T0, 123_400), (long)first.ts);
        Assert.Equal(Nanos(T0, 999_900), (long)first.obs);
        Assert.Equal(trace, ClickHouseIds.GuidToTraceIdHex((Guid)first.trace_id));
        Assert.Equal("9000000000000001", ClickHouseIds.UInt64ToSpanIdHex((ulong)first.span_id));
        Assert.Equal((17, "ERROR", "ev", "hello world"), ((int)(byte)first.severity_number, (string)first.severity_text, (string)first.event_name, (string)first.body));
        Assert.Equal("7", ((IDictionary<string, string>)first.attributes)["n"]);
        Assert.Equal(("System.Boom", "1.2.3", "raw-svc"), ((string)first.exception_type, (string)first.service_version, (string)first.service_name));

        var second = rows[1];
        Assert.Equal(Nanos(T0, 5_000_000), (long)second.ts);
        Assert.Equal(Guid.Empty, (Guid)second.trace_id);
        Assert.Equal(0UL, (ulong)second.span_id);
        Assert.Equal("{\"a\":1}", (string)second.body);
    }

    [Fact]
    public async Task Points_RoundTrip_AllFiveTables_AndSeriesIdIsStableUnderKeyOrder()
    {
        var scope = new InstrumentationScopeModel { Name = "m", Version = "1" };
        var t = SeededDataBuilder.ToUnixNano(T0);
        const string exTrace = "8f000000000000000000000000000002";

        MetricModel Gauge(Dictionary<string, object> attrs) => new()
        {
            Name = "g", Type = MetricType.GAUGE, Unit = "1", Description = "gd", Resource = Resource(), InstrumentationScope = scope,
            GaugeDataPoints = [new GaugeDataPointModel
            {
                TimeUnixNano = t, StartTimeUnixNano = t - 1_000_000_000, ValueInt = 7, Attributes = attrs,
                Exemplars = [new ExemplarModel { TimeUnixNano = t, ValueDouble = 3.5, TraceIdHex = exTrace, SpanIdHex = "9000000000000002", FilteredAttributes = new() { ["x"] = "y" } }]
            }]
        };

        // the same attribute set inserted in two key orders, and a different one
        var ordered = Gauge(new() { ["a"] = "1", ["b"] = "2" });
        var reversed = Gauge(new() { ["b"] = "2", ["a"] = "1" });
        var other = Gauge(new() { ["a"] = "1", ["b"] = "3" });
        var metrics = new List<MetricModel>
        {
            ordered, reversed, other,
            new() { Name = "s", Type = MetricType.SUM, Resource = Resource(), InstrumentationScope = scope,
                SumDataPoints = [new SumDataPointModel { TimeUnixNano = t, ValueDouble = 2.5, AggregationTemporality = AggregationTemporality.CUMULATIVE, IsMonotonic = true }] },
            new() { Name = "h", Type = MetricType.HISTOGRAM, Resource = Resource(), InstrumentationScope = scope,
                HistogramDataPoints = [new HistogramDataPointModel { TimeUnixNano = t, Count = 10, Sum = 5.5, Min = null, Max = 9.0, BucketCounts = [1, 2, 7], ExplicitBounds = [1, 5], AggregationTemporality = AggregationTemporality.DELTA }] },
            new() { Name = "e", Type = MetricType.EXPONENTIAL_HISTOGRAM, Resource = Resource(), InstrumentationScope = scope,
                ExponentialHistogramDataPoints = [new ExponentialHistogramDataPointModel { TimeUnixNano = t, Count = 6, Sum = 1.5, Scale = 2, ZeroCount = 1, PositiveOffset = 3, PositiveBucketCounts = [1, 2], NegativeOffset = -1, NegativeBucketCounts = [2], AggregationTemporality = AggregationTemporality.CUMULATIVE }] },
            new() { Name = "q", Type = MetricType.SUMMARY, Resource = Resource(), InstrumentationScope = scope,
                SummaryDataPoints = [new SummaryDataPointModel { TimeUnixNano = t, Count = 3, Sum = 9.0, QuantileValues = [new() { Quantile = 0.5, Value = 1.0 }, new() { Quantile = 0.99, Value = 2.0 }] }] }
        };
        await Writer.FlushMetricsAsync(metrics);

        var gauges = await QueryAsync(
            "SELECT series_id, value, metric_unit, metric_description, toUnixTimestamp64Nano(time) AS tn, toUnixTimestamp64Nano(start_time) AS sn, " +
            "attributes, exemplars.value AS ev, exemplars.trace_id AS et, exemplars.span_id AS es, exemplars.filtered_attributes AS ef, " +
            "scope_name, resource_attributes FROM gauge_points ORDER BY attributes['b'], series_id");
        Assert.Equal(3, gauges.Count);
        var sameSet = gauges.Where(g => ((IDictionary<string, string>)g.attributes)["b"] == "2").ToList();
        Assert.Equal(2, sameSet.Count);
        Assert.Equal((ulong)sameSet[0].series_id, (ulong)sameSet[1].series_id);
        Assert.NotEqual((ulong)sameSet[0].series_id, (ulong)gauges.Single(g => ((IDictionary<string, string>)g.attributes)["b"] == "3").series_id);

        var g0 = sameSet[0];
        Assert.Equal(7d, (double)g0.value); // as_int stored as Float64
        Assert.Equal(("1", "gd", "m"), ((string)g0.metric_unit, (string)g0.metric_description, (string)g0.scope_name));
        Assert.Equal((t, t - 1_000_000_000), ((long)g0.tn, (long)g0.sn));
        Assert.Equal(3.5, ((IEnumerable<double>)g0.ev).Single());
        Assert.Equal(exTrace, ClickHouseIds.GuidToTraceIdHex(((IEnumerable<Guid>)g0.et).Single()));
        Assert.Equal("9000000000000002", ClickHouseIds.UInt64ToSpanIdHex(((IEnumerable<ulong>)g0.es).Single()));
        Assert.Equal("y", ((IEnumerable<IDictionary<string, string>>)g0.ef).Single()["x"]);

        var sum = (await QueryAsync("SELECT value, toString(temporality) AS tmp, is_monotonic, start_time FROM sum_points")).Single();
        Assert.Equal((2.5, "CUMULATIVE"), ((double)sum.value, (string)sum.tmp));
        Assert.True(Convert.ToBoolean(sum.is_monotonic));

        var h = (await QueryAsync("SELECT count, sum, isNull(min) AS min_null, max, bucket_counts, explicit_bounds, toString(temporality) AS tmp FROM histogram_points")).Single();
        Assert.Equal((10UL, 5.5, 1, 9.0, "DELTA"), ((ulong)h.count, (double)h.sum, Convert.ToInt32(h.min_null), (double)h.max, (string)h.tmp));
        Assert.Equal([1UL, 2UL, 7UL], ((IEnumerable<ulong>)h.bucket_counts).ToArray());
        Assert.Equal([1d, 5d], ((IEnumerable<double>)h.explicit_bounds).ToArray());

        var e = (await QueryAsync("SELECT count, scale, zero_count, positive_offset, positive_bucket_counts, negative_offset, negative_bucket_counts FROM exp_histogram_points")).Single();
        Assert.Equal((6UL, 2, 1UL, 3, -1), ((ulong)e.count, (int)e.scale, (ulong)e.zero_count, (int)e.positive_offset, (int)e.negative_offset));
        Assert.Equal([1UL, 2UL], ((IEnumerable<ulong>)e.positive_bucket_counts).ToArray());
        Assert.Equal([2UL], ((IEnumerable<ulong>)e.negative_bucket_counts).ToArray());

        var q = (await QueryAsync("SELECT count, sum, quantiles.quantile AS qq, quantiles.value AS qv FROM summary_points")).Single();
        Assert.Equal((3UL, 9.0), ((ulong)q.count, (double)q.sum));
        Assert.Equal([0.5, 0.99], ((IEnumerable<double>)q.qq).ToArray());
        Assert.Equal([1.0, 2.0], ((IEnumerable<double>)q.qv).ToArray());
    }

    [Fact]
    public async Task SameTokenIsStoredOnce_DifferentTokensTwice()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(fixture.TenantId, T0, traceCount: 5);
        var logs = SeededDataBuilder.BasicLogWindow(fixture.TenantId, T0, count: 20);

        await Writer.FlushTracesAsync(spans, "token-1");
        await Writer.FlushTracesAsync(spans, "token-1");
        await Writer.FlushLogsAsync(logs, "token-1"); // same token, different table: stored
        await Writer.FlushLogsAsync(logs, "token-1");
        Assert.Equal(spans.Count, await CountAsync("spans"));
        Assert.Equal(logs.Count, await CountAsync("log_records"));

        await Writer.FlushTracesAsync(spans, "token-2");
        Assert.Equal(spans.Count * 2, await CountAsync("spans"));

        // the interface methods mint a fresh token each call
        await Writer.FlushTracesAsync(spans);
        Assert.Equal(spans.Count * 3, await CountAsync("spans"));
    }

    [Fact]
    public async Task BatchLargerThanOldBatchSize_IsStoredWhole_UnderOneToken()
    {
        // Phase 0 spike 3: with BatchSize 100,000 the 2nd and 3rd INSERT of a 250,000-row write shared the token and were dropped.
        const int count = 250_000;
        var resource = Resource();
        var scope = new InstrumentationScopeModel { Name = "bulk" };
        var records = new List<LogRecordModel>(count);
        for (var i = 0; i < count; i++)
            records.Add(new LogRecordModel { TimeUnixNano = SeededDataBuilder.ToUnixNano(T0) + i * 1000L, SeverityNumber = 9, BodyValue = "x", Resource = resource, InstrumentationScope = scope });

        await Writer.FlushLogsAsync(records, "big");
        Assert.Equal(count, await CountAsync("log_records"));
        await Writer.FlushLogsAsync(records, "big");
        Assert.Equal(count, await CountAsync("log_records"));
    }
}
