namespace Keryhe.Telemetry.TestDataGenerator.Model;

public enum InstrumentKind
{
    HistogramExplicit,
    HistogramExponential,
    /// <summary>Monotonic cumulative sum.</summary>
    Counter,
    /// <summary>Non-monotonic sum, read periodically.</summary>
    UpDownSum,
    Gauge,
}

public sealed record InstrumentDef(string Name, string Unit, string Description, InstrumentKind Kind, double[]? Boundaries = null)
{
    public bool IsPeriodicSample => Kind is InstrumentKind.UpDownSum or InstrumentKind.Gauge;
}

/// <summary>The metric catalog, shared by the SDK sink (views, instruments) and the OTLP sink (aggregation) so they agree.</summary>
public static class Instruments
{
    /// <summary>The bucket advice from the OTel HTTP/DB semantic conventions, in seconds.</summary>
    public static readonly double[] DurationSeconds = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    /// <summary>Scale of the OTLP sink's exponential histograms (the SDK picks its own, up to 20).</summary>
    public const int ExponentialScale = 4;

    public const string HttpServerDuration = "http.server.request.duration";
    public const string HttpClientDuration = "http.client.request.duration";
    public const string DbClientDuration = "db.client.operation.duration";
    public const string MessagingPublishDuration = "messaging.client.operation.duration";
    public const string MessagingProcessDuration = "messaging.process.duration";
    public const string OrdersPlaced = "shop.orders.placed";
    public const string PaymentsProcessed = "shop.payments.processed";
    public const string ProcessCpu = "process.cpu.utilization";
    public const string ProcessMemory = "process.memory.usage";
    public const string DbConnections = "db.client.connection.count";

    public static readonly IReadOnlyList<InstrumentDef> All =
    [
        new(HttpServerDuration, "s", "Duration of HTTP server requests.", InstrumentKind.HistogramExplicit, DurationSeconds),
        new(HttpClientDuration, "s", "Duration of outbound HTTP requests.", InstrumentKind.HistogramExplicit, DurationSeconds),
        // Exponential on purpose, so the exponential-histogram read path has real data.
        new(DbClientDuration, "s", "Duration of database client operations.", InstrumentKind.HistogramExponential),
        new(MessagingPublishDuration, "s", "Duration of messaging publish operations.", InstrumentKind.HistogramExplicit, DurationSeconds),
        new(MessagingProcessDuration, "s", "Duration of processing a received message.", InstrumentKind.HistogramExplicit, DurationSeconds),
        new(OrdersPlaced, "{order}", "Orders successfully placed.", InstrumentKind.Counter),
        new(PaymentsProcessed, "{payment}", "Payments processed, by result.", InstrumentKind.Counter),
        new(ProcessCpu, "1", "Difference in process.cpu.time since the last measurement, divided by the elapsed time and number of CPUs.", InstrumentKind.Gauge),
        new(ProcessMemory, "By", "The amount of physical memory in use.", InstrumentKind.UpDownSum),
        new(DbConnections, "{connection}", "The number of connections currently in the pool, by state.", InstrumentKind.UpDownSum),
    ];

    private static readonly Dictionary<string, InstrumentDef> ByNameMap = All.ToDictionary(i => i.Name);

    public static InstrumentDef Get(string name) => ByNameMap[name];
}
