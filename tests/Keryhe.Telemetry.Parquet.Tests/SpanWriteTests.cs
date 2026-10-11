using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Parquet.Tests.Spans;

namespace Keryhe.Telemetry.Parquet.Tests;

public class SpanWriteTests
{
    [Fact]
    public async Task EveryColumnRoundTripsThroughAFlushAndADuckDbRead()
    {
        using var fx = new ParquetFixture();
        var resource = Resource(7, "checkout");
        var span = Span(resource, BaseNano, "GET /cart", traceId: "ffeeddccbbaa99887766554433221100", spanId: "fedcba9876543210");
        span.ParentSpanIdHex = "8000000000000001";
        span.EndTimeUnixNano = BaseNano + 1_234_567;
        span.TraceState = "k=v";
        span.Flags = 1;
        span.StatusCode = SpanStatusCode.ERROR;
        span.StatusMessage = "boom ünïcode";
        span.DroppedAttributesCount = 2;
        span.DroppedEventsCount = 3;
        span.DroppedLinksCount = 4;
        span.Attributes = new Dictionary<string, object>
        {
            ["http.status_code"] = 500, ["ratio"] = 0.5, ["ok"] = false, ["name"] = "x\"y", ["tags"] = new[] { "a", "b" }
        };
        span.Events.Add(new SpanEventModel
        {
            Name = "exception", TimeUnixNano = BaseNano + 10, DroppedAttributesCount = 1,
            Attributes = new Dictionary<string, object> { ["exception.type"] = "IOException" }
        });
        span.Events.Add(new SpanEventModel { Name = "retry", TimeUnixNano = BaseNano + 20 });
        span.Links.Add(new SpanLinkModel
        {
            LinkedTraceIdHex = "80000000000000010000000000000002", LinkedSpanIdHex = "ffffffffffffffff", TraceState = "ls",
            Flags = 5, DroppedAttributesCount = 6, Attributes = new Dictionary<string, object> { ["lk"] = 1 }
        });
        span.InstrumentationScope = new InstrumentationScopeModel
        {
            Name = "scope", Version = "2.1", SchemaUrl = "https://example/scope",
            Attributes = new Dictionary<string, object> { ["sk"] = "sv" }
        };

        await fx.Writer.FlushTracesAsync([span]);

        var files = fx.AllParquetFiles("spans");
        Assert.Single(files);
        string S(string select) => ParquetFixture.Scalar<string>(files, $"CAST({select} AS VARCHAR)");

        Assert.Equal("7", S("tenant_id"));
        Assert.Equal("checkout", S("service_name"));
        Assert.Equal("GET /cart", S("span_name"));
        Assert.Equal(BaseNano.ToString(), S("start_time_unix_nano"));
        Assert.Equal((BaseNano + 1_234_567).ToString(), S("end_time_unix_nano"));
        Assert.Equal("1234567", S("duration_ns"));
        Assert.Equal(0xffeeddccbbaa9988UL.ToString(), S("trace_hi"));
        Assert.Equal(0x7766554433221100UL.ToString(), S("trace_lo"));
        Assert.Equal(0xfedcba9876543210UL.ToString(), S("span_id"));
        Assert.Equal(0x8000000000000001UL.ToString(), S("parent_span_id"));
        Assert.Equal("k=v", S("trace_state"));
        Assert.Equal("1", S("flags"));
        Assert.Equal("SERVER", S("kind"));
        Assert.Equal("ERROR", S("status_code"));
        Assert.Equal("boom ünïcode", S("status_message"));
        Assert.Equal("500", S("attributes['http.status_code']"));
        Assert.Equal("0.5", S("attributes['ratio']"));
        Assert.Equal("false", S("attributes['ok']"));
        Assert.Equal("x\"y", S("attributes['name']"));
        Assert.Equal("[\"a\",\"b\"]", S("attributes['tags']"));
        Assert.Equal("2", S("dropped_attributes_count"));
        Assert.Equal("3", S("dropped_events_count"));
        Assert.Equal("4", S("dropped_links_count"));

        Assert.Equal("2", S("len(events)"));
        Assert.Equal("exception", S("events[1].name"));
        Assert.Equal((BaseNano + 10).ToString(), S("events[1].time_unix_nano"));
        Assert.Equal("IOException", S("events[1].attributes['exception.type']"));
        Assert.Equal("1", S("events[1].dropped_attributes_count"));
        Assert.Equal("retry", S("events[2].name"));
        Assert.Equal("0", S("len(map_keys(events[2].attributes))"));

        Assert.Equal("1", S("len(links)"));
        Assert.Equal(0x8000000000000001UL.ToString(), S("links[1].trace_hi"));
        Assert.Equal("2", S("links[1].trace_lo"));
        Assert.Equal(ulong.MaxValue.ToString(), S("links[1].span_id"));
        Assert.Equal("ls", S("links[1].trace_state"));
        Assert.Equal("5", S("links[1].flags"));
        Assert.Equal("1", S("links[1].attributes['lk']"));
        Assert.Equal("6", S("links[1].dropped_attributes_count"));

        Assert.Equal("checkout", S("resource_attributes['service.name']"));
        Assert.Equal("h1", S("resource_attributes['host.name']"));
        Assert.Equal("https://example/schema", S("resource_schema_url"));
        Assert.Equal("scope", S("scope_name"));
        Assert.Equal("2.1", S("scope_version"));
        Assert.Equal("sv", S("scope_attributes['sk']"));
        Assert.Equal("https://example/scope", S("scope_schema_url"));
    }

    [Fact]
    public async Task NoEventsLinksOrAttributesAreEmptyNotNull()
    {
        using var fx = new ParquetFixture();
        await fx.Writer.FlushTracesAsync([Span(Resource(1, "a"), BaseNano)]);
        var files = fx.AllParquetFiles("spans");
        Assert.Equal(0L, ParquetFixture.Scalar<long>(files, "CAST(len(events) + len(links) + cardinality(attributes) AS BIGINT)"));
        Assert.Equal(1L, ParquetFixture.Scalar<long>(files, "count(*)", "events IS NOT NULL AND links IS NOT NULL AND attributes IS NOT NULL"));
    }

    [Fact]
    public async Task ASpanWithMalformedIdsAndNoServiceStillWrites()
    {
        using var fx = new ParquetFixture();
        var span = Span(new ResourceModel { TenantId = 1 }, BaseNano, traceId: "not-hex", spanId: "zz");
        span.InstrumentationScope = null;
        span.EndTimeUnixNano = span.StartTimeUnixNano - 5; // negative duration clamps to 0
        await fx.Writer.FlushTracesAsync([span]);
        var files = fx.AllParquetFiles("spans");
        Assert.Equal("", ParquetFixture.Scalar<string>(files, "service_name"));
        Assert.Equal(0UL, ParquetFixture.Scalar<ulong>(files, "trace_hi"));
        Assert.Equal(0UL, ParquetFixture.Scalar<ulong>(files, "span_id"));
        Assert.Equal(0L, ParquetFixture.Scalar<long>(files, "duration_ns"));
    }

    [Fact]
    public async Task FilesAreSortedByServiceThenStart()
    {
        using var fx = new ParquetFixture();
        var a = Resource(1, "aaa");
        var b = Resource(1, "bbb");
        await fx.Writer.FlushTracesAsync([
            Span(b, BaseNano + 5, "b2"), Span(a, BaseNano + 9, "a2"), Span(b, BaseNano + 1, "b1"), Span(a, BaseNano + 3, "a1")]);
        var files = fx.AllParquetFiles("spans");
        Assert.Equal("a1,a2,b1,b2", ParquetFixture.Scalar<string>(files, "string_agg(span_name, ',')"));
    }
}
