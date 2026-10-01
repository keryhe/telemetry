using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keryhe.Telemetry.StressTests.Load;

/// <summary>An inclusive integer range, sampled uniformly.</summary>
public sealed record IntRange(int Min, int Max)
{
    public int Sample(Random rng) => Min >= Max ? Min : rng.Next(Min, Max + 1);
}

/// <summary>
/// The write-load half of a stress profile (stress-test plan, Phase 2). Every value has a default,
/// so a profile JSON only lists what it overrides. Given the same <see cref="Seed"/>, a run sends
/// the same data shape on the same rate schedule; only absolute timestamps differ.
/// </summary>
public sealed class LoadProfile
{
    public int Seed { get; set; } = 1;

    /// <summary>Relative traffic weight per tenant, by position in the seeded tenant list. Missing entries weigh 1.</summary>
    public List<double> TenantWeights { get; set; } = [];
    public int ServicesPerTenant { get; set; } = 3;
    public int OperationsPerService { get; set; } = 8;

    public TraceLoad Traces { get; set; } = new();
    public LogLoad Logs { get; set; } = new();
    public MetricLoad Metrics { get; set; } = new();
    public TransportLoad Transport { get; set; } = new();
    public TimeShaping Time { get; set; } = new();

    public static LoadProfile Load(string path) =>
        JsonSerializer.Deserialize<LoadProfile>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"'{path}' is empty.");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class TraceLoad
{
    /// <summary>Target SPANS per second (the unit the ingestion gate and the plan's profile table use). 0 disables traces.</summary>
    public double SpansPerSecond { get; set; } = 500;
    public IntRange SpansPerTrace { get; set; } = new(3, 15);
    public int MaxDepth { get; set; } = 4;
    public int FanOut { get; set; } = 3;
    /// <summary>Fraction of traces containing an error span.</summary>
    public double ErrorRate { get; set; } = 0.05;
    public int AttributeCount { get; set; } = 4;
    public int AttributeCardinality { get; set; } = 50;
    public IntRange EventsPerSpan { get; set; } = new(0, 2);
    public IntRange LinksPerSpan { get; set; } = new(0, 1);

    /// <summary>
    /// Whether a re-delivered span batch collapses into one row, which is a property of the schema under test: true on 2.x (spans are unique
    /// on (trace_id, span_id)), false on 3.0.0 (no unique key, decision 7 of the schema-simplification plan). Not read from a profile: the
    /// runner sets it from <c>schema/apply-schema.sh</c>'s target version (<see cref="LedgerSemantics"/>).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RedeliveryCollapses { get; set; }
}

public sealed class LogLoad
{
    public double RecordsPerSecond { get; set; } = 500;
    /// <summary>Weights by OTLP severity name: TRACE, DEBUG, INFO, WARN, ERROR, FATAL.</summary>
    public Dictionary<string, double> SeverityWeights { get; set; } = new()
    {
        ["TRACE"] = 1, ["DEBUG"] = 10, ["INFO"] = 70, ["WARN"] = 12, ["ERROR"] = 6, ["FATAL"] = 1
    };
    public IntRange BodyLength { get; set; } = new(40, 400);
    public int AttributeCount { get; set; } = 4;
    public int AttributeCardinality { get; set; } = 50;
    /// <summary>Fraction of records carrying a trace/span id.</summary>
    public double TraceCorrelatedFraction { get; set; } = 0.3;
}

public sealed class MetricLoad
{
    /// <summary>Target data points per second, across all five data-point tables. 0 disables metrics.</summary>
    public double DataPointsPerSecond { get; set; } = 500;
    public int MetricNamesPerService { get; set; } = 10;
    /// <summary>Series (label sets) per metric.</summary>
    public int SeriesPerMetric { get; set; } = 5;
    /// <summary>Weights by type: Gauge, Sum, Histogram, ExponentialHistogram, Summary.</summary>
    public Dictionary<string, double> TypeMix { get; set; } = new()
    {
        ["Gauge"] = 3, ["Sum"] = 3, ["Histogram"] = 3, ["ExponentialHistogram"] = 1, ["Summary"] = 1
    };
    /// <summary>Fraction of points (Summary excluded) carrying an exemplar.</summary>
    public double ExemplarFraction { get; set; } = 0.1;
    /// <summary>Fraction of metrics using delta rather than cumulative temporality.</summary>
    public double DeltaFraction { get; set; } = 0.3;
    /// <summary>Sets a point's start time (delta: time minus this interval).</summary>
    public int ExportIntervalSeconds { get; set; } = 10;
}

public sealed class TransportLoad
{
    /// <summary>Records per Export call: log records, spans, or metric data points.</summary>
    public int RecordsPerExport { get; set; } = 100;
    /// <summary>Concurrent gRPC channels (each its own connection pool).</summary>
    public int Channels { get; set; } = 2;
    /// <summary>Above 1, lets a channel open extra HTTP/2 connections when its streams are exhausted.</summary>
    public int ConnectionsPerChannel { get; set; } = 1;
    /// <summary>
    /// Cap on concurrent unfinished exports per signal. Past it a scheduled export is NOT sent and is
    /// counted as not-sent, which marks the load tool as the bottleneck rather than the server.
    /// </summary>
    public int MaxInFlightExports { get; set; } = 256;
    public int ExportTimeoutSeconds { get; set; } = 30;
}

public sealed class TimeShaping
{
    /// <summary>Fraction of records stamped a few minutes in the past, exercising the rollup re-roll.</summary>
    public double LateArrivalFraction { get; set; } = 0;
    public IntRange LateArrivalMinutes { get; set; } = new(2, 8);
    /// <summary>Fraction of traces whose root span is never sent (orphan detection).</summary>
    public double OrphanFraction { get; set; } = 0;
    /// <summary>Fraction of records stamped past the retention window (retention sweep, Decision 11).</summary>
    public double BackdatedFraction { get; set; } = 0;
    /// <summary>Age of backdated records. Must exceed every retention window (default settings: 90/90/180 days).</summary>
    public IntRange BackdatedAgeDays { get; set; } = new(200, 260);
    /// <summary>Fraction of exports sent a second time, byte for byte.</summary>
    public double RedeliveryFraction { get; set; } = 0;
}
