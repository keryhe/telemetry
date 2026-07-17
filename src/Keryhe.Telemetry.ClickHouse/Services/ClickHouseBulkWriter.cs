using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data;
using static Keryhe.Telemetry.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// ClickHouse implementation of <see cref="ITelemetryBulkWriter"/>. Unlike the relational
/// providers there is no <c>RETURNING</c>/<c>OUTPUT</c> to recover generated ids and no
/// <c>ON CONFLICT</c>/<c>MERGE</c> for dedup: surrogate ids are computed in-process
/// (<see cref="ClickHouseIds"/>), dedup is delegated to the tables' <c>ReplacingMergeTree</c>
/// engine plus the shared <see cref="ResourceScopeCache"/>, and every table is written with
/// ClickHouse's native async bulk-copy path (<see cref="ClickHouseBulkCopy"/>). The
/// channel-draining loop and the normalization/hashing helpers live in
/// <c>Keryhe.Telemetry.Data</c>.
/// </summary>
public sealed class ClickHouseBulkWriter(
    IConfiguration configuration,
    ResourceScopeCache cache,
    ILogger<ClickHouseBulkWriter> logger) : ITelemetryBulkWriter
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        var rows = records.Select(r => new object?[]
        {
            RowId.Next(),
            resourceIds[HashResource(NormalizeResource(r.Resource))],
            scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))],
            r.TimeUnixNano ?? 0L,
            r.ObservedTimeUnixNano,
            r.SeverityNumber,
            r.SeverityText,
            r.BodyType?.ToString(),
            r.BodyValue,
            r.DroppedAttributesCount,
            r.Flags,
            r.TraceIdHex,
            r.SpanIdHex,
            SerializeJsonOrNull(r.Attributes),
            r.EventName
        });

        await BulkInsertAsync(conn, "log_records", LogColumns, rows, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    private static readonly string[] LogColumns =
    [
        "id", "resource_id", "scope_id", "time_unix_nano", "observed_time_unix_nano",
        "severity_number", "severity_text", "body_type", "body_value",
        "dropped_attributes_count", "flags", "trace_id", "span_id", "attributes_json", "event_name"
    ];

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public async Task FlushTracesAsync(List<TraceModel> traces, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        var spans = traces
            .SelectMany(t => t.Spans.Select(s => (
                Span:     s,
                Resource: s.Resource ?? t.Resource,
                Scope:    s.InstrumentationScope ?? t.InstrumentationScope)))
            .ToList();

        if (spans.Count == 0) return;

        var resourceIds = await ResolveResourcesAsync(conn, spans.Select(s => s.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, spans.Select(s => s.Scope), ct);

        // Span db-id is deterministic from (trace_id, span_id); no RETURNING needed. Dedup
        // is handled by the spans table's ReplacingMergeTree ORDER BY (trace_id, span_id).
        var spanRows = spans.Select(x =>
        {
            var (span, resource, scope) = x;
            return new object?[]
            {
                ClickHouseIds.FromKey($"{span.TraceIdHex}__{span.SpanIdHex}"),
                span.TraceIdHex,
                span.SpanIdHex,
                span.ParentSpanIdHex,
                resourceIds[HashResource(NormalizeResource(resource))],
                scopeIds[HashScope(NormalizeScope(scope))],
                span.Name,
                span.Kind.ToString(),
                span.StartTimeUnixNano,
                span.EndTimeUnixNano,
                span.DroppedAttributesCount,
                span.DroppedEventsCount,
                span.DroppedLinksCount,
                span.TraceState,
                span.StatusCode.ToString(),
                span.StatusMessage,
                SerializeJsonOrNull(span.Attributes),
                span.Flags
            };
        });

        await BulkInsertAsync(conn, "spans", SpanColumns, spanRows, ct);

        var events = new List<object?[]>();
        var links  = new List<object?[]>();
        foreach (var (span, _, _) in spans)
        {
            var spanDbId = ClickHouseIds.FromKey($"{span.TraceIdHex}__{span.SpanIdHex}");
            foreach (var e in span.Events)
                events.Add([RowId.Next(), spanDbId, e.Name, e.TimeUnixNano, e.DroppedAttributesCount, SerializeJsonOrNull(e.Attributes)]);
            foreach (var l in span.Links)
                links.Add([RowId.Next(), spanDbId, l.LinkedTraceIdHex, l.LinkedSpanIdHex, l.TraceState, l.DroppedAttributesCount, SerializeJsonOrNull(l.Attributes), l.Flags]);
        }

        if (events.Count > 0) await BulkInsertAsync(conn, "span_events", SpanEventColumns, events, ct);
        if (links.Count  > 0) await BulkInsertAsync(conn, "span_links",  SpanLinkColumns,  links,  ct);

        logger.LogDebug("Flushed {SpanCount} spans across {TraceCount} traces", spans.Count, traces.Count);
    }

    private static readonly string[] SpanColumns =
    [
        "id", "trace_id", "span_id", "parent_span_id", "resource_id", "scope_id",
        "name", "kind", "start_time_unix_nano", "end_time_unix_nano",
        "dropped_attributes_count", "dropped_events_count", "dropped_links_count",
        "trace_state", "status_code", "status_message", "attributes_json", "flags"
    ];

    private static readonly string[] SpanEventColumns =
        ["id", "span_id", "name", "time_unix_nano", "dropped_attributes_count", "attributes_json"];

    private static readonly string[] SpanLinkColumns =
        ["id", "span_id", "linked_trace_id", "linked_span_id", "trace_state", "dropped_attributes_count", "attributes_json", "flags"];

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
    {
        await using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, metrics.Select(m => m.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, metrics.Select(m => m.InstrumentationScope), ct);

        // Each metric gets a fresh in-process id; data points reference it within this flush.
        var metricIds = new long[metrics.Count];
        var metricRows = new List<object?[]>(metrics.Count);
        for (var i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            metricIds[i] = RowId.Next();
            metricRows.Add(
            [
                metricIds[i],
                resourceIds[HashResource(NormalizeResource(m.Resource))],
                scopeIds[HashScope(NormalizeScope(m.InstrumentationScope))],
                m.Name,
                m.Description,
                m.Unit,
                m.Type.ToString()
            ]);
        }

        await BulkInsertAsync(conn, "metrics", MetricColumns, metricRows, ct);

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric   = metrics[i];
            var metricId = metricIds[i];
            switch (metric.Type)
            {
                case MetricType.GAUGE when metric.GaugeDataPoints?.Count > 0:
                    await BulkInsertAsync(conn, "gauge_data_points", GaugeColumns,
                        metric.GaugeDataPoints.Select(d => new object?[]
                        {
                            metricId, d.StartTimeUnixNano, d.TimeUnixNano, d.ValueDouble, d.ValueInt, d.Flags,
                            SerializeJsonOrNull(d.Attributes)
                        }), ct);
                    break;
                case MetricType.SUM when metric.SumDataPoints?.Count > 0:
                    await BulkInsertAsync(conn, "sum_data_points", SumColumns,
                        metric.SumDataPoints.Select(d => new object?[]
                        {
                            metricId, d.StartTimeUnixNano, d.TimeUnixNano, d.ValueDouble, d.ValueInt,
                            d.AggregationTemporality.ToString(), (byte)(d.IsMonotonic ? 1 : 0), d.Flags,
                            SerializeJsonOrNull(d.Attributes)
                        }), ct);
                    break;
                case MetricType.HISTOGRAM when metric.HistogramDataPoints?.Count > 0:
                    await BulkInsertAsync(conn, "histogram_data_points", HistogramColumns,
                        metric.HistogramDataPoints.Select(d => new object?[]
                        {
                            metricId, d.StartTimeUnixNano, d.TimeUnixNano, d.Count, d.Sum,
                            SerializeJsonOrNull(d.BucketCounts), SerializeJsonOrNull(d.ExplicitBounds),
                            d.AggregationTemporality.ToString(), d.Flags, d.Min, d.Max,
                            SerializeJsonOrNull(d.Attributes)
                        }), ct);
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM when metric.ExponentialHistogramDataPoints?.Count > 0:
                    await BulkInsertAsync(conn, "exponential_histogram_data_points", ExpHistogramColumns,
                        metric.ExponentialHistogramDataPoints.Select(d => new object?[]
                        {
                            metricId, d.StartTimeUnixNano, d.TimeUnixNano, d.Count, d.Sum, d.Scale, d.ZeroCount,
                            d.PositiveOffset, SerializeJsonOrNull(d.PositiveBucketCounts),
                            d.NegativeOffset, SerializeJsonOrNull(d.NegativeBucketCounts),
                            d.AggregationTemporality.ToString(), d.Flags, d.Min, d.Max,
                            SerializeJsonOrNull(d.Attributes)
                        }), ct);
                    break;
                case MetricType.SUMMARY when metric.SummaryDataPoints?.Count > 0:
                    await BulkInsertAsync(conn, "summary_data_points", SummaryColumns,
                        metric.SummaryDataPoints.Select(d => new object?[]
                        {
                            metricId, d.StartTimeUnixNano, d.TimeUnixNano, d.Count, d.Sum,
                            SerializeJsonOrNull(d.QuantileValues), d.Flags,
                            SerializeJsonOrNull(d.Attributes)
                        }), ct);
                    break;
            }
        }

        logger.LogDebug("Flushed {Count} metrics", metrics.Count);
    }

    private static readonly string[] MetricColumns =
        ["id", "resource_id", "scope_id", "name", "description", "unit", "type"];

    private static readonly string[] GaugeColumns =
        ["metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int", "flags", "attributes_json"];

    private static readonly string[] SumColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int",
        "aggregation_temporality", "is_monotonic", "flags", "attributes_json"
    ];

    private static readonly string[] HistogramColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
        "bucket_counts", "explicit_bounds", "aggregation_temporality", "flags",
        "min_value", "max_value", "attributes_json"
    ];

    private static readonly string[] ExpHistogramColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
        "scale", "zero_count", "positive_offset", "positive_bucket_counts",
        "negative_offset", "negative_bucket_counts", "aggregation_temporality", "flags",
        "min_value", "max_value", "attributes_json"
    ];

    private static readonly string[] SummaryColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
        "quantile_values", "flags", "attributes_json"
    ];

    // =========================================================================
    // RESOURCE / SCOPE RESOLUTION
    // =========================================================================
    // Same shape as the relational providers, but ids are derived deterministically from the
    // dedup hash instead of returned by the database. The ResourceScopeCache short-circuits
    // repeat inserts; residual duplicates collapse via ReplacingMergeTree at merge time.

    private async Task<Dictionary<string, long>> ResolveResourcesAsync(
        ClickHouseConnection conn, IEnumerable<ResourceModel?> resources, CancellationToken ct)
    {
        var result  = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new Dictionary<string, ResourceModel>(StringComparer.Ordinal);

        foreach (var r in resources)
        {
            var model = NormalizeResource(r);
            var hash  = HashResource(model);
            if (result.ContainsKey(hash)) continue;
            if (cache.TryGetResource(hash, out var id))
                result[hash] = id;
            else
                pending.TryAdd(hash, model);
        }

        if (pending.Count > 0)
        {
            var rows = new List<object?[]>(pending.Count);
            foreach (var (hash, model) in pending)
            {
                var id = ClickHouseIds.FromHash(hash);
                rows.Add([id, model.TenantId, hash, model.SchemaUrl, SerializeDeterministicJson(model.Attributes)]);
                cache.SetResource(hash, id);
                result[hash] = id;
            }
            await BulkInsertAsync(conn, "resources", ResourceColumns, rows, ct);
        }

        return result;
    }

    private async Task<Dictionary<string, long>> ResolveScopesAsync(
        ClickHouseConnection conn, IEnumerable<InstrumentationScopeModel?> scopes, CancellationToken ct)
    {
        var result  = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new Dictionary<string, InstrumentationScopeModel>(StringComparer.Ordinal);

        foreach (var s in scopes)
        {
            var model = NormalizeScope(s);
            var hash  = HashScope(model);
            if (result.ContainsKey(hash)) continue;
            if (cache.TryGetScope(hash, out var id))
                result[hash] = id;
            else
                pending.TryAdd(hash, model);
        }

        if (pending.Count > 0)
        {
            var rows = new List<object?[]>(pending.Count);
            foreach (var (hash, model) in pending)
            {
                var id = ClickHouseIds.FromHash(hash);
                rows.Add([id, model.Name, model.Version, model.SchemaUrl, hash, SerializeDeterministicJson(model.Attributes)]);
                cache.SetScope(hash, id);
                result[hash] = id;
            }
            await BulkInsertAsync(conn, "instrumentation_scopes", ScopeColumns, rows, ct);
        }

        return result;
    }

    private static readonly string[] ResourceColumns =
        ["id", "tenant_id", "resource_hash", "schema_url", "attributes_json"];

    private static readonly string[] ScopeColumns =
        ["id", "name", "version", "schema_url", "scope_hash", "attributes_json"];

    // =========================================================================
    // BULK COPY
    // =========================================================================

    private static async Task BulkInsertAsync(
        ClickHouseConnection conn, string table, string[] columns, IEnumerable<object?[]> rows, CancellationToken ct)
    {
        using var bulk = new ClickHouseBulkCopy(conn)
        {
            DestinationTableName = table,
            ColumnNames = columns,
            BatchSize = 100_000
        };
        await bulk.InitAsync();
        await bulk.WriteToServerAsync(rows.Select(r => (object[])r), ct);
    }
}
