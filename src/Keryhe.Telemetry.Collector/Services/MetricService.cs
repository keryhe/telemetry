using Grpc.Core;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core.Data;
using OpenTelemetry.Proto.Collector.Metrics.V1;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>The OTLP/gRPC metrics endpoint: authentication is done by the pipeline, conversion and enqueueing by <see cref="OtlpMetricIngestor"/>.</summary>
public class MetricService(OtlpMetricIngestor ingestor) : MetricsService.MetricsServiceBase
{
    public override async Task<ExportMetricsServiceResponse> Export(ExportMetricsServiceRequest request, ServerCallContext context)
    {
        if (request == null)
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, "Request cannot be null"));

        // Authenticated by ApiKeyAuthenticationHandler before this method is entered.
        var tenantId = TenantClaims.GetRequiredTenantId(context);
        try
        {
            var result = await ingestor.IngestAsync(request, tenantId, "grpc", context.CancellationToken);
            return new ExportMetricsServiceResponse
            {
                PartialSuccess = new ExportMetricsPartialSuccess { RejectedDataPoints = result.Rejected, ErrorMessage = result.ErrorMessage }
            };
        }
        catch (IngestionRejectedException rejection)
        {
            // UNAVAILABLE + RetryInfo: retryable by every exporter, nothing was queued.
            throw ExportRejections.ToRpcException(rejection);
        }
    }
}
