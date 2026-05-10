using System.Threading.Channels;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data;

public sealed class TelemetryIngestionChannel
{
    // Bounded to apply backpressure if the DB writer falls behind.
    // FullMode.Wait causes the gRPC handler to await rather than drop data.
    public Channel<List<LogRecordModel>> Logs { get; } =
        Channel.CreateBounded<List<LogRecordModel>>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public Channel<List<TraceModel>> Traces { get; } =
        Channel.CreateBounded<List<TraceModel>>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public Channel<List<MetricModel>> Metrics { get; } =
        Channel.CreateBounded<List<MetricModel>>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
}
