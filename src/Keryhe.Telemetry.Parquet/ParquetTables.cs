using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>Which retention window (the control plane's) a table follows.</summary>
public enum ParquetRetentionWindow { Traces, Logs, Metrics }

/// <summary>How compaction folds a column of a derived table when it merges rows of the same key (README D8).</summary>
public enum ParquetMerge { Key, Sum, Max, Min, Any }

/// <summary>
/// One column of a table. <see cref="Nested"/> columns (a <c>MAP</c> or a list of structs) cannot be written by the
/// DuckDB appender, so the writer stages them as JSON text and the <c>COPY</c> converts them.
/// </summary>
public sealed record ParquetColumn(string Name, string Type, bool Nested = false, ParquetMerge Merge = ParquetMerge.Key)
{
    /// <summary>The column's type in the staging table.</summary>
    internal string StagingType => Nested ? "VARCHAR" : Type;

    /// <summary>The expression that turns the staged column into the stored type.</summary>
    internal string SelectExpression => Nested
        ? $"CAST(json_transform(\"{Name}\", '\"{Type}\"') AS {Type}) AS \"{Name}\""
        : $"\"{Name}\"";
}

/// <summary>
/// A table's definition (README D7): one record drives the writer, the compactor, retention and reads. Adding a table is
/// one definition in <see cref="ParquetTables"/>.
/// </summary>
public sealed record ParquetTable(
    string Name,
    IReadOnlyList<ParquetColumn> Columns,
    string SortKey,
    string PartitionTimeColumn,
    ParquetRetentionWindow Retention,
    bool Derived = false,
    IReadOnlyList<string>? MergeKeyExpressions = null)
{
    /// <summary>
    /// Compaction pre-sums this table's rows (phase 4 decision 5): rows with the same key (the <see cref="ParquetMerge.Key"/>
    /// columns plus <see cref="MergeKeyExpressions"/>) become one row, the other columns folded as their merge says.
    /// </summary>
    public bool PreSummed => Columns.Any(c => c.Merge != ParquetMerge.Key);

    /// <summary>The first column of the sort key, the one a very large compaction is cut on.</summary>
    internal ParquetColumn FirstSortColumn
    {
        get
        {
            var name = SortKey.Split(',')[0].Trim();
            return Columns.First(c => c.Name == name);
        }
    }

    internal string StagingColumnList => string.Join(", ", Columns.Select(c => $"\"{c.Name}\" {c.StagingType}"));

    internal string SelectList => string.Join(", ", Columns.Select(c => c.SelectExpression));

    internal string Describe()
        => $"{Name}|{SortKey}|{PartitionTimeColumn}|{Retention}|{string.Join(",", Columns.Select(c => $"{c.Name}:{c.Type}"))}{(Derived ? "|derived" : "")}|{string.Join(",", Columns.Where(c => c.Merge != ParquetMerge.Key).Select(c => $"{c.Name}={c.Merge}"))}|{string.Join(";", MergeKeyExpressions ?? [])}";
}

public static class ParquetTables
{
    private const string Attributes = "MAP(VARCHAR, VARCHAR)";

    public const string Spans = "spans";

    private static readonly string EventType =
        $"STRUCT(time_unix_nano BIGINT, name VARCHAR, attributes {Attributes}, dropped_attributes_count UBIGINT)[]";

    private static readonly string LinkType =
        $"STRUCT(trace_hi UBIGINT, trace_lo UBIGINT, span_id UBIGINT, trace_state VARCHAR, flags UBIGINT, attributes {Attributes}, dropped_attributes_count UBIGINT)[]";

    /// <summary>
    /// Spans: one row carries its resource and scope (no reference tables), as in the ClickHouse row model. Sorted by
    /// service then start so a service-scoped list prunes; the partition time is the start.
    /// </summary>
    public static readonly ParquetTable SpanTable = new(
        Spans,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("span_name", "VARCHAR"),
            new("start_time_unix_nano", "BIGINT"),
            new("end_time_unix_nano", "BIGINT"),
            new("duration_ns", "BIGINT"),
            new("trace_hi", "UBIGINT"),
            new("trace_lo", "UBIGINT"),
            new("span_id", "UBIGINT"),
            new("parent_span_id", "UBIGINT"),
            new("trace_state", "VARCHAR"),
            new("flags", "UBIGINT"),
            new("kind", "VARCHAR"),
            new("status_code", "VARCHAR"),
            new("status_message", "VARCHAR"),
            new("attributes", Attributes, Nested: true),
            new("dropped_attributes_count", "UBIGINT"),
            new("events", EventType, Nested: true),
            new("dropped_events_count", "UBIGINT"),
            new("links", LinkType, Nested: true),
            new("dropped_links_count", "UBIGINT"),
            new("resource_attributes", Attributes, Nested: true),
            new("resource_schema_url", "VARCHAR"),
            new("scope_name", "VARCHAR"),
            new("scope_version", "VARCHAR"),
            new("scope_attributes", Attributes, Nested: true),
            new("scope_schema_url", "VARCHAR"),
        ],
        SortKey: "service_name, start_time_unix_nano",
        PartitionTimeColumn: "start_time_unix_nano",
        Retention: ParquetRetentionWindow.Traces);

    public const string TraceIndex = "trace_index";
    public const string RequestRollup = "request_rollup_minute";

    /// <summary>
    /// One row per (tenant, trace, service, hour of the span's start) in a flushed batch: the trace's earliest start and
    /// latest end there, a span count and an error flag. Sorted by trace id so a lookup by id prunes on the files'
    /// min/max statistics. Derived: written by the collector after the
    /// span files.
    /// </summary>
    public static readonly ParquetTable TraceIndexTable = new(
        TraceIndex,
        [
            new("tenant_id", "BIGINT"),
            new("trace_hi", "UBIGINT"),
            new("trace_lo", "UBIGINT"),
            new("service_name", "VARCHAR"),
            new("start_min", "BIGINT", Merge: ParquetMerge.Min),
            new("end_max", "BIGINT", Merge: ParquetMerge.Max),
            new("span_count", "BIGINT", Merge: ParquetMerge.Sum),
            new("has_error", "UTINYINT", Merge: ParquetMerge.Max),
        ],
        SortKey: "trace_hi, trace_lo",
        PartitionTimeColumn: "start_min",
        Retention: ParquetRetentionWindow.Traces,
        Derived: true,
        // One row per trace, service and hour of the span's start, as the collector writes them: a merge keeps the hours apart,
        // so an hour's rows mean the same before and after compaction.
        MergeKeyExpressions: ["(start_min // 3600000000000)"]);

    /// <summary>
    /// Inbound spans (SERVER, CONSUMER) per (tenant, service, minute, operation) with the shared duration bands (README D8:
    /// partial rows, reads <c>SUM</c> them). Derived.
    /// </summary>
    public static readonly ParquetTable RequestRollupTable = new(
        RequestRollup,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("bucket_start_unix_nano", "BIGINT"),
            new("operation", "VARCHAR"),
            new("request_count", "BIGINT", Merge: ParquetMerge.Sum),
            new("error_count", "BIGINT", Merge: ParquetMerge.Sum),
            new("sum_duration_nanos", "BIGINT", Merge: ParquetMerge.Sum),
            new("max_duration_nanos", "BIGINT", Merge: ParquetMerge.Max),
            .. Enumerable.Range(0, DurationBands.Count).Select(i => new ParquetColumn($"h{i:00}", "BIGINT", Merge: ParquetMerge.Sum)),
        ],
        SortKey: "service_name, bucket_start_unix_nano",
        PartitionTimeColumn: "bucket_start_unix_nano",
        Retention: ParquetRetentionWindow.Traces,
        Derived: true);

    // ------------------------------------------------------------------ logs

    public const string Logs = "log_records";
    public const string LogRollup = "log_rollup_minute";

    private static readonly ParquetColumn[] ResourceAndScope =
    [
        new("resource_attributes", Attributes, Nested: true),
        new("resource_schema_url", "VARCHAR"),
        new("scope_name", "VARCHAR"),
        new("scope_version", "VARCHAR"),
        new("scope_attributes", Attributes, Nested: true),
        new("scope_schema_url", "VARCHAR"),
    ];

    /// <summary>
    /// Log records, one row each carrying its resource and scope. The partition time is the record's timestamp (its observed
    /// time when it has none, else the write time), sorted by service then time so a service-scoped slice prunes.
    /// </summary>
    public static readonly ParquetTable LogTable = new(
        Logs,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("time_unix_nano", "BIGINT"),
            new("observed_time_unix_nano", "BIGINT"),
            new("trace_hi", "UBIGINT"),
            new("trace_lo", "UBIGINT"),
            new("span_id", "UBIGINT"),
            new("flags", "UBIGINT"),
            new("severity_number", "UTINYINT"),
            new("severity_text", "VARCHAR"),
            new("event_name", "VARCHAR"),
            new("body", "VARCHAR"),
            new("attributes", Attributes, Nested: true),
            new("dropped_attributes_count", "UBIGINT"),
            .. ResourceAndScope,
        ],
        SortKey: "service_name, time_unix_nano",
        PartitionTimeColumn: "time_unix_nano",
        Retention: ParquetRetentionWindow.Logs);

    /// <summary>Log records per (tenant, service, minute, severity); a missing severity is -1 (README D8). Derived.</summary>
    public static readonly ParquetTable LogRollupTable = new(
        LogRollup,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("bucket_start_unix_nano", "BIGINT"),
            new("severity_number", "SMALLINT"),
            new("record_count", "BIGINT", Merge: ParquetMerge.Sum),
        ],
        SortKey: "service_name, bucket_start_unix_nano",
        PartitionTimeColumn: "bucket_start_unix_nano",
        Retention: ParquetRetentionWindow.Logs,
        Derived: true);

    // ------------------------------------------------------------------ metric points

    public const string GaugePoints = "gauge_points";
    public const string SumPoints = "sum_points";
    public const string HistogramPoints = "histogram_points";
    public const string ExpHistogramPoints = "exp_histogram_points";
    public const string SummaryPoints = "summary_points";
    public const string MetricCatalog = "metric_catalog";
    public const string MetricSeries = "metric_series";

    private static readonly string ExemplarType =
        $"STRUCT(time_unix_nano BIGINT, value DOUBLE, trace_hi UBIGINT, trace_lo UBIGINT, span_id UBIGINT, filtered_attributes {Attributes})[]";

    private static readonly ParquetColumn Exemplars = new("exemplars", ExemplarType, Nested: true);

    private static ParquetColumn[] PointColumns() =>
    [
        new("tenant_id", "BIGINT"),
        new("service_name", "VARCHAR"),
        new("metric_name", "VARCHAR"),
        new("series_id", "UBIGINT"),
        new("time_unix_nano", "BIGINT"),
        new("start_time_unix_nano", "BIGINT"),
        new("metric_unit", "VARCHAR"),
        new("metric_description", "VARCHAR"),
        new("flags", "UBIGINT"),
        new("attributes", Attributes, Nested: true),
        .. ResourceAndScope,
    ];

    // Sorted by service, metric, series, time: a read names the metric and a window, and one series' points sit together.
    private const string PointSortKey = "service_name, metric_name, series_id, time_unix_nano";

    private static ParquetTable Points(string name, params ParquetColumn[] tail) => new(
        name, [.. PointColumns(), .. tail], PointSortKey, "time_unix_nano", ParquetRetentionWindow.Metrics);

    public static readonly ParquetTable GaugeTable = Points(GaugePoints, new("value", "DOUBLE"), Exemplars);

    public static readonly ParquetTable SumTable = Points(SumPoints,
        new("value", "DOUBLE"), new("temporality", "VARCHAR"), new("is_monotonic", "BOOLEAN"), Exemplars);

    public static readonly ParquetTable HistogramTable = Points(HistogramPoints,
        new("count", "UBIGINT"), new("sum", "DOUBLE"), new("min", "DOUBLE"), new("max", "DOUBLE"),
        new("bucket_counts", "UBIGINT[]", Nested: true), new("explicit_bounds", "DOUBLE[]", Nested: true),
        new("temporality", "VARCHAR"), Exemplars);

    public static readonly ParquetTable ExpHistogramTable = Points(ExpHistogramPoints,
        new("count", "UBIGINT"), new("sum", "DOUBLE"), new("min", "DOUBLE"), new("max", "DOUBLE"),
        new("scale", "INTEGER"), new("zero_count", "UBIGINT"), new("zero_threshold", "DOUBLE"),
        new("positive_offset", "INTEGER"), new("positive_bucket_counts", "UBIGINT[]", Nested: true),
        new("negative_offset", "INTEGER"), new("negative_bucket_counts", "UBIGINT[]", Nested: true),
        new("temporality", "VARCHAR"), Exemplars);

    public static readonly ParquetTable SummaryTable = Points(SummaryPoints,
        new("count", "UBIGINT"), new("sum", "DOUBLE"),
        new("quantiles", "STRUCT(quantile DOUBLE, value DOUBLE)[]", Nested: true));

    /// <summary>
    /// One row per (tenant, service, metric name, type) per write: the catalog (README D9). Partitioned by the last time it
    /// saw the metric, so retention drops it with the day; the catalog read folds every day (min first seen, max last seen).
    /// Derived.
    /// </summary>
    public static readonly ParquetTable MetricCatalogTable = new(
        MetricCatalog,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("metric_name", "VARCHAR"),
            new("metric_type", "VARCHAR"),
            new("unit", "VARCHAR", Merge: ParquetMerge.Any),
            new("description", "VARCHAR", Merge: ParquetMerge.Any),
            new("first_seen_unix_nano", "BIGINT", Merge: ParquetMerge.Min),
            new("last_seen_unix_nano", "BIGINT", Merge: ParquetMerge.Max),
        ],
        SortKey: "service_name, metric_name",
        PartitionTimeColumn: "last_seen_unix_nano",
        Retention: ParquetRetentionWindow.Metrics,
        Derived: true);

    /// <summary>One row per series per write: its labels and when it was seen (README D9). Derived.</summary>
    public static readonly ParquetTable MetricSeriesTable = new(
        MetricSeries,
        [
            new("tenant_id", "BIGINT"),
            new("service_name", "VARCHAR"),
            new("metric_name", "VARCHAR"),
            new("series_id", "UBIGINT"),
            new("attributes", Attributes, Nested: true, Merge: ParquetMerge.Any),
            new("resource_attributes", Attributes, Nested: true, Merge: ParquetMerge.Any),
            new("scope_name", "VARCHAR", Merge: ParquetMerge.Any),
            new("scope_version", "VARCHAR", Merge: ParquetMerge.Any),
            new("first_seen_unix_nano", "BIGINT", Merge: ParquetMerge.Min),
            new("last_seen_unix_nano", "BIGINT", Merge: ParquetMerge.Max),
        ],
        SortKey: "service_name, metric_name, series_id",
        PartitionTimeColumn: "last_seen_unix_nano",
        Retention: ParquetRetentionWindow.Metrics,
        Derived: true);

    public static IReadOnlyList<ParquetTable> All { get; } =
    [
        SpanTable, TraceIndexTable, RequestRollupTable,
        LogTable, LogRollupTable,
        GaugeTable, SumTable, HistogramTable, ExpHistogramTable, SummaryTable, MetricCatalogTable, MetricSeriesTable
    ];

    public static ParquetTable ByName(string name)
        => All.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"Unknown Parquet table '{name}'.", nameof(name));

    /// <summary>A hash of every table definition, stored in <c>_schema.json</c>: a root written under other definitions is refused.</summary>
    public static string DefinitionHash()
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", All.Select(t => t.Describe())))));
}
