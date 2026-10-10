using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// Collector improvements phase 2, in process (TestServer, no database): the receive-size limit before and after gzip, the byte
/// budget on the queue, and the attribute/column limits seen in what reaches the ingestion channel.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class InputLimitsHttpTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static Metadata Bearer(bool gzip = false)
    {
        var m = new Metadata { { "authorization", $"Bearer {Key}" } };
        if (gzip) m.Add("grpc-internal-encoding-request", "gzip");
        return m;
    }

    private static OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient Traces(CollectorHost h) => new(h.Channel);

    private static ExportTraceServiceRequest Request(string spanName = "op", int attributes = 0, string attributeValue = "v", int spans = 1, string? bigValue = null)
    {
        var scope = new ScopeSpans();
        for (var i = 0; i < spans; i++)
        {
            var span = new Span
            {
                TraceId = ByteString.CopyFrom(new byte[16].Select((_, k) => (byte)(k + 1)).ToArray()),
                SpanId = ByteString.CopyFrom(new byte[8].Select((_, k) => (byte)(k + i + 1)).ToArray()),
                Name = spanName, StartTimeUnixNano = 1, EndTimeUnixNano = 2
            };
            if (bigValue is not null) span.Attributes.Add(new KeyValue { Key = "big", Value = new AnyValue { StringValue = bigValue } });
            for (var a = 0; a < attributes; a++)
                span.Attributes.Add(new KeyValue { Key = $"attr{a}", Value = new AnyValue { StringValue = attributeValue } });
            scope.Spans.Add(span);
        }
        var rs = new ResourceSpans { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
        rs.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } });
        rs.ScopeSpans.Add(scope);
        var req = new ExportTraceServiceRequest();
        req.ResourceSpans.Add(rs);
        return req;
    }

    private static Dictionary<string, string?> Config(long? maxQueuedBytes = null, int? maxMessage = null) => new()
    {
        ["Telemetry:Ingestion:MaxGateWaitMilliseconds"] = "0",
        ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1",   // these tests are about the whole queue, not one tenant's share of it
        ["Telemetry:Ingestion:MaxQueuedBytesPerSignal"] = (maxQueuedBytes ?? 256L * 1024 * 1024).ToString(),
        ["Telemetry:Collector:MaxReceiveMessageSizeBytes"] = (maxMessage ?? 4 * 1024 * 1024).ToString()
    };

    [Fact]
    public async Task A_message_over_the_receive_limit_is_refused_and_one_under_it_is_accepted()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxMessage: 100_000));
        host.Lookup.Add(Key, tenantId: 7);

        await Traces(host).ExportAsync(Request(attributes: 5, attributeValue: new string('v', 1000)), Bearer());     // ~5 KB: control
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Traces(host).ExportAsync(Request(attributes: 5, attributeValue: new string('v', 30_000)), Bearer()));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);   // ~150 KB
    }

    [Fact]
    public async Task The_limit_applies_to_the_decompressed_size_of_a_gzip_message()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxMessage: 100_000));
        host.Lookup.Add(Key, tenantId: 7);

        // 1 MB of one repeated character compresses to a few KB on the wire.
        var big = Request(attributes: 1, attributeValue: new string('a', 1_000_000));
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Traces(host).ExportAsync(big, Bearer(gzip: true)));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);

        // Control: a small gzip message passes, so the refusal above is the size and not the encoding.
        await Traces(host).ExportAsync(Request(attributes: 2, attributeValue: "small"), Bearer(gzip: true));
    }

    [Fact]
    public async Task A_long_span_name_and_many_attributes_reach_the_queue_clipped_with_dropped_counts_raised()
    {
        await using var host = await CollectorHost.StartAsync(Config());
        host.Lookup.Add(Key, tenantId: 7);

        await Traces(host).ExportAsync(Request(spanName: new string('n', 1000), attributes: 500, attributeValue: "v", bigValue: new string('b', 20_000)), Bearer());

        Assert.True(host.Ingestion.Traces.Reader.TryRead(out var batch));
        var span = Assert.Single(batch);
        Assert.Equal(255, span.Name.Length);
        Assert.Equal(128, span.Attributes!.Count);
        Assert.Equal(501 - 128, span.DroppedAttributesCount);   // the 501 sent less the 128 kept
        Assert.Equal(16_384, ((string)span.Attributes["big"]).Length);
    }

    [Fact]
    public async Task A_long_log_body_and_severity_text_are_clipped()
    {
        await using var host = await CollectorHost.StartAsync(Config());
        host.Lookup.Add(Key, tenantId: 7);

        var record = new LogRecord { TimeUnixNano = 1, SeverityText = new string('s', 400), Body = new AnyValue { StringValue = new string('b', 100_000) } };
        var rl = new ResourceLogs { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
        rl.ScopeLogs.Add(new ScopeLogs { LogRecords = { record } });
        var req = new ExportLogsServiceRequest { ResourceLogs = { rl } };
        await new OpenTelemetry.Proto.Collector.Logs.V1.LogsService.LogsServiceClient(host.Channel).ExportAsync(req, Bearer());

        Assert.True(host.Ingestion.Logs.Reader.TryRead(out var batch));
        var log = Assert.Single(batch);
        Assert.Equal(255, log.SeverityText!.Length);
        Assert.Equal(65_536, log.BodyValue!.Length);
    }

    [Fact]
    public async Task The_byte_budget_refuses_an_export_the_record_count_would_admit_and_is_released_with_it()
    {
        await using var host = await CollectorHost.StartAsync(Config(maxQueuedBytes: 3_000));
        host.Lookup.Add(Key, tenantId: 7);

        var request = Request(attributes: 5, attributeValue: new string('v', 300));   // about 1.6 KB
        var size = request.CalculateSize();
        Assert.InRange(size, 1_000, 1_900);

        await Traces(host).ExportAsync(request, Bearer());                       // first export: room
        Assert.Equal(size, host.Ingestion.TraceGate.ResidentBytes);              // reserved at the request's protobuf size

        // One record is far below the 200,000-span cap, but two exports of this size do not fit in 3 KB.
        var ex = await Assert.ThrowsAsync<RpcException>(async () => await Traces(host).ExportAsync(request, Bearer()));
        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        Assert.Equal(1, host.Ingestion.TraceGate.Resident);

        // What a worker does once the export is flushed: the records and the bytes leave together.
        Assert.True(host.Ingestion.Traces.Reader.TryRead(out var batch));
        Assert.True(host.Ingestion.TryTakeEnqueued(batch, out _, out var bytes));
        Assert.Equal(size, bytes);
        host.Ingestion.TraceGate.Release(batch.Count, bytes);
        Assert.Equal(0, host.Ingestion.TraceGate.ResidentBytes);
        await Traces(host).ExportAsync(request, Bearer());
    }
}
