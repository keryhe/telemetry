using System.Net;
using Google.Protobuf;
using Grpc.Net.Client;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// <c>Telemetry:Collector:MaxConnectionAgeSeconds</c> on a real Kestrel port (TestServer has no connections): a client exporting steadily is sent a
/// HTTP/2 GOAWAY once its connection is old enough, reconnects, and loses no export. No database; the workers are not started.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class ConnectionAgeTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ExportTraceServiceRequest Request()
    {
        var span = new Span
        {
            TraceId = ByteString.CopyFrom(Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray()),
            SpanId = ByteString.CopyFrom(Enumerable.Range(0, 8).Select(i => (byte)(i + 1)).ToArray()),
            Name = "op", StartTimeUnixNano = 1, EndTimeUnixNano = 2
        };
        var rs = new ResourceSpans { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
        rs.Resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "svc" } });
        rs.ScopeSpans.Add(new ScopeSpans { Spans = { span } });
        return new ExportTraceServiceRequest { ResourceSpans = { rs } };
    }

    /// <summary>Exports for <paramref name="seconds"/>, one export every 100 ms; returns (connections opened, exports ok, exports failed).</summary>
    private static async Task<(int Connections, int Ok, int Failed)> RunAsync(int? maxAgeSeconds, double seconds)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Collector:MaxConnectionAgeSeconds"] = (maxAgeSeconds ?? 0).ToString(),
            ["Telemetry:Ingestion:MaxQueuedSpans"] = "1000000",
            ["Telemetry:Ingestion:TenantQuota:MaxShare"] = "1",
        });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);
        foreach (var d in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace?.StartsWith("Keryhe.Telemetry") == true).ToList())
            builder.Services.Remove(d);
        var lookup = new CollectorHost.FakeLookup();
        lookup.Add(Key, tenantId: 7);
        builder.Services.AddSingleton<IApiKeyLookup>(lookup);

        await using var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKeryheTelemetryCollector();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var connections = 0;
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = false,
                ConnectCallback = async (context, ct) =>
                {
                    Interlocked.Increment(ref connections);
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                    await socket.ConnectAsync(context.DnsEndPoint, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            };
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
            var client = new OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient(channel);
            var headers = new Grpc.Core.Metadata { { "authorization", $"Bearer {Key}" } };

            int ok = 0, failed = 0;
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                try { await client.ExportAsync(Request(), headers); ok++; }
                catch (Grpc.Core.RpcException) { failed++; }
                await Task.Delay(100);
            }
            return (connections, ok, failed);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_steady_client_is_moved_to_a_new_connection_without_losing_an_export()
    {
        var (connections, ok, failed) = await RunAsync(maxAgeSeconds: 1, seconds: 4);

        Assert.InRange(connections, 3, 5);      // 4 s at a 1 to 1.1 s age
        Assert.Equal(0, failed);
        Assert.True(ok >= 30, $"only {ok} exports completed");
    }

    [Fact]
    public async Task Without_a_maximum_age_the_connection_lives_as_long_as_the_client()
    {
        var (connections, ok, failed) = await RunAsync(maxAgeSeconds: null, seconds: 3);

        Assert.Equal(1, connections);
        Assert.Equal(0, failed);
        Assert.True(ok >= 20);
    }
}
