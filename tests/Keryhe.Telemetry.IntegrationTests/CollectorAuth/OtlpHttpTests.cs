using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// Collector improvements phase 4, in process (TestServer, no database): OTLP/HTTP accepts what gRPC accepts and queues the same records, in
/// protobuf and JSON, plain and gzip; every error status is the specified one with a google.rpc.Status body.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class OtlpHttpTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] Signals = ["traces", "logs", "metrics"];

    // ---- requests with something in every field the converters read ----

    private static KeyValue Attr(string key, AnyValue value) => new() { Key = key, Value = value };
    private static AnyValue S(string v) => new() { StringValue = v };

    private static OpenTelemetry.Proto.Resource.V1.Resource Res()
    {
        var r = new OpenTelemetry.Proto.Resource.V1.Resource();
        r.Attributes.Add(Attr("service.name", S("checkout")));
        r.Attributes.Add(Attr("host.cpu", new AnyValue { IntValue = 8 }));
        return r;
    }

    private static ExportTraceServiceRequest TraceRequest()
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray()),
            SpanId = ByteString.CopyFrom(Enumerable.Range(0, 8).Select(i => (byte)(0x10 + i)).ToArray()),
            ParentSpanId = ByteString.CopyFrom(Enumerable.Range(0, 8).Select(i => (byte)(0x20 + i)).ToArray()),
            Name = "GET /cart", Kind = Span.Types.SpanKind.Server,
            StartTimeUnixNano = 1_700_000_000_000_000_000UL, EndTimeUnixNano = 1_700_000_000_250_000_000UL,
            Status = new OpenTelemetry.Proto.Trace.V1.Status { Code = OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode.Error, Message = "boom" },
        };
        span.Attributes.Add(Attr("http.status_code", new AnyValue { IntValue = 500 }));
        span.Attributes.Add(Attr("http.url", S("https://example.test/cart")));
        span.Attributes.Add(Attr("retry", new AnyValue { BoolValue = true }));
        span.Attributes.Add(Attr("ratio", new AnyValue { DoubleValue = 0.25 }));
        span.Events.Add(new Span.Types.Event { Name = "exception", TimeUnixNano = 1_700_000_000_100_000_000UL, Attributes = { Attr("exception.type", S("IOException")) } });
        span.Links.Add(new Span.Types.Link
        {
            TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i)).ToArray()),
            SpanId = ByteString.CopyFrom(Enumerable.Range(0, 8).Select(i => (byte)(0x30 + i)).ToArray()),
        });
        var rs = new ResourceSpans { Resource = Res() };
        rs.ScopeSpans.Add(new ScopeSpans { Scope = new InstrumentationScope { Name = "lib", Version = "1.2" }, Spans = { span } });
        return new ExportTraceServiceRequest { ResourceSpans = { rs } };
    }

    private static ExportLogsServiceRequest LogsRequest()
    {
        var log = new LogRecord
        {
            TimeUnixNano = 1_700_000_000_000_000_000UL, SeverityNumber = SeverityNumber.Warn, SeverityText = "WARN",
            Body = S("disk almost full"),
            TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray()),
            SpanId = ByteString.CopyFrom(Enumerable.Range(0, 8).Select(i => (byte)(0x10 + i)).ToArray()),
        };
        log.Attributes.Add(Attr("disk", S("/dev/sda1")));
        var rl = new ResourceLogs { Resource = Res() };
        rl.ScopeLogs.Add(new ScopeLogs { Scope = new InstrumentationScope { Name = "lib" }, LogRecords = { log } });
        return new ExportLogsServiceRequest { ResourceLogs = { rl } };
    }

    private static ExportMetricsServiceRequest MetricsRequest()
    {
        var gauge = new Metric { Name = "queue.depth", Unit = "{item}", Gauge = new Gauge { DataPoints = { new NumberDataPoint { TimeUnixNano = 1_700_000_000_000_000_000UL, AsInt = 42, Attributes = { Attr("q", S("a")) } } } } };
        var sum = new Metric
        {
            Name = "requests", Unit = "1",
            Sum = new Sum
            {
                IsMonotonic = true, AggregationTemporality = AggregationTemporality.Cumulative,
                DataPoints = { new NumberDataPoint { TimeUnixNano = 1_700_000_000_000_000_000UL, StartTimeUnixNano = 1_699_999_000_000_000_000UL, AsDouble = 12.5 } }
            }
        };
        var histogram = new Metric
        {
            Name = "latency", Unit = "ms",
            Histogram = new Histogram
            {
                AggregationTemporality = AggregationTemporality.Delta,
                DataPoints = { new HistogramDataPoint { TimeUnixNano = 1_700_000_000_000_000_000UL, Count = 3, Sum = 30, BucketCounts = { 1, 2 }, ExplicitBounds = { 10.0 } } }
            }
        };
        var rm = new ResourceMetrics { Resource = Res() };
        rm.ScopeMetrics.Add(new ScopeMetrics { Scope = new InstrumentationScope { Name = "lib" }, Metrics = { gauge, sum, histogram } });
        return new ExportMetricsServiceRequest { ResourceMetrics = { rm } };
    }

    private static IMessage RequestFor(string signal) => signal switch { "traces" => TraceRequest(), "logs" => LogsRequest(), _ => MetricsRequest() };

    // ---- OTLP/JSON encoding for the tests: the protobuf JSON form with ids as hex ----

    private static readonly HashSet<string> IdFields = ["traceId", "spanId", "parentSpanId"];

    private static string ToOtlpJson(IMessage message)
    {
        var node = JsonNode.Parse(JsonFormatter.Default.Format(message))!;
        Rewrite(node);
        return node.ToJsonString();

        static void Rewrite(JsonNode n)
        {
            if (n is JsonObject o)
                foreach (var (name, value) in o.ToList())
                {
                    if (IdFields.Contains(name) && value is JsonValue v && v.TryGetValue<string>(out var b64)) o[name] = Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant();
                    else if (value is not null) Rewrite(value);
                }
            else if (n is JsonArray a)
                foreach (var item in a) if (item is not null) Rewrite(item);
        }
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(bytes);
        return ms.ToArray();
    }

    private static HttpRequestMessage Post(string path, byte[] body, string contentType, bool gzip = false, string? key = Key, string? authorization = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "http://localhost" + path) { Content = new ByteArrayContent(gzip ? Gzip(body) : body) };
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (gzip) req.Content.Headers.ContentEncoding.Add("gzip");
        if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        else if (key is not null) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return req;
    }

    private static byte[] BodyOf(IMessage m, bool json) => json ? Encoding.UTF8.GetBytes(ToOtlpJson(m)) : m.ToByteArray();
    private static string TypeOf(bool json) => json ? "application/json" : "application/x-protobuf";
    private static string PathOf(string signal) => "/v1/" + signal;

    private static async Task ExportGrpcAsync(CollectorHost host, string signal)
    {
        var headers = new Metadata { { "authorization", $"Bearer {Key}" } };
        switch (signal)
        {
            case "traces": await new OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient(host.Channel).ExportAsync(TraceRequest(), headers); break;
            case "logs": await new OpenTelemetry.Proto.Collector.Logs.V1.LogsService.LogsServiceClient(host.Channel).ExportAsync(LogsRequest(), headers); break;
            default: await new OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService.MetricsServiceClient(host.Channel).ExportAsync(MetricsRequest(), headers); break;
        }
    }

    private static object ReadQueued(CollectorHost host, string signal)
    {
        switch (signal)
        {
            case "traces": Assert.True(host.Ingestion.Traces.Reader.TryRead(out var t)); return t!;
            case "logs": Assert.True(host.Ingestion.Logs.Reader.TryRead(out var l)); return l!;
            default: Assert.True(host.Ingestion.Metrics.Reader.TryRead(out var m)); return m!;
        }
    }

    private static string Canonical(object queued) => JsonSerializer.Serialize(queued, queued.GetType());

    // ---- the same records as gRPC ----

    [Theory]
    [InlineData("traces", false, false)]
    [InlineData("traces", true, false)]
    [InlineData("traces", false, true)]
    [InlineData("traces", true, true)]
    [InlineData("logs", false, false)]
    [InlineData("logs", true, false)]
    [InlineData("logs", false, true)]
    [InlineData("logs", true, true)]
    [InlineData("metrics", false, false)]
    [InlineData("metrics", true, false)]
    [InlineData("metrics", false, true)]
    [InlineData("metrics", true, true)]
    public async Task An_http_export_queues_exactly_what_the_same_grpc_export_queues(string signal, bool json, bool gzip)
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);

        await ExportGrpcAsync(host, signal);
        var viaGrpc = Canonical(ReadQueued(host, signal));

        var response = await host.Http.SendAsync(Post(PathOf(signal), BodyOf(RequestFor(signal), json), TypeOf(json), gzip));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TypeOf(json), response.Content.Headers.ContentType!.MediaType);
        var viaHttp = Canonical(ReadQueued(host, signal));

        Assert.Equal(viaGrpc, viaHttp);
        Assert.Contains("\"TenantId\":7", viaHttp);   // the key's tenant, on every record
    }

    [Fact]
    public async Task A_json_body_written_the_way_the_otlp_specification_shows_it_is_accepted()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);

        // Hex ids, enums as integers, 64-bit integers as strings, camelCase names, an unknown field and an unknown nested field.
        const string json = """
        {
          "resourceSpans": [{
            "resource": { "attributes": [ { "key": "service.name", "value": { "stringValue": "my.service" } } ], "somethingNew": 1 },
            "scopeSpans": [{
              "scope": { "name": "my.library", "version": "1.0.0" },
              "spans": [{
                "traceId": "5B8EFFF798038103D269B633813FC60C",
                "spanId": "EEE19B7EC3C1B174",
                "parentSpanId": "EEE19B7EC3C1B173",
                "name": "I'm a server span",
                "startTimeUnixNano": "1544712660000000000",
                "endTimeUnixNano": "1544712661000000000",
                "kind": 2,
                "status": { "code": 1 },
                "attributes": [ { "key": "my.span.attr", "value": { "intValue": "42" } } ],
                "futureField": { "nested": true }
              }]
            }]
          }]
        }
        """;

        var response = await host.Http.SendAsync(Post("/v1/traces", Encoding.UTF8.GetBytes(json), "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(host.Ingestion.Traces.Reader.TryRead(out var batch));
        var span = Assert.Single(batch);
        Assert.Equal("5b8efff798038103d269b633813fc60c", span.TraceIdHex);
        Assert.Equal("eee19b7ec3c1b174", span.SpanIdHex);
        Assert.Equal("eee19b7ec3c1b173", span.ParentSpanIdHex);
        Assert.Equal(Keryhe.Telemetry.Core.Models.SpanKind.SERVER, span.Kind);
        Assert.Equal(1544712660000000000L, span.StartTimeUnixNano);
        Assert.Equal(42L, span.Attributes!["my.span.attr"]);
        Assert.Equal("my.service", span.Resource!.Attributes["service.name"]);
    }

    [Fact]
    public async Task A_rejected_record_is_a_200_with_a_partial_success_not_an_error_status()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);
        var bad = TraceRequest();
        bad.ResourceSpans[0].ScopeSpans[0].Spans[0].SpanId = ByteString.Empty;   // a span without a span id cannot be stored

        var response = await host.Http.SendAsync(Post("/v1/traces", bad.ToByteArray(), "application/x-protobuf"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = ExportTraceServiceResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, body.PartialSuccess.RejectedSpans);
        Assert.NotEmpty(body.PartialSuccess.ErrorMessage);

        // Control: a good export has no partial-success block at all.
        var ok = await host.Http.SendAsync(Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf"));
        Assert.Null(ExportTraceServiceResponse.Parser.ParseFrom(await ok.Content.ReadAsByteArrayAsync()).PartialSuccess);
    }

    // ---- unsupported and malformed requests ----

    private static async Task<Google.Rpc.Status> StatusBodyAsync(HttpResponseMessage response, bool json)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return json ? Google.Rpc.Status.Parser.ParseJson(Encoding.UTF8.GetString(bytes)) : Google.Rpc.Status.Parser.ParseFrom(bytes);
    }

    [Fact]
    public async Task A_wrong_content_type_or_encoding_is_415_and_a_missing_one_too()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);
        var body = TraceRequest().ToByteArray();

        var wrongType = await host.Http.SendAsync(Post("/v1/traces", body, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal((int)StatusCode.InvalidArgument, (await StatusBodyAsync(wrongType, json: true)).Code);

        var brotli = Post("/v1/traces", body, "application/x-protobuf");
        brotli.Content!.Headers.ContentEncoding.Add("br");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await host.Http.SendAsync(brotli)).StatusCode);

        var noType = Post("/v1/traces", body, "application/x-protobuf");
        noType.Content!.Headers.ContentType = null;
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await host.Http.SendAsync(noType)).StatusCode);

        // Control: the same body with the right headers is accepted, with an explicit identity encoding too.
        var identity = Post("/v1/traces", body, "application/x-protobuf; charset=utf-8");
        identity.Content!.Headers.ContentEncoding.Add("identity");
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(identity)).StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_limit_is_413_even_when_the_compressed_body_is_small()
    {
        await using var host = await CollectorHost.StartAsync(new() { ["Telemetry:Collector:MaxReceiveMessageSizeBytes"] = "100000" });
        host.Lookup.Add(Key, tenantId: 7);

        var big = TraceRequest();
        big.ResourceSpans[0].ScopeSpans[0].Spans[0].Attributes.Add(Attr("blob", S(new string('a', 1_000_000))));
        var plain = big.ToByteArray();
        var gz = Gzip(plain);
        Assert.True(gz.Length < 100_000 && plain.Length > 100_000);   // the premise: small on the wire, big once expanded

        var asGzip = await host.Http.SendAsync(Post("/v1/traces", plain, "application/x-protobuf", gzip: true));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, asGzip.StatusCode);
        var asPlain = await host.Http.SendAsync(Post("/v1/traces", plain, "application/x-protobuf"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, asPlain.StatusCode);
        Assert.Equal((int)StatusCode.ResourceExhausted, (await StatusBodyAsync(asPlain, json: false)).Code);

        // Control: a small export on the same host is accepted.
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", gzip: true))).StatusCode);
    }

    [Theory]
    [InlineData("protobuf-garbage")]
    [InlineData("json-garbage")]
    [InlineData("json-bad-hex")]
    [InlineData("gzip-garbage")]
    public async Task A_body_that_is_not_an_otlp_export_is_400_with_a_status_body(string kind)
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);

        var request = kind switch
        {
            "protobuf-garbage" => Post("/v1/traces", [0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x01], "application/x-protobuf"),
            "json-garbage" => Post("/v1/traces", Encoding.UTF8.GetBytes("{ not json"), "application/json"),
            "json-bad-hex" => Post("/v1/traces", Encoding.UTF8.GetBytes("""{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"zz-not-hex"}]}]}]}"""), "application/json"),
            _ => Post("/v1/traces", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], "application/x-protobuf"),
        };
        if (kind == "gzip-garbage") request.Content!.Headers.ContentEncoding.Add("gzip");

        var response = await host.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((int)StatusCode.InvalidArgument, (await StatusBodyAsync(response, json: kind.StartsWith("json"))).Code);
        Assert.False(host.Ingestion.Traces.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Only_post_is_routed_and_a_base_path_moves_the_routes()
    {
        await using var host = await CollectorHost.StartAsync(new() { ["Telemetry:Collector:HttpBasePath"] = "/otlp/" });
        host.Lookup.Add(Key, tenantId: 7);
        var body = TraceRequest().ToByteArray();

        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.SendAsync(Post("/v1/traces", body, "application/x-protobuf"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(Post("/otlp/v1/traces", body, "application/x-protobuf"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Http.GetAsync("http://localhost/otlp/v1/traces")).StatusCode);
    }

    // ---- authentication ----

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task A_key_that_is_not_usable_is_401_with_the_reason_in_a_status_body(string reason)
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);
        host.Lookup.Add("ktel_old-key-cccccccccccccccccccccccccccccccccc", tenantId: 7, expiresAt: host.Clock.GetUtcNow().AddMinutes(-1));

        var request = reason switch
        {
            "missing" => Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", key: null),
            "malformed" => Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", authorization: "Basic dXNlcjpwYXNz"),
            "invalid" => Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", key: "ktel_nope"),
            _ => Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", key: "ktel_old-key-cccccccccccccccccccccccccccccccccc"),
        };
        var response = await host.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var status = await StatusBodyAsync(response, json: false);
        Assert.Equal((int)StatusCode.Unauthenticated, status.Code);
        Assert.Contains(reason switch { "missing" => "missing", "malformed" => "malformed", "invalid" => "invalid", _ => "expired" }, status.Message);
        Assert.Equal(1, host.AuthFailureCount("traces", reason));
        Assert.False(host.Ingestion.Traces.Reader.TryRead(out _));       // the body was never ingested
    }

    [Fact]
    public async Task A_failing_key_lookup_is_503_with_retry_after()
    {
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add(Key, tenantId: 7);
        host.Lookup.Throw = true;

        var response = await host.Http.SendAsync(Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
        Assert.Equal((int)StatusCode.Unavailable, (await StatusBodyAsync(response, json: false)).Code);
    }

    [Fact]
    public async Task An_address_out_of_failed_attempts_is_429_without_retry_after_and_a_cached_key_still_works()
    {
        await using var host = await CollectorHost.StartAsync(fakeClientAddresses: true);
        host.Lookup.Add(Key, tenantId: 7);
        HttpRequestMessage From(string key) { var r = Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", key: key); r.Headers.Add("X-Test-IP", "203.0.113.9"); return r; }

        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(From(Key))).StatusCode);                  // warms the positive cache
        for (var i = 0; i < 25; i++) await host.Http.SendAsync(From($"ktel_bad-{i}"));

        var throttled = await host.Http.SendAsync(From("ktel_bad-next"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Null(throttled.Headers.RetryAfter);
        Assert.Equal((int)StatusCode.ResourceExhausted, (await StatusBodyAsync(throttled, json: false)).Code);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(From(Key))).StatusCode);
    }

    // ---- backpressure ----

    [Fact]
    public async Task A_full_queue_is_503_with_retry_after_before_the_body_is_read_and_room_makes_it_200()
    {
        await using var host = await CollectorHost.StartAsync(new()
        {
            ["Telemetry:Ingestion:MaxQueuedSpans"] = "1",
            ["Telemetry:Ingestion:MaxGateWaitMilliseconds"] = "50",
            ["Telemetry:Ingestion:RejectRetryDelayMilliseconds"] = "2000",
            ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1",
        });
        host.Lookup.Add(Key, tenantId: 7);
        var body = TraceRequest().ToByteArray();

        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(Post("/v1/traces", body, "application/x-protobuf"))).StatusCode);   // fills the one-span queue
        var refused = await host.Http.SendAsync(Post("/v1/traces", body, "application/x-protobuf"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 2, 3);
        var status = await StatusBodyAsync(refused, json: false);
        Assert.Equal((int)StatusCode.Unavailable, status.Code);
        Assert.Contains("throttled", status.Message);

        // The pre-check refuses without reading the body: not even a corrupt one is a 400 now.
        await Task.Delay(100);
        var corrupt = await host.Http.SendAsync(Post("/v1/traces", [0xFF, 0xFF, 0xFF], "application/x-protobuf"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);

        Assert.True(host.Ingestion.Traces.Reader.TryRead(out var queued));
        host.Ingestion.TraceGate.Release(queued.Count, 0);
        Assert.Equal(HttpStatusCode.OK, (await host.Http.SendAsync(Post("/v1/traces", body, "application/x-protobuf"))).StatusCode);
    }

    [Fact]
    public async Task Http_records_are_counted_with_the_http_protocol_tag()
    {
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        await using var host = await CollectorHost.StartAsync();
        host.Lookup.Add("ktel_proto-tag-key-dddddddddddddddddddddddddddd", tenantId: 99123);
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var own = host.Ingestion.GetType(); // the channel's metrics are the host's own; filter on the unique tenant instead
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == "Keryhe.Telemetry.Ingestion" && i.Name.EndsWith("records_accepted")) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            string? tenant = null, protocol = null;
            foreach (var t in tags) { if (t.Key == "tenant") tenant = t.Value?.ToString(); if (t.Key == "protocol") protocol = t.Value?.ToString(); }
            if (tenant == "99123") seen.AddOrUpdate(protocol!, v, (_, o) => o + v);
        });
        listener.Start();

        var headers = new Metadata { { "authorization", "Bearer ktel_proto-tag-key-dddddddddddddddddddddddddddd" } };
        await new OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient(host.Channel).ExportAsync(TraceRequest(), headers);
        await host.Http.SendAsync(Post("/v1/traces", TraceRequest().ToByteArray(), "application/x-protobuf", key: "ktel_proto-tag-key-dddddddddddddddddddddddddddd"));

        Assert.Equal(1, seen.GetValueOrDefault("grpc"));
        Assert.Equal(1, seen.GetValueOrDefault("http"));
    }
}
