using System.Net;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.TestDataGenerator.Sinks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

/// <summary>
/// The real OpenTelemetry .NET SDK exporting over <c>http/protobuf</c> (the generator's <c>Generator:Protocol</c> option) into the
/// collector's real OTLP/HTTP endpoints, key authentication and ingestion channel on a Kestrel HTTP/1.1 port. Not a fake: a client that is
/// not ours, speaking its own idea of OTLP/HTTP (paths, headers, content type, gzip), must be accepted and its records queued.
/// </summary>
public class HttpProtobufInteropTests
{
    private sealed class SdkEvents : System.Diagnostics.Tracing.EventListener
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name.StartsWith("OpenTelemetry")) EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Warning);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e) =>
            Errors.Enqueue($"{e.EventSource.Name}/{e.EventName}: {string.Join(", ", e.Payload ?? [])}");
    }

    private const string ApiKey = "ktel_interop-key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class Lookup : IApiKeyLookup
    {
        public Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken) =>
            Task.FromResult<ApiKeyLookupResult?>(keyHash == Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash(ApiKey)
                ? new ApiKeyLookupResult(TenantId: 7, ApiKeyId: 1, ExpiresAt: null) : null);
    }

    [Fact]
    public async Task The_sdk_over_http_protobuf_is_accepted_for_traces_logs_and_metrics()
    {
        using var sdkEvents = new SdkEvents();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http1));
        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);
        foreach (var d in builder.Services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace?.StartsWith("Keryhe.Telemetry") == true).ToList())
            builder.Services.Remove(d);
        builder.Services.AddSingleton<IApiKeyLookup>(new Lookup());

        await using var app = builder.Build();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.Use(async (ctx, next) =>
        {
            await next();
            seen.Enqueue($"{ctx.Request.Method} {ctx.Request.Path} {ctx.Request.ContentType} enc={ctx.Request.Headers.ContentEncoding} -> {ctx.Response.StatusCode}");
        });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKeryheTelemetryCollector();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var channel = app.Services.GetRequiredService<TelemetryIngestionChannel>();

            // A tenant of its own: the SDK providers listen by activity-source name (derived from the pod, which includes the tenant), process-wide, so
            // the same pods used by another test running at the same time would have their spans exported by this test's providers too.
            var options = TestSupport.Options();
            options.Tenants[0].Name = "interop-tenant";
            var set = TestSupport.Evening(options, minutes: 3);
            await using (var sdk = new SdkSink(new Uri(address), ApiKey, metricIntervalSeconds: 3600, httpProtobuf: true))
                await sdk.WriteAsync(set.Chunk, CancellationToken.None);
            // Disposing the SDK sink flushes every provider, which is what delivers the metrics.

            var spans = Drain(channel.Traces.Reader).Sum(batch => batch.Count);
            var logs = Drain(channel.Logs.Reader).Sum(batch => batch.Count);
            var metrics = Drain(channel.Metrics.Reader).Sum(batch => batch.Count);

            // The same chunk through the same SDK over gRPC into the fake collector is the yardstick: what the SDK emits, the HTTP path must queue.
            await using var fake = await FakeCollector.StartAsync();
            await using (var sdk = new SdkSink(fake.Endpoint, "any-key", metricIntervalSeconds: 3600))
                await sdk.WriteAsync(set.Chunk, CancellationToken.None);
            var expectedSpans = fake.Traces.Sum(t => t.Request.ResourceSpans.Sum(rs => rs.ScopeSpans.Sum(ss => ss.Spans.Count)));
            var expectedLogs = fake.Logs.Sum(t => t.Request.ResourceLogs.Sum(rl => rl.ScopeLogs.Sum(sl => sl.LogRecords.Count)));
            var expectedMetrics = fake.Metrics.Sum(t => t.Request.ResourceMetrics.Sum(rm => rm.ScopeMetrics.Sum(sm => sm.Metrics.Count)));

            Assert.True(expectedSpans > 0 && expectedLogs > 0 && expectedMetrics > 0);
            Assert.True(spans == expectedSpans, $"spans {spans} vs {expectedSpans}; requests seen: {string.Join(" | ", seen.Take(8))}");
            Assert.True(logs == expectedLogs, $"logs {logs} vs {expectedLogs}; sdk events: {string.Join(" | ", sdkEvents.Errors.Where(e => !e.Contains("MetricInstrumentIgnored")).Take(8))}");
            Assert.Equal(expectedMetrics, metrics);
        }
        finally
        {
            await app.StopAsync();
        }

        static List<List<T>> Drain<T>(System.Threading.Channels.ChannelReader<List<T>> reader)
        {
            var all = new List<List<T>>();
            while (reader.TryRead(out var batch)) all.Add(batch);
            return all;
        }
    }
}
