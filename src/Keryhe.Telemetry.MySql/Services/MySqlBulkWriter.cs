using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data;
using static Keryhe.Telemetry.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="ITelemetryBulkWriter"/>. Owns only the
/// dialect-specific flush logic — batched multi-row <c>INSERT</c> for the high-volume
/// tables, <c>INSERT IGNORE</c> + a natural-key <c>SELECT</c> to resolve span ids, and
/// <c>INSERT ... ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)</c> upserts for
/// resource/scope dedup. The channel-draining loop and the normalization/hashing helpers
/// live in <c>Keryhe.Telemetry.Data</c>. Targets MySQL 8.0+.
/// </summary>
public sealed class MySqlBulkWriter(
    IConfiguration configuration,
    ResourceScopeCache cache,
    ILogger<MySqlBulkWriter> logger) : ITelemetryBulkWriter
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    // Chunk multi-row INSERTs so a single statement stays well under MySQL's
    // max_allowed_packet and placeholder limits.
    private const int ChunkSize = 500;

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        await BulkInsertLogsAsync(conn, records, resourceIds, scopeIds, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public async Task FlushTracesAsync(List<TraceModel> traces, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
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

        var insertedSpanIds = await BulkInsertSpansAsync(conn, spans, resourceIds, scopeIds, ct);

        var events = new List<(long SpanDbId, SpanEventModel Event)>();
        var links  = new List<(long SpanDbId, SpanLinkModel  Link)>();

        foreach (var (span, _, _) in spans)
        {
            if (!insertedSpanIds.TryGetValue((span.TraceIdHex, span.SpanIdHex), out var dbId))
                continue;
            foreach (var e in span.Events) events.Add((dbId, e));
            foreach (var l in span.Links)  links.Add((dbId, l));
        }

        if (events.Count > 0) await BulkInsertSpanEventsAsync(conn, events, ct);
        if (links.Count  > 0) await BulkInsertSpanLinksAsync(conn, links, ct);

        logger.LogDebug("Flushed {SpanCount} spans across {TraceCount} traces", spans.Count, traces.Count);
    }

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, metrics.Select(m => m.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, metrics.Select(m => m.InstrumentationScope), ct);

        var metricIds = await InsertMetricsAsync(conn, metrics, resourceIds, scopeIds, ct);

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric   = metrics[i];
            var metricId = metricIds[i];
            switch (metric.Type)
            {
                case MetricType.GAUGE when metric.GaugeDataPoints?.Count > 0:
                    await BulkInsertGaugeDataPointsAsync(conn, metricId, metric.GaugeDataPoints, ct);
                    break;
                case MetricType.SUM when metric.SumDataPoints?.Count > 0:
                    await BulkInsertSumDataPointsAsync(conn, metricId, metric.SumDataPoints, ct);
                    break;
                case MetricType.HISTOGRAM when metric.HistogramDataPoints?.Count > 0:
                    await BulkInsertHistogramDataPointsAsync(conn, metricId, metric.HistogramDataPoints, ct);
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM when metric.ExponentialHistogramDataPoints?.Count > 0:
                    await BulkInsertExpHistogramDataPointsAsync(conn, metricId, metric.ExponentialHistogramDataPoints, ct);
                    break;
                case MetricType.SUMMARY when metric.SummaryDataPoints?.Count > 0:
                    await BulkInsertSummaryDataPointsAsync(conn, metricId, metric.SummaryDataPoints, ct);
                    break;
            }
        }

        logger.LogDebug("Flushed {Count} metrics", metrics.Count);
    }

    // =========================================================================
    // RESOURCE / SCOPE RESOLUTION
    // =========================================================================

    private async Task<Dictionary<string, long>> ResolveResourcesAsync(
        MySqlConnection conn,
        IEnumerable<ResourceModel?> resources,
        CancellationToken ct)
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

        foreach (var (hash, model) in pending)
        {
            var id = await UpsertResourceAsync(conn, model, hash, ct);
            cache.SetResource(hash, id);
            result[hash] = id;
        }

        return result;
    }

    private async Task<Dictionary<string, long>> ResolveScopesAsync(
        MySqlConnection conn,
        IEnumerable<InstrumentationScopeModel?> scopes,
        CancellationToken ct)
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

        foreach (var (hash, model) in pending)
        {
            var id = await UpsertScopeAsync(conn, model, hash, ct);
            cache.SetScope(hash, id);
            result[hash] = id;
        }

        return result;
    }

    // INSERT ... ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id) is the MySQL upsert-and-return
    // idiom: on a fresh insert LAST_INSERT_ID() is the new auto-increment id, and on a duplicate
    // key the assignment sets LAST_INSERT_ID() to the existing row's id. Either way
    // cmd.LastInsertedId yields the surrogate id the read repos join on.
    private static async Task<long> UpsertResourceAsync(
        MySqlConnection conn, ResourceModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO resources (attributes_json, resource_hash, schema_url, tenant_id)
            VALUES (@attrJson, @hash, @schemaUrl, @tenantId)
            ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
            """;

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tenantId",  model.TenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    private static async Task<long> UpsertScopeAsync(
        MySqlConnection conn, InstrumentationScopeModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO instrumentation_scopes (name, version, schema_url, scope_hash, attributes_json)
            VALUES (@name, @version, @schemaUrl, @hash, @attrJson)
            ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
            """;

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name",      model.Name);
        cmd.Parameters.AddWithValue("@version",   (object?)model.Version   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    // =========================================================================
    // BULK INSERT: LOGS
    // =========================================================================

    private static async Task BulkInsertLogsAsync(
        MySqlConnection conn,
        List<LogRecordModel> records,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        var columns = new[]
        {
            "resource_id", "scope_id", "time_unix_nano", "observed_time_unix_nano",
            "severity_number", "severity_text", "body_type", "body_value",
            "dropped_attributes_count", "flags", "trace_id", "span_id", "attributes_json", "event_name"
        };

        var rows = new List<object?[]>(records.Count);
        foreach (var r in records)
        {
            rows.Add(new object?[]
            {
                resourceIds[HashResource(NormalizeResource(r.Resource))],
                scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))],
                r.TimeUnixNano ?? 0L,
                BoxOrNull(r.ObservedTimeUnixNano),
                BoxOrNull(r.SeverityNumber),
                (object?)r.SeverityText         ?? DBNull.Value,
                (object?)r.BodyType?.ToString() ?? DBNull.Value,
                (object?)r.BodyValue            ?? DBNull.Value,
                r.DroppedAttributesCount,
                r.Flags,
                (object?)r.TraceIdHex           ?? DBNull.Value,
                (object?)r.SpanIdHex            ?? DBNull.Value,
                (object?)SerializeJsonOrNull(r.Attributes) ?? DBNull.Value,
                (object?)r.EventName            ?? DBNull.Value
            });
        }

        await BulkInsertAsync(conn, "log_records", columns, rows, ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static async Task<Dictionary<(string TraceId, string SpanId), long>> BulkInsertSpansAsync(
        MySqlConnection conn,
        List<(SpanModel Span, ResourceModel? Resource, InstrumentationScopeModel? Scope)> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // Distinct natural keys in this batch.
        var keys = new List<(string, string)>();
        var keySet = new HashSet<(string, string)>();
        foreach (var (span, _, _) in spans)
            if (keySet.Add((span.TraceIdHex, span.SpanIdHex)))
                keys.Add((span.TraceIdHex, span.SpanIdHex));

        // Snapshot which keys already exist so that, after INSERT IGNORE, we can return ids only
        // for the newly inserted spans (matching the SqlServer MERGE ... OUTPUT semantics and so
        // avoiding duplicate span_events / span_links for pre-existing spans).
        var before = await SelectSpanIdsAsync(conn, keys, ct);

        var columns = new[]
        {
            "trace_id", "span_id", "parent_span_id", "resource_id", "scope_id",
            "name", "kind", "start_time_unix_nano", "end_time_unix_nano",
            "dropped_attributes_count", "dropped_events_count", "dropped_links_count",
            "trace_state", "status_code", "status_message", "attributes_json", "flags"
        };

        var rows = new List<object?[]>(keys.Count);
        var added = new HashSet<(string, string)>();
        foreach (var (span, resource, scope) in spans)
        {
            if (!added.Add((span.TraceIdHex, span.SpanIdHex))) continue;
            rows.Add(new object?[]
            {
                span.TraceIdHex,
                span.SpanIdHex,
                (object?)span.ParentSpanIdHex ?? DBNull.Value,
                resourceIds[HashResource(NormalizeResource(resource))],
                scopeIds[HashScope(NormalizeScope(scope))],
                span.Name,
                span.Kind.ToString(),
                span.StartTimeUnixNano,
                span.EndTimeUnixNano,
                span.DroppedAttributesCount,
                span.DroppedEventsCount,
                span.DroppedLinksCount,
                (object?)span.TraceState    ?? DBNull.Value,
                span.StatusCode.ToString(),
                (object?)span.StatusMessage ?? DBNull.Value,
                (object?)SerializeJsonOrNull(span.Attributes) ?? DBNull.Value,
                span.Flags
            });
        }

        await BulkInsertAsync(conn, "spans", columns, rows, ct, ignore: true);

        var after = await SelectSpanIdsAsync(conn, keys, ct);

        var inserted = new Dictionary<(string, string), long>();
        foreach (var kv in after)
            if (!before.ContainsKey(kv.Key))
                inserted[kv.Key] = kv.Value;

        return inserted;
    }

    private static async Task<Dictionary<(string, string), long>> SelectSpanIdsAsync(
        MySqlConnection conn, List<(string, string)> keys, CancellationToken ct)
    {
        var map = new Dictionary<(string, string), long>();
        if (keys.Count == 0) return map;

        for (var offset = 0; offset < keys.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, keys.Count - offset);
            var sb = new StringBuilder("SELECT id, trace_id, span_id FROM spans WHERE (trace_id, span_id) IN (");
            await using var cmd = new MySqlCommand { Connection = conn };
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("(@t").Append(i).Append(",@s").Append(i).Append(')');
                cmd.Parameters.AddWithValue($"@t{i}", keys[offset + i].Item1);
                cmd.Parameters.AddWithValue($"@s{i}", keys[offset + i].Item2);
            }
            sb.Append(')');
            cmd.CommandText = sb.ToString();

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                map[(reader.GetString(1), reader.GetString(2))] = reader.GetInt64(0);
        }

        return map;
    }

    private static async Task BulkInsertSpanEventsAsync(
        MySqlConnection conn,
        List<(long SpanDbId, SpanEventModel Event)> events,
        CancellationToken ct)
    {
        var columns = new[] { "span_id", "name", "time_unix_nano", "dropped_attributes_count", "attributes_json" };

        var rows = new List<object?[]>(events.Count);
        foreach (var (spanId, e) in events)
            rows.Add(new object?[]
            {
                spanId, e.Name, e.TimeUnixNano, e.DroppedAttributesCount,
                (object?)SerializeJsonOrNull(e.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "span_events", columns, rows, ct);
    }

    private static async Task BulkInsertSpanLinksAsync(
        MySqlConnection conn,
        List<(long SpanDbId, SpanLinkModel Link)> links,
        CancellationToken ct)
    {
        var columns = new[]
        {
            "span_id", "linked_trace_id", "linked_span_id", "trace_state",
            "dropped_attributes_count", "attributes_json", "flags"
        };

        var rows = new List<object?[]>(links.Count);
        foreach (var (spanId, l) in links)
            rows.Add(new object?[]
            {
                spanId, l.LinkedTraceIdHex, l.LinkedSpanIdHex,
                (object?)l.TraceState ?? DBNull.Value,
                l.DroppedAttributesCount,
                (object?)SerializeJsonOrNull(l.Attributes) ?? DBNull.Value,
                l.Flags
            });

        await BulkInsertAsync(conn, "span_links", columns, rows, ct);
    }

    // =========================================================================
    // INSERT: METRICS (individually to capture AUTO_INCREMENT-generated IDs)
    // =========================================================================
    // metrics is the low-volume reference table; each row is inserted on its own so
    // cmd.LastInsertedId yields the id that keys the high-volume data point inserts below.

    private static async Task<long[]> InsertMetricsAsync(
        MySqlConnection conn,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO metrics (resource_id, scope_id, name, description, unit, type)
            VALUES (@resourceId, @scopeId, @name, @description, @unit, @type)
            """;

        var ids = new long[metrics.Count];
        for (var i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            await using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@resourceId",  resourceIds[HashResource(NormalizeResource(m.Resource))]);
            cmd.Parameters.AddWithValue("@scopeId",     scopeIds[HashScope(NormalizeScope(m.InstrumentationScope))]);
            cmd.Parameters.AddWithValue("@name",        m.Name);
            cmd.Parameters.AddWithValue("@description", (object?)m.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@unit",        (object?)m.Unit        ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type",        m.Type.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            ids[i] = cmd.LastInsertedId;
        }

        return ids;
    }

    // =========================================================================
    // BULK INSERT: DATA POINTS
    // =========================================================================

    private static async Task BulkInsertGaugeDataPointsAsync(
        MySqlConnection conn, long metricId,
        List<GaugeDataPointModel> dataPoints, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano",
            "value_double", "value_int", "flags", "attributes_json"
        };

        var rows = new List<object?[]>(dataPoints.Count);
        foreach (var d in dataPoints)
            rows.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt), d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "gauge_data_points", columns, rows, ct);
    }

    private static async Task BulkInsertSumDataPointsAsync(
        MySqlConnection conn, long metricId,
        List<SumDataPointModel> dataPoints, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int",
            "aggregation_temporality", "is_monotonic", "flags", "attributes_json"
        };

        var rows = new List<object?[]>(dataPoints.Count);
        foreach (var d in dataPoints)
            rows.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt),
                d.AggregationTemporality.ToString(), d.IsMonotonic, d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "sum_data_points", columns, rows, ct);
    }

    private static async Task BulkInsertHistogramDataPointsAsync(
        MySqlConnection conn, long metricId,
        List<HistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "bucket_counts", "explicit_bounds", "aggregation_temporality", "flags",
            "min_value", "max_value", "attributes_json"
        };

        var rows = new List<object?[]>(dataPoints.Count);
        foreach (var d in dataPoints)
            rows.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum),
                (object?)SerializeJsonOrNull(d.BucketCounts)   ?? DBNull.Value,
                (object?)SerializeJsonOrNull(d.ExplicitBounds) ?? DBNull.Value,
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "histogram_data_points", columns, rows, ct);
    }

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        MySqlConnection conn, long metricId,
        List<ExponentialHistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "scale", "zero_count", "positive_offset", "positive_bucket_counts",
            "negative_offset", "negative_bucket_counts", "aggregation_temporality",
            "flags", "min_value", "max_value", "attributes_json"
        };

        var rows = new List<object?[]>(dataPoints.Count);
        foreach (var d in dataPoints)
            rows.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum), d.Scale, d.ZeroCount,
                BoxOrNull(d.PositiveOffset),
                (object?)SerializeJsonOrNull(d.PositiveBucketCounts) ?? DBNull.Value,
                BoxOrNull(d.NegativeOffset),
                (object?)SerializeJsonOrNull(d.NegativeBucketCounts) ?? DBNull.Value,
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "exponential_histogram_data_points", columns, rows, ct);
    }

    private static async Task BulkInsertSummaryDataPointsAsync(
        MySqlConnection conn, long metricId,
        List<SummaryDataPointModel> dataPoints, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "quantile_values", "flags", "attributes_json"
        };

        var rows = new List<object?[]>(dataPoints.Count);
        foreach (var d in dataPoints)
            rows.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, d.Sum,
                (object?)SerializeJsonOrNull(d.QuantileValues) ?? DBNull.Value,
                d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, "summary_data_points", columns, rows, ct);
    }

    // =========================================================================
    // PROVIDER-LOCAL HELPERS
    // =========================================================================

    // Builds and executes chunked, parameterized multi-row INSERT statements. Pass
    // ignore: true to emit INSERT IGNORE (used for spans, where the unique (trace_id, span_id)
    // key deduplicates re-delivered spans).
    private static async Task BulkInsertAsync(
        MySqlConnection conn, string table, string[] columns,
        List<object?[]> rows, CancellationToken ct, bool ignore = false)
    {
        if (rows.Count == 0) return;

        var colList = string.Join(", ", columns);
        var verb = ignore ? "INSERT IGNORE INTO " : "INSERT INTO ";

        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, rows.Count - offset);
            var sb = new StringBuilder(verb).Append(table).Append(" (").Append(colList).Append(") VALUES ");
            await using var cmd = new MySqlCommand { Connection = conn };
            for (var r = 0; r < count; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append('(');
                var row = rows[offset + r];
                for (var c = 0; c < columns.Length; c++)
                {
                    if (c > 0) sb.Append(',');
                    var p = $"@r{r}c{c}";
                    sb.Append(p);
                    cmd.Parameters.AddWithValue(p, row[c] ?? DBNull.Value);
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // Boxes a nullable value type for ADO.NET, mapping null to DBNull.
    private static object BoxOrNull<T>(T? value) where T : struct
        => value.HasValue ? value.Value : DBNull.Value;
}
