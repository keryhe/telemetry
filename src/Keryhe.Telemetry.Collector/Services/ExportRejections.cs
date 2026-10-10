using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Collector.Services;

/// <summary>
/// Turns an <see cref="IngestionRejectedException"/> into what an OTLP exporter understands: gRPC <c>UNAVAILABLE</c>
/// (retried by every exporter) with a <c>google.rpc.RetryInfo</c> detail in the <c>grpc-status-details-bin</c> trailer.
/// <c>UNAVAILABLE</c> rather than <c>RESOURCE_EXHAUSTED</c>: the OTLP specification makes the latter retryable only when
/// <c>RetryInfo</c> is present, so an exporter that ignores status details would drop the data.
/// </summary>
public static class ExportRejections
{
    public const string DetailsTrailer = "grpc-status-details-bin";

    public static RpcException ToRpcException(IngestionRejectedException rejection)
    {
        var status = new Google.Rpc.Status
        {
            Code = (int)StatusCode.Unavailable,
            Message = rejection.Message,
        };
        status.Details.Add(Any.Pack(new Google.Rpc.RetryInfo { RetryDelay = Duration.FromTimeSpan(rejection.RetryAfter) }));
        var trailers = new Metadata { { DetailsTrailer, status.ToByteArray() } };
        return new RpcException(new Grpc.Core.Status(StatusCode.Unavailable, rejection.Message), trailers);
    }
}
