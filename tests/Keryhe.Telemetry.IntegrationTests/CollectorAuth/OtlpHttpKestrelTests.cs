using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Grpc.Net.Client;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// A real Kestrel listener (not TestServer): one TLS port with <c>Http1AndHttp2</c>, the way the shipped <c>appsettings.json</c> configures
/// the collector's <c>Https</c> endpoint. ALPN must give a gRPC client HTTP/2 and an OTLP/HTTP client HTTP/1.1 on the same port, and both
/// must end in the same ingestion channel. No database; the ingestion workers are not started.
/// </summary>
[Trait("Suite", "CollectorAuth")]
public class OtlpHttpKestrelTests
{
    private const string Key = "ktel_test-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Kestrel/SslStream cannot use an ephemeral key on every platform: round-trip through a PKCS#12 blob.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, "x"), "x");
    }

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

    [Fact]
    public async Task One_tls_port_serves_grpc_over_http2_and_otlp_http_over_http1()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        using var cert = SelfSigned();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(cert);
        }));
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
            var port = new Uri(address).Port;
            var channel = app.Services.GetRequiredService<TelemetryIngestionChannel>();

            // Two handlers: a gRPC channel takes over its handler's connection logic, so an HttpClient cannot share it.
            static SocketsHttpHandler Trusting() => new() { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
            using var grpc = GrpcChannel.ForAddress($"https://127.0.0.1:{port}", new GrpcChannelOptions { HttpHandler = Trusting(), DisposeHttpClient = true });
            var metadata = new Grpc.Core.Metadata { { "authorization", $"Bearer {Key}" } };
            await new OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceClient(grpc).ExportAsync(Request(), metadata);
            Assert.True(channel.Traces.Reader.TryRead(out var viaGrpc));

            using var http = new HttpClient(Trusting()) { DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            var post = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/v1/traces") { Content = new ByteArrayContent(Request().ToByteArray()) };
            post.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-protobuf");
            post.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Key}");
            var response = await http.SendAsync(post);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(HttpVersion.Version11, response.Version);
            Assert.True(channel.Traces.Reader.TryRead(out var viaHttp));
            Assert.Equal(viaGrpc.Single().SpanIdHex, viaHttp.Single().SpanIdHex);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
