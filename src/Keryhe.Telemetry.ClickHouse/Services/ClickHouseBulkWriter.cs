using System.Collections.Concurrent;
using System.Data;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="ITelemetryBulkWriter"/> for the row model in
/// <c>plans/clickhouse-row-model.md</c>. Every row carries its own resource and scope (attribute maps, schema URL,
/// scope name and version), so there are no reference tables, no surrogate ids and no resolution step; spans, log
/// records and the five point tables are plain <c>MergeTree</c> appends written with ClickHouse's native async
/// bulk-copy path (<see cref="ClickHouseBulkCopy"/>). Attribute encoding is <see cref="ClickHouseAttributes"/>, id
/// conversion <see cref="ClickHouseIds"/>.
///
/// <b>Deduplication tokens.</b> Each <c>Flush*Async(batch, token, ct)</c> sets <c>insert_deduplication_token</c> on its
/// INSERTs to the token plus the table name (one batch writes several tables), so a retry of the identical batch
/// with the same token is stored once. The token belongs to ONE INSERT statement: a second statement under the same
/// token is silently dropped, so <see cref="ClickHouseBulkCopy.BatchSize"/> is always set to at least the batch's row
/// count, and a caller that wants a bigger set stored splits it into separately tokened batches. A re-batched set
/// needs a new token. The <see cref="ITelemetryBulkWriter"/> methods mint a fresh token per call.
///
/// <b>Large batches are split.</b> A raw-table insert of at least <see cref="ClickHouseIngestionOptions.ParallelFlushMinRows"/>
/// rows is cut at fixed row boundaries into pieces (each at least <see cref="ClickHouseIngestionOptions.InsertPieceRows"/> rows,
/// at most <see cref="ClickHouseIngestionOptions.MaxParallelInserts"/> of them) that insert concurrently, piece <c>i</c> under
/// the token <c>{token}:{table}:{i}</c>; the same batch always cuts at the same rows, so a retry re-sends the same pieces and
/// the ones that landed are dropped. The piece, not the table, is then the unit of atomicity. Spans and logs are also built into
/// rows on several threads at that size. Smaller batches are one insert under <c>{token}:{table}</c>, as before.
///
/// DELIBERATE EXCEPTION to the relational providers: a flush is NOT atomic. Each table's insert is its own atomic
/// operation and ClickHouse has no multi-statement transaction, so a flush that fails partway leaves some tables with
/// this batch's rows and others without. Retrying the batch with the same token is safe: the tables that landed
/// drop the repeat and the missing ones are stored.
///
/// <c>ClickHouseBulkCopy.InitAsync()</c> is a real round trip (it fetches column types to serialize RowBinary) and
/// the library exposes no way to seed that without it, so <see cref="_tableBulkCopies"/> keeps one long-lived
/// connection + initialized bulk copy per destination table for the life of this singleton. Concurrent flushes that
/// target the SAME table serialize through that table's lock; different tables run fully concurrently. A write that
/// throws tears down and forgets its table's entry so the next call rebuilds it (self-healing against a dropped
/// connection or a restarted server).
/// </summary>
public sealed partial class ClickHouseBulkWriter(
    IConfiguration configuration,
    ILogger<ClickHouseBulkWriter> logger,
    IOptions<ClickHouseIngestionOptions>? clickHouseOptions = null,
    IOptions<TelemetryIngestionOptions>? sharedOptions = null,
    IngestionMetrics? metrics = null,
    TimeProvider? time = null) : ITelemetryBulkWriter, IClickHouseTokenWriter, IAsyncDisposable
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;
    private readonly ClickHouseIngestionOptions _chOptions = clickHouseOptions?.Value ?? new ClickHouseIngestionOptions();
    private readonly TelemetryIngestionOptions _shared = sharedOptions?.Value ?? new TelemetryIngestionOptions();
    private readonly IngestionMetrics? _metrics = metrics;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CatalogTracker _seriesTracker = new(time ?? TimeProvider.System, clickHouseOptions?.Value ?? new ClickHouseIngestionOptions());
    private readonly CatalogTracker _metricTracker = new(time ?? TimeProvider.System, clickHouseOptions?.Value ?? new ClickHouseIngestionOptions());
    private readonly ConcurrentDictionary<string, TableBulkCopy> _tableBulkCopies = new();

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
        => FlushLogsAsync(records, NewToken(), ct);

    public async Task FlushLogsAsync(List<LogRecordModel> records, string token, CancellationToken ct = default)
    {
        if (records.Count == 0) return;

        var context = new FlushContext();
        var nowNano = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var rows = BuildRows(records, r => LogRow(r, context, nowNano));

        await BulkInsertAsync("log_records", LogColumns, rows, token, ct);
        await InsertDerivedAsync("log_rollup_minute", LogRollupColumns, LogRollupRows(records, context, nowNano), token, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    private static object?[] LogRow(LogRecordModel r, FlushContext context, long nowNano)
    {
        var resource = context.Resource(r.Resource);
        var scope = context.Scope(r.InstrumentationScope);
        var (ts, observed) = LogTimes(r, nowNano);

        return
        [
            (ulong)resource.TenantId, resource.ServiceName, ToDateTime(ts), ToDateTime(observed),
            ClickHouseIds.TraceIdToGuid(r.TraceIdHex), ClickHouseIds.SpanIdToUInt64(r.SpanIdHex),
            (uint)r.Flags, (byte)Math.Clamp(r.SeverityNumber ?? 0, 0, 255), r.SeverityText ?? "", r.EventName ?? "",
            r.BodyValue ?? "", ClickHouseAttributes.ToMap(r.Attributes), (uint)r.DroppedAttributesCount,
            resource.Attributes, resource.SchemaUrl, scope.Name, scope.Version, scope.Attributes, scope.SchemaUrl
        ];
    }

    private static readonly string[] LogColumns =
    [
        "tenant_id", "service_name", "timestamp", "observed_timestamp", "trace_id", "span_id", "flags",
        "severity_number", "severity_text", "event_name", "body", "attributes", "dropped_attributes_count",
        "resource_attributes", "resource_schema_url", "scope_name", "scope_version", "scope_attributes", "scope_schema_url"
    ];

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public Task FlushTracesAsync(List<SpanModel> spans, CancellationToken ct = default)
        => FlushTracesAsync(spans, NewToken(), ct);

    public async Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default)
    {
        if (spans.Count == 0) return;

        var context = new FlushContext();
        var rows = BuildRows(spans, span => SpanRow(span, context));

        await BulkInsertAsync("spans", SpanColumns, rows, token, ct);
        await InsertDerivedAsync("trace_index", TraceIndexColumns, TraceIndexRows(spans, context), token, ct);
        await InsertDerivedAsync("request_rollup_minute", RequestRollupColumns, RequestRollupRows(spans, context), token, ct);
        logger.LogDebug("Flushed {SpanCount} spans", spans.Count);
    }

    private static object?[] SpanRow(SpanModel span, FlushContext context)
    {
        var resource = context.Resource(span.Resource);
        var scope = context.Scope(span.InstrumentationScope);

        var events = span.Events;
        var eventTimes = new DateTime[events.Count];
        var eventNames = new string[events.Count];
        var eventAttributes = new Dictionary<string, string>[events.Count];
        var eventDropped = new uint[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            eventTimes[i] = ToDateTime(events[i].TimeUnixNano);
            eventNames[i] = events[i].Name ?? "";
            eventAttributes[i] = ClickHouseAttributes.ToMap(events[i].Attributes);
            eventDropped[i] = (uint)events[i].DroppedAttributesCount;
        }

        var links = span.Links;
        var linkTraceIds = new Guid[links.Count];
        var linkSpanIds = new ulong[links.Count];
        var linkStates = new string[links.Count];
        var linkFlags = new uint[links.Count];
        var linkAttributes = new Dictionary<string, string>[links.Count];
        var linkDropped = new uint[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            linkTraceIds[i] = ClickHouseIds.TraceIdToGuid(links[i].LinkedTraceIdHex);
            linkSpanIds[i] = ClickHouseIds.SpanIdToUInt64(links[i].LinkedSpanIdHex);
            linkStates[i] = links[i].TraceState ?? "";
            linkFlags[i] = (uint)links[i].Flags;
            linkAttributes[i] = ClickHouseAttributes.ToMap(links[i].Attributes);
            linkDropped[i] = (uint)links[i].DroppedAttributesCount;
        }

        // duration comes from the original nanoseconds: start_time is stored at 100 ns resolution.
        var duration = (ulong)Math.Max(0L, span.EndTimeUnixNano - span.StartTimeUnixNano);

        return
        [
            (ulong)resource.TenantId, resource.ServiceName, span.Name ?? "", ToDateTime(span.StartTimeUnixNano), duration,
            ClickHouseIds.TraceIdToGuid(span.TraceIdHex), ClickHouseIds.SpanIdToUInt64(span.SpanIdHex),
            ClickHouseIds.SpanIdToUInt64(span.ParentSpanIdHex), span.TraceState ?? "", (uint)span.Flags,
            span.Kind.ToString(), span.StatusCode.ToString(), span.StatusMessage ?? "",
            ClickHouseAttributes.ToMap(span.Attributes), (uint)span.DroppedAttributesCount,
            eventTimes, eventNames, eventAttributes, eventDropped, (uint)span.DroppedEventsCount,
            linkTraceIds, linkSpanIds, linkStates, linkFlags, linkAttributes, linkDropped, (uint)span.DroppedLinksCount,
            resource.Attributes, resource.SchemaUrl, scope.Name, scope.Version, scope.Attributes, scope.SchemaUrl
        ];
    }

    private static readonly string[] SpanColumns =
    [
        "tenant_id", "service_name", "span_name", "start_time", "duration_ns", "trace_id", "span_id", "parent_span_id",
        "trace_state", "flags", "kind", "status_code", "status_message", "attributes", "dropped_attributes_count",
        "events.time", "events.name", "events.attributes", "events.dropped_attributes_count", "dropped_events_count",
        "links.trace_id", "links.span_id", "links.trace_state", "links.flags", "links.attributes",
        "links.dropped_attributes_count", "dropped_links_count",
        "resource_attributes", "resource_schema_url", "scope_name", "scope_version", "scope_attributes", "scope_schema_url"
    ];

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
        => FlushMetricsAsync(metrics, NewToken(), ct);

    public async Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default)
    {
        if (metrics.Count == 0) return;

        // Points are grouped by destination table across the WHOLE batch: each bulk insert is its own part, so a
        // per-metric insert would turn a 2,000-metric flush into up to 2,000 tiny parts (TOO_MANY_PARTS).
        var context = new FlushContext();
        var built = BuildMetricRows(metrics, context);
        var gauge = built.Gauge;
        var sum = built.Sum;
        var histogram = built.Histogram;
        var expHistogram = built.ExpHistogram;
        var summary = built.Summary;
        var derived = built.Derived;
        var parallelTables = built.Points >= _chOptions.ParallelFlushMinRows && _chOptions.MaxParallelInserts > 1;

        // The five points tables are separate tables with separate tokens, so a large batch writes them together; the
        // catalog and series rows below still wait for all of them. A small batch writes them one after another, as before.
        var writes = new List<Task>(5);
        async Task Write(string table, string[] columns, List<object?[]> rows)
        {
            if (rows.Count == 0) return;
            if (parallelTables) writes.Add(BulkInsertAsync(table, columns, rows, token, ct));
            else await BulkInsertAsync(table, columns, rows, token, ct);
        }
        await Write("gauge_points", GaugeColumns, gauge);
        await Write("sum_points", SumColumns, sum);
        await Write("histogram_points", HistogramColumns, histogram);
        await Write("exp_histogram_points", ExpHistogramColumns, expHistogram);
        await Write("summary_points", SummaryColumns, summary);
        await Task.WhenAll(writes);

        await derived.WriteAsync(this, token, ct);
        logger.LogDebug("Flushed {Count} metrics", metrics.Count);
    }

    private static readonly string[] PointCommonColumns =
    [
        "tenant_id", "service_name", "metric_name", "series_id", "time", "start_time", "metric_unit", "metric_description",
        "flags", "attributes", "resource_attributes", "resource_schema_url", "scope_name", "scope_version",
        "scope_attributes", "scope_schema_url"
    ];

    private static readonly string[] ExemplarColumns =
        ["exemplars.time", "exemplars.value", "exemplars.trace_id", "exemplars.span_id", "exemplars.filtered_attributes"];

    private static readonly string[] GaugeColumns = [.. PointCommonColumns, "value", .. ExemplarColumns];

    private static readonly string[] SumColumns =
        [.. PointCommonColumns, "value", "temporality", "is_monotonic", .. ExemplarColumns];

    private static readonly string[] HistogramColumns =
        [.. PointCommonColumns, "count", "sum", "min", "max", "bucket_counts", "explicit_bounds", "temporality", .. ExemplarColumns];

    private static readonly string[] ExpHistogramColumns =
    [
        .. PointCommonColumns, "count", "sum", "min", "max", "scale", "zero_count", "zero_threshold",
        "positive_offset", "positive_bucket_counts", "negative_offset", "negative_bucket_counts", "temporality", .. ExemplarColumns
    ];

    private static readonly string[] SummaryColumns =
        [.. PointCommonColumns, "count", "sum", "quantiles.quantile", "quantiles.value"];

    /// <summary>The five exemplar array columns, in <see cref="ExemplarColumns"/> order.</summary>
    private static object?[] Exemplars(List<ExemplarModel>? exemplars)
    {
        if (exemplars is not { Count: > 0 })
            return [Array.Empty<DateTime>(), Array.Empty<double>(), Array.Empty<Guid>(), Array.Empty<ulong>(), Array.Empty<Dictionary<string, string>>()];

        var times = new DateTime[exemplars.Count];
        var values = new double[exemplars.Count];
        var traceIds = new Guid[exemplars.Count];
        var spanIds = new ulong[exemplars.Count];
        var filtered = new Dictionary<string, string>[exemplars.Count];
        for (var i = 0; i < exemplars.Count; i++)
        {
            var e = exemplars[i];
            times[i] = ToDateTime(e.TimeUnixNano);
            values[i] = e.ValueDouble ?? e.ValueInt ?? 0d;
            traceIds[i] = ClickHouseIds.TraceIdToGuid(e.TraceIdHex);
            spanIds[i] = ClickHouseIds.SpanIdToUInt64(e.SpanIdHex);
            filtered[i] = ClickHouseAttributes.ToMap(e.FilteredAttributes);
        }
        return [times, values, traceIds, spanIds, filtered];
    }

    // =========================================================================
    // ROW HELPERS
    // =========================================================================

    private static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Unix nanoseconds as a <see cref="DateTime"/>. The driver carries .NET ticks, so the last two digits of a
    /// nanosecond timestamp are lost (README R9); a negative value is clamped to the epoch.
    /// </summary>
    private static DateTime ToDateTime(long unixNano)
        => DateTime.UnixEpoch.AddTicks(Math.Max(0L, unixNano) / 100);

    private static ulong ToUInt64(long value) => (ulong)Math.Max(0L, value);

    private static ulong[] ToUInt64Array(long[]? values)
    {
        if (values is not { Length: > 0 }) return [];
        var result = new ulong[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = ToUInt64(values[i]);
        return result;
    }

    private sealed record ResourceInfo(
        long TenantId, string ServiceName, string SchemaUrl, Dictionary<string, string> Attributes, string Key);

    private sealed record ScopeInfo(
        string Name, string Version, string SchemaUrl, Dictionary<string, string> Attributes, string Key);

    /// <summary>The rows of a metrics batch by destination table, and the catalog and series rows it makes due.</summary>
    private sealed class MetricRows
    {
        public readonly List<object?[]> Gauge = [], Sum = [], Histogram = [], ExpHistogram = [], Summary = [];
        public readonly MetricDerived Derived = new();

        /// <summary>Data points in the batch.</summary>
        public long Points;

        public void Add(MetricRows other)
        {
            Gauge.AddRange(other.Gauge); Sum.AddRange(other.Sum); Histogram.AddRange(other.Histogram);
            ExpHistogram.AddRange(other.ExpHistogram); Summary.AddRange(other.Summary);
            Derived.Merge(other.Derived);
        }
    }

    private static int PointCount(MetricModel m) => m.Type switch
    {
        MetricType.GAUGE => m.GaugeDataPoints?.Count ?? 0,
        MetricType.SUM => m.SumDataPoints?.Count ?? 0,
        MetricType.HISTOGRAM => m.HistogramDataPoints?.Count ?? 0,
        MetricType.EXPONENTIAL_HISTOGRAM => m.ExponentialHistogramDataPoints?.Count ?? 0,
        _ => m.SummaryDataPoints?.Count ?? 0
    };

    /// <summary>
    /// A large batch (by data points) is built on several threads, each over a contiguous run of metrics into its own
    /// <see cref="MetricRows"/>, merged in order; a small one is built in one pass as before.
    /// </summary>
    private MetricRows BuildMetricRows(List<MetricModel> metrics, FlushContext context)
    {
        var points = 0L;
        foreach (var m in metrics) points += PointCount(m);

        if (points < _chOptions.ParallelFlushMinRows || _chOptions.MaxParallelInserts <= 1 || metrics.Count < 2)
        {
            var rows = new MetricRows { Points = points };
            foreach (var metric in metrics) AddMetric(metric, context, rows);
            return rows;
        }

        var chunks = Math.Min(metrics.Count, _chOptions.MaxParallelInserts * 4);
        var parts = new MetricRows[chunks];
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = _chOptions.MaxParallelInserts }, chunk =>
        {
            var from = (int)((long)metrics.Count * chunk / chunks);
            var to = (int)((long)metrics.Count * (chunk + 1) / chunks);
            var rows = parts[chunk] = new MetricRows();
            for (var i = from; i < to; i++) AddMetric(metrics[i], context, rows);
        });

        var merged = parts[0];
        for (var i = 1; i < parts.Length; i++) merged.Add(parts[i]);
        merged.Points = points;
        return merged;
    }

    private static void AddMetric(MetricModel metric, FlushContext context, MetricRows rows)
    {
        var gauge = rows.Gauge;
        var sum = rows.Sum;
        var histogram = rows.Histogram;
        var expHistogram = rows.ExpHistogram;
        var summary = rows.Summary;
        var derived = rows.Derived;

        var resource = context.Resource(metric.Resource);
        var scope = context.Scope(metric.InstrumentationScope);
        var prefix = ClickHouseIds.SeriesPrefix(resource.TenantId, resource.Key, scope.Key, metric.Name, metric.Type);
        var name = metric.Name;
        var unit = metric.Unit ?? "";
        var description = metric.Description ?? "";

        var catalogKey = (ulong)ClickHouseIds.MetricId(resource.TenantId, resource.ServiceName, name, metric.Type);

        object?[] Common(Dictionary<string, object>? pointAttributes, long time, long? start, int flags)
        {
            var seriesId = ClickHouseIds.SeriesId(prefix, pointAttributes);
            var attributes = ClickHouseAttributes.ToMap(pointAttributes);
            derived.Note(catalogKey, seriesId, resource, scope, metric, unit, description, attributes, time);
            return
            [
                (ulong)resource.TenantId, resource.ServiceName, name, seriesId, ToDateTime(time), ToDateTime(start ?? 0),
                unit, description, (uint)flags, attributes,
                resource.Attributes, resource.SchemaUrl, scope.Name, scope.Version, scope.Attributes, scope.SchemaUrl
            ];
        }

        switch (metric.Type)
        {
            case MetricType.GAUGE when metric.GaugeDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    gauge.Add([.. Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags),
                        d.ValueDouble ?? d.ValueInt ?? 0d, .. Exemplars(d.Exemplars)]);
                break;
            case MetricType.SUM when metric.SumDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    sum.Add([.. Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags),
                        d.ValueDouble ?? d.ValueInt ?? 0d, d.AggregationTemporality.ToString(), d.IsMonotonic, .. Exemplars(d.Exemplars)]);
                break;
            case MetricType.HISTOGRAM when metric.HistogramDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    histogram.Add([.. Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags),
                        ToUInt64(d.Count), d.Sum, d.Min, d.Max, ToUInt64Array(d.BucketCounts), d.ExplicitBounds ?? [],
                        d.AggregationTemporality.ToString(), .. Exemplars(d.Exemplars)]);
                break;
            case MetricType.EXPONENTIAL_HISTOGRAM when metric.ExponentialHistogramDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    expHistogram.Add([.. Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags),
                        ToUInt64(d.Count), d.Sum, d.Min, d.Max, d.Scale, ToUInt64(d.ZeroCount), 0d,
                        d.PositiveOffset ?? 0, ToUInt64Array(d.PositiveBucketCounts), d.NegativeOffset ?? 0, ToUInt64Array(d.NegativeBucketCounts),
                        d.AggregationTemporality.ToString(), .. Exemplars(d.Exemplars)]);
                break;
            case MetricType.SUMMARY when metric.SummaryDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    summary.Add([.. Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags),
                        ToUInt64(d.Count), d.Sum,
                        d.QuantileValues?.Select(q => q.Quantile).ToArray() ?? [],
                        d.QuantileValues?.Select(q => q.Value).ToArray() ?? []]);
                break;
        }
    }

    /// <summary>
    /// Converts a resource's or scope's attribute map once per instance for the length of one flush: the gRPC services
    /// share one model instance per <c>ResourceSpans</c>/<c>ResourceLogs</c>/<c>ResourceMetrics</c> block, so every row
    /// of the block reuses one converted map (mirroring <see cref="ResourceModel.CachedHash"/>). The shared map is only
    /// ever read by the serializer.
    /// </summary>
    private sealed class FlushContext
    {
        // Concurrent: a large batch is built on several threads. Two threads that miss together both convert the same
        // instance and one result wins; the converted maps are only ever read afterwards.
        private readonly ConcurrentDictionary<ResourceModel, ResourceInfo> _resources = new(ReferenceEqualityComparer.Instance);
        private readonly ConcurrentDictionary<InstrumentationScopeModel, ScopeInfo> _scopes = new(ReferenceEqualityComparer.Instance);

        public ResourceInfo Resource(ResourceModel? resource)
        {
            if (resource is not null && _resources.TryGetValue(resource, out var cached)) return cached;

            var model = NormalizeResource(resource);
            var info = new ResourceInfo(
                model.TenantId,
                ExtractServiceName(model.Attributes) ?? "",
                model.SchemaUrl ?? "",
                ClickHouseAttributes.ToMap(model.Attributes),
                HashResource(model));
            return resource is not null ? _resources.GetOrAdd(resource, info) : info;
        }

        public ScopeInfo Scope(InstrumentationScopeModel? scope)
        {
            if (scope is not null && _scopes.TryGetValue(scope, out var cached)) return cached;

            var model = NormalizeScope(scope);
            var info = new ScopeInfo(
                model.Name ?? "",
                model.Version ?? "",
                model.SchemaUrl ?? "",
                ClickHouseAttributes.ToMap(model.Attributes),
                HashScope(model));
            return scope is not null ? _scopes.GetOrAdd(scope, info) : info;
        }
    }

    // =========================================================================
    // BULK COPY (cached per table -- see class doc comment)
    // =========================================================================

    private Task BulkInsertAsync(string table, string[] columns, List<object?[]> rows, string token, CancellationToken ct)
    {
        var tableBulkCopy = _tableBulkCopies.GetOrAdd(table, t => new TableBulkCopy(_connectionString, t, columns, _chOptions.MaxParallelInserts));

        // A small batch is one INSERT under "{token}:{table}", as it always was. A large one is cut into contiguous pieces,
        // each its own INSERT with its own token, sent concurrently: the same batch always cuts at the same rows, so a retry
        // with the same token stores each piece once, whichever landed before.
        var pieces = PieceCount(rows.Count);
        if (pieces == 1) return tableBulkCopy.WriteAsync(rows, $"{token}:{table}", ct);

        var writes = new Task[pieces];
        for (var i = 0; i < pieces; i++)
        {
            var from = (int)((long)rows.Count * i / pieces);
            var to = (int)((long)rows.Count * (i + 1) / pieces);
            writes[i] = tableBulkCopy.WriteAsync(rows.GetRange(from, to - from), $"{token}:{table}:{i}", ct);
        }
        return Task.WhenAll(writes);
    }

    private int PieceCount(int rowCount)
        => rowCount < _chOptions.ParallelFlushMinRows ? 1 : Math.Max(1, Math.Min(_chOptions.MaxParallelInserts, rowCount / _chOptions.InsertPieceRows));

    /// <summary>
    /// Builds one row per item, in order. A large batch is built on several threads (contiguous chunks into a pre-sized array);
    /// <paramref name="build"/> must be safe to call concurrently (<see cref="FlushContext"/> is).
    /// </summary>
    private List<object?[]> BuildRows<T>(List<T> items, Func<T, object?[]> build)
    {
        if (items.Count < _chOptions.ParallelFlushMinRows || _chOptions.MaxParallelInserts <= 1)
        {
            var rows = new List<object?[]>(items.Count);
            foreach (var item in items) rows.Add(build(item));
            return rows;
        }

        var built = new object?[items.Count][];
        var chunks = _chOptions.MaxParallelInserts * 4;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = _chOptions.MaxParallelInserts }, chunk =>
        {
            var from = (int)((long)items.Count * chunk / chunks);
            var to = (int)((long)items.Count * (chunk + 1) / chunks);
            for (var i = from; i < to; i++) built[i] = build(items[i]);
        });
        return new List<object?[]>(built);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tableBulkCopy in _tableBulkCopies.Values)
            await tableBulkCopy.DisposeAsync();
    }

    /// <summary>
    /// The persistent connections for a single destination table, reused across every flush for the life of the process:
    /// up to <c>maxSlots</c> of them (each one connection + an initialized <see cref="ClickHouseBulkCopy"/>), created on
    /// first use, so the pieces of a split batch can insert concurrently. See the class doc comment.
    /// </summary>
    private sealed class TableBulkCopy(string connectionString, string table, string[] columns, int maxSlots) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(maxSlots, maxSlots);
        private readonly ConcurrentQueue<Slot> _idle = new();

        public async Task WriteAsync(List<object?[]> rows, string deduplicationToken, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            var slot = _idle.TryDequeue(out var idle) ? idle : new Slot(connectionString, table, columns);
            try
            {
                await slot.WriteAsync(rows, deduplicationToken, ct);
            }
            finally
            {
                _idle.Enqueue(slot);
                _gate.Release();
            }
        }

        private int _disposed;

        // Idempotent: the container may dispose the shared writer once per interface it is registered under.
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            for (var i = 0; i < maxSlots; i++) await _gate.WaitAsync();
            while (_idle.TryDequeue(out var slot)) await slot.DisposeAsync();
        }
    }

    private sealed class Slot(string connectionString, string table, string[] columns) : IAsyncDisposable
    {
        private ClickHouseConnection? _connection;
        private ClickHouseBulkCopy? _bulkCopy;

        public async Task WriteAsync(List<object?[]> rows, string deduplicationToken, CancellationToken ct)
        {
            try
            {
                await EnsureReadyAsync(ct);
                // One INSERT statement per token: BatchSize covers the whole batch (see the writer's doc comment).
                _bulkCopy!.BatchSize = Math.Max(rows.Count, 1);
                _connection!.CustomSettings["insert_deduplication_token"] = deduplicationToken;
                await _bulkCopy.WriteToServerAsync(rows.Select(r => (object[])r), ct);
            }
            catch
            {
                // Self-healing: without this, a broken connection (network blip, server restart) would fail every
                // subsequent flush through this slot forever, since nothing else rebuilds it. The caller still owns
                // retrying the flush; this only guarantees that retry has a working connection.
                await TearDownAsync();
                throw;
            }
        }

        private async Task EnsureReadyAsync(CancellationToken ct)
        {
            if (_connection is { State: ConnectionState.Open } && _bulkCopy != null) return;

            await TearDownAsync();
            _connection = new ClickHouseConnection(connectionString);
            await _connection.OpenAsync(ct);
            _bulkCopy = new ClickHouseBulkCopy(_connection)
            {
                DestinationTableName = table,
                ColumnNames = columns,
                MaxDegreeOfParallelism = 1
            };
            await _bulkCopy.InitAsync();
        }

        private async Task TearDownAsync()
        {
            _bulkCopy?.Dispose();
            _bulkCopy = null;
            if (_connection != null) await _connection.DisposeAsync();
            _connection = null;
        }

        public ValueTask DisposeAsync() => new(TearDownAsync());
    }
}
