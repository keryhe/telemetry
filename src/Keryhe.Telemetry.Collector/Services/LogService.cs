using Grpc.Core;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core.Data;
using OpenTelemetry.Proto.Collector.Logs.V1;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>The OTLP/gRPC logs endpoint: authentication is done by the pipeline, conversion and enqueueing by <see cref="OtlpLogIngestor"/>.</summary>
public class LogService(OtlpLogIngestor ingestor) : LogsService.LogsServiceBase
{
    public override async Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        if (request == null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Request cannot be null"));

        // Authenticated by ApiKeyAuthenticationHandler before this method is entered.
        var tenantId = TenantClaims.GetRequiredTenantId(context);
        try
        {
            var result = await ingestor.IngestAsync(request, tenantId, "grpc", context.CancellationToken);
            return new ExportLogsServiceResponse
            {
                PartialSuccess = new ExportLogsPartialSuccess { RejectedLogRecords = result.Rejected, ErrorMessage = result.ErrorMessage }
            };
        }
        catch (IngestionRejectedException rejection)
        {
            // UNAVAILABLE + RetryInfo: retryable by every exporter, nothing was queued.
            throw ExportRejections.ToRpcException(rejection);
        }
    }
}
