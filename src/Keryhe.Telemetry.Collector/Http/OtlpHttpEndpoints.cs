using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Collector.Services;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Keryhe.Telemetry.Collector.Http;

/// <summary>
/// OTLP/HTTP: <c>POST /v1/traces</c>, <c>/v1/logs</c> and <c>/v1/metrics</c> (protobuf or JSON, optionally gzip), minimal-API endpoints
/// carrying the same authorization policy and authentication handler as the gRPC services, and the same ingestors behind them. Status
/// codes follow the OTLP specification: <c>200</c> with the export response (a partial success included); <c>400</c> for a body that is not
/// an OTLP export; <c>401</c> for a key that is missing, malformed, invalid or expired (written by the handler); <c>413</c> for a body over
/// <c>MaxReceiveMessageSizeBytes</c> once decompressed; <c>415</c> for an unsupported content type or encoding; <c>429</c> for an address
/// that has used up its failed attempts (handler; no <c>Retry-After</c>, so exporters stop); and <c>503</c> with <c>Retry-After</c> when
/// the queue is full, a tenant is over its quota or rate, the collector is shutting down, or the key lookup is unavailable. Every error
/// body is a <c>google.rpc.Status</c> in the request's content type.
/// </summary>
public static class OtlpHttpEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string basePath)
    {
        Map(endpoints, basePath + "/v1/traces", "traces", ExportTraceServiceRequest.Parser,
            (IServiceProvider sp) => sp.GetRequiredService<OtlpTraceIngestor>(),
            (ingestor, request, tenantId, ct) => ingestor.IngestAsync(request, tenantId, "http", ct),
            result => new ExportTraceServiceResponse
            {
                PartialSuccess = result.Rejected > 0 || result.ErrorMessage.Length > 0
                    ? new ExportTracePartialSuccess { RejectedSpans = result.Rejected, ErrorMessage = result.ErrorMessage } : null
            },
            channel => channel.TraceGate);

        Map(endpoints, basePath + "/v1/logs", "logs", ExportLogsServiceRequest.Parser,
            (IServiceProvider sp) => sp.GetRequiredService<OtlpLogIngestor>(),
            (ingestor, request, tenantId, ct) => ingestor.IngestAsync(request, tenantId, "http", ct),
            result => new ExportLogsServiceResponse
            {
                PartialSuccess = result.Rejected > 0 || result.ErrorMessage.Length > 0
                    ? new ExportLogsPartialSuccess { RejectedLogRecords = result.Rejected, ErrorMessage = result.ErrorMessage } : null
            },
            channel => channel.LogGate);

        Map(endpoints, basePath + "/v1/metrics", "metrics", ExportMetricsServiceRequest.Parser,
            (IServiceProvider sp) => sp.GetRequiredService<OtlpMetricIngestor>(),
            (ingestor, request, tenantId, ct) => ingestor.IngestAsync(request, tenantId, "http", ct),
            result => new ExportMetricsServiceResponse
            {
                PartialSuccess = result.Rejected > 0 || result.ErrorMessage.Length > 0
                    ? new ExportMetricsPartialSuccess { RejectedDataPoints = result.Rejected, ErrorMessage = result.ErrorMessage } : null
            },
            channel => channel.MetricGate);
    }

    private static void Map<TReq, TResp, TIngestor>(
        IEndpointRouteBuilder endpoints, string pattern, string signal, MessageParser<TReq> parser,
        Func<IServiceProvider, TIngestor> ingestorOf,
        Func<TIngestor, TReq, long, CancellationToken, Task<IngestResult>> ingest,
        Func<IngestResult, TResp> response,
        Func<TelemetryIngestionChannel, RecordCountGate> gateOf)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        endpoints
            .MapPost(pattern, async (HttpContext ctx) =>
            {
                var services = ctx.RequestServices;
                var type = OtlpHttpBodyReader.ContentTypeOf(ctx.Request);
                // Errors are written in the request's content type; a request with none we understand gets JSON.
                var replyAs = type ?? OtlpContentType.Json;
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Keryhe.Telemetry.Collector.Http");
                var metrics = services.GetRequiredService<IngestionMetrics>();
                var tenantId = TenantClaims.GetTenantId(ctx);

                try
                {
                    if (tenantId is null) { await WriteStatusAsync(ctx, 401, StatusCode.Unauthenticated, "Not authenticated.", replyAs); return; }
                    if (type is null)
                    {
                        await WriteStatusAsync(ctx, 415, StatusCode.InvalidArgument,
                            $"Content-Type must be {OtlpHttpBodyReader.ProtobufMediaType} or {OtlpHttpBodyReader.JsonMediaType}.", replyAs);
                        return;
                    }

                    var channel = services.GetRequiredService<TelemetryIngestionChannel>();
                    // A queue that is already refusing callers refuses this one before its body is read.
                    channel.ThrowIfSaturated(signal, gateOf(channel));

                    var maxBytes = services.GetRequiredService<IOptions<TelemetryCollectorOptions>>().Value.MaxReceiveMessageSizeBytes;
                    var request = await OtlpHttpBodyReader.ReadAsync(ctx.Request, type.Value, parser, maxBytes > 0 ? maxBytes : long.MaxValue, ctx.RequestAborted);

                    var result = await ingest(ingestorOf(services), request, tenantId.Value, ctx.RequestAborted);
                    await WriteMessageAsync(ctx, 200, response(result), replyAs);
                }
                catch (IngestionRejectedException rejection)
                {
                    await WriteStatusAsync(ctx, 503, StatusCode.Unavailable, rejection.Message, replyAs, rejection.RetryAfter);
                }
                catch (PayloadTooLargeException ex)
                {
                    if (tenantId is { } t) metrics.RecordRefused(signal, t, RefusalReasons.Invalid, 1, "http");
                    await WriteStatusAsync(ctx, 413, StatusCode.ResourceExhausted, ex.Message, replyAs);
                }
                catch (UnsupportedOtlpRequestException ex)
                {
                    await WriteStatusAsync(ctx, 415, StatusCode.InvalidArgument, ex.Message, replyAs);
                }
                catch (InvalidOtlpBodyException ex)
                {
                    if (tenantId is { } t) metrics.RecordRefused(signal, t, RefusalReasons.Invalid, 1, "http");
                    logger.LogDebug(ex, "Rejected a malformed OTLP/HTTP {Signal} body", signal);
                    await WriteStatusAsync(ctx, 400, StatusCode.InvalidArgument, ex.Message, replyAs);
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    // The client went away; nothing to answer.
                }
            })
            .WithMetadata(CollectorEndpointMetadata.Http)
            .RequireAuthorization(CollectorAuthorization.CollectorPolicy)
            .WithDisplayName($"OTLP/HTTP {signal}");
    }

    private static Task WriteMessageAsync(HttpContext ctx, int httpStatus, IMessage message, OtlpContentType type)
    {
        ctx.Response.StatusCode = httpStatus;
        if (type == OtlpContentType.Json)
        {
            ctx.Response.ContentType = OtlpHttpBodyReader.JsonMediaType;
            return ctx.Response.WriteAsync(JsonFormatter.Default.Format(message));
        }
        ctx.Response.ContentType = OtlpHttpBodyReader.ProtobufMediaType;
        return ctx.Response.Body.WriteAsync(message.ToByteArray()).AsTask();
    }

    /// <summary>Writes an error as a <c>google.rpc.Status</c> body, with <c>Retry-After</c> (whole seconds, rounded up) when a delay is given.</summary>
    public static Task WriteStatusAsync(HttpContext ctx, int httpStatus, StatusCode code, string message, OtlpContentType type, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } delay)
            ctx.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return WriteMessageAsync(ctx, httpStatus, new Google.Rpc.Status { Code = (int)code, Message = message }, type);
    }
}
