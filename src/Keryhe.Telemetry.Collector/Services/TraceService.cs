using Grpc.Core;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core.Data;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>The OTLP/gRPC traces endpoint: authentication is done by the pipeline, conversion and enqueueing by <see cref="OtlpTraceIngestor"/>.</summary>
public class TraceService(OtlpTraceIngestor ingestor) : OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceBase
{
    public override async Task<ExportTraceServiceResponse> Export(ExportTraceServiceRequest request, ServerCallContext context)
    {
        if (request == null)
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, "Request cannot be null"));

        // Authenticated by ApiKeyAuthenticationHandler before this method is entered.
        var tenantId = TenantClaims.GetRequiredTenantId(context);
        try
        {
            var result = await ingestor.IngestAsync(request, tenantId, "grpc", context.CancellationToken);
            return new ExportTraceServiceResponse
            {
                PartialSuccess = new ExportTracePartialSuccess { RejectedSpans = result.Rejected, ErrorMessage = result.ErrorMessage }
            };
        }
        catch (IngestionRejectedException rejection)
        {
            // UNAVAILABLE + RetryInfo: retryable by every exporter, nothing was queued.
            throw ExportRejections.ToRpcException(rejection);
        }
    }
}
