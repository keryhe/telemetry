using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

/// <summary>An in-process OTLP/gRPC endpoint that records every export, with the bearer token each one carried.</summary>
public sealed class FakeCollector : IAsyncDisposable
{
    private readonly WebApplication _app;

    public ConcurrentQueue<(string Auth, ExportTraceServiceRequest Request)> Traces { get; } = new();
    public ConcurrentQueue<(string Auth, ExportLogsServiceRequest Request)> Logs { get; } = new();
    public ConcurrentQueue<(string Auth, ExportMetricsServiceRequest Request)> Metrics { get; } = new();

    private FakeCollector(WebApplication app, Uri endpoint)
    {
        _app = app;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }

    public static async Task<FakeCollector> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<FakeSink>();
        var app = builder.Build();
        FakeCollector? self = null;
        app.Use(async (ctx, next) =>
        {
            // The services read the bearer token off the request through this accessor.
            ctx.Items["auth"] = ctx.Request.Headers.Authorization.ToString();
            await next();
        });
        app.MapGrpcService<TraceSvc>();
        app.MapGrpcService<LogsSvc>();
        app.MapGrpcService<MetricsSvc>();
        app.Services.GetRequiredService<FakeSink>();
        await app.StartAsync();
        var address = app.Urls.First();
        self = new FakeCollector(app, new Uri(address));
        app.Services.GetRequiredService<FakeSink>().Owner = self;
        return self;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    internal sealed class FakeSink
    {
        public FakeCollector? Owner { get; set; }
    }

    private sealed class TraceSvc(FakeSink sink) : TraceService.TraceServiceBase
    {
        public override Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
        {
            sink.Owner!.Traces.Enqueue((Auth(context), request));
            return Task.FromResult(new ExportTraceServiceResponse());
        }
    }

    private sealed class LogsSvc(FakeSink sink) : LogsService.LogsServiceBase
    {
        public override Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
        {
            sink.Owner!.Logs.Enqueue((Auth(context), request));
            return Task.FromResult(new ExportLogsServiceResponse());
        }
    }

    private sealed class MetricsSvc(FakeSink sink) : MetricsService.MetricsServiceBase
    {
        public override Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
        {
            sink.Owner!.Metrics.Enqueue((Auth(context), request));
            return Task.FromResult(new ExportMetricsServiceResponse());
        }
    }

    private static string Auth(ServerCallContext context) =>
        context.RequestHeaders.FirstOrDefault(h => h.Key == "authorization")?.Value ?? "";
}
