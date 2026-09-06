using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data;
using static Keryhe.Telemetry.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.Timescale.Services;

/// <summary>
/// TimescaleDB (PostgreSQL) implementation of <see cref="ITelemetryBulkWriter"/>. Owns
/// only the Npgsql bulk path: <c>unnest()</c> array expansion for set-based inserts,
/// <c>ON CONFLICT DO NOTHING</c> dedup with <c>RETURNING</c>, and CTE upserts for
/// resource/scope. The channel-draining loop and the normalization/hashing helpers live
/// in <c>Keryhe.Telemetry.Data</c>. The hypertable nature of the target tables is
/// transparent to these inserts.
/// </summary>
public sealed class TimescaleBulkWriter(
    NpgsqlDataSource dataSource,
    ResourceScopeCache cache,
    ILogger<TimescaleBulkWriter> logger) : ITelemetryBulkWriter
{
    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        await BulkInsertLogsAsync(conn, records, resourceIds, scopeIds, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public async Task FlushTracesAsync(List<TraceModel> traces, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Flatten spans, resolving effective resource/scope per span
        var spans = traces
            .SelectMany(t => t.Spans.Select(s => (
                Span: s,
                Resource: s.Resource ?? t.Resource,
                Scope: s.InstrumentationScope ?? t.InstrumentationScope)))
            .ToList();

        if (spans.Count == 0) return;

        var resourceIds = await ResolveResourcesAsync(conn, spans.Select(s => s.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, spans.Select(s => s.Scope), ct);

        var insertedSpanIds = await BulkInsertSpansAsync(conn, spans, resourceIds, scopeIds, ct);

        var events = new List<(long SpanDbId, SpanEventModel Event)>();
        var links = new List<(long SpanDbId, SpanLinkModel Link)>();

        foreach (var (span, _, _) in spans)
        {
            if (!insertedSpanIds.TryGetValue((span.TraceIdHex, span.SpanIdHex), out var dbId))
                continue;
            foreach (var e in span.Events) events.Add((dbId, e));
            foreach (var l in span.Links) links.Add((dbId, l));
        }

        if (events.Count > 0) await BulkInsertSpanEventsAsync(conn, events, ct);
        if (links.Count > 0) await BulkInsertSpanLinksAsync(conn, links, ct);

        logger.LogDebug("Flushed {SpanCount} spans across {TraceCount} traces", spans.Count, traces.Count);
    }

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, metrics.Select(m => m.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, metrics.Select(m => m.InstrumentationScope), ct);

        var metricIds = await ResolveMetricIdsAsync(conn, metrics, resourceIds, scopeIds, ct);

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
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
        NpgsqlConnection conn,
        IEnumerable<ResourceModel?> resources,
        CancellationToken ct)
    {
        // Keyed by ResourceKey, never by the bare hash: two tenants running the same service with the
        // same attributes share a hash, and collapsing them here would give the second tenant the
        // first tenant's resources.id -- silently storing its telemetry under the wrong owner.
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new Dictionary<string, (string Hash, ResourceModel Model)>(StringComparer.Ordinal);

        foreach (var r in resources)
        {
            var model = NormalizeResource(r);
            var hash = HashResource(model);
            var key = ResourceKey(model.TenantId, hash);
            if (result.ContainsKey(key)) continue;

            if (cache.TryGetResource(model.TenantId, hash, out var id))
                result[key] = id;
            else
                pending.TryAdd(key, (hash, model));
        }

        foreach (var (key, entry) in pending)
        {
            var id = await UpsertResourceAsync(conn, entry.Model, entry.Hash, ct);
            cache.SetResource(entry.Model.TenantId, entry.Hash, id);
            result[key] = id;
        }

        return result;
    }

    private async Task<Dictionary<string, long>> ResolveScopesAsync(
        NpgsqlConnection conn,
        IEnumerable<InstrumentationScopeModel?> scopes,
        CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new Dictionary<string, InstrumentationScopeModel>(StringComparer.Ordinal);

        foreach (var s in scopes)
        {
            var model = NormalizeScope(s);
            var hash = HashScope(model);
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

    // Single CTE that returns the ID whether the row was just inserted or already existed.
    private static async Task<long> UpsertResourceAsync(
        NpgsqlConnection conn, ResourceModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            WITH ins AS (
                INSERT INTO resources (attributes_json, created_at, resource_hash, schema_url, tenant_id)
                VALUES (CAST($1 AS jsonb), NOW(), $2, $3, $4)
                ON CONFLICT (tenant_id, resource_hash) DO NOTHING
                RETURNING id
            )
            SELECT id FROM ins
            UNION ALL
            SELECT id FROM resources WHERE tenant_id = $4 AND resource_hash = $2
            LIMIT 1
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, SerializeDeterministicJson(model.Attributes));
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, hash);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Bigint, model.TenantId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> UpsertScopeAsync(
        NpgsqlConnection conn, InstrumentationScopeModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            WITH ins AS (
                INSERT INTO instrumentation_scopes (name, version, schema_url, scope_hash, created_at, attributes_json)
                VALUES ($1, $2, $3, $4, NOW(), CAST($5 AS jsonb))
                ON CONFLICT (scope_hash) DO NOTHING
                RETURNING id
            )
            SELECT id FROM ins
            UNION ALL
            SELECT id FROM instrumentation_scopes WHERE scope_hash = $4
            LIMIT 1
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, model.Name);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)model.Version ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Text, hash);
        cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, SerializeDeterministicJson(model.Attributes));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // =========================================================================
    // BULK INSERT: LOGS
    // =========================================================================

    private static async Task BulkInsertLogsAsync(
        NpgsqlConnection conn,
        List<LogRecordModel> records,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO log_records (
                resource_id, scope_id, time_unix_nano, observed_time_unix_nano,
                severity_number, severity_text, body_type, body_value,
                dropped_attributes_count, flags, trace_id, span_id, attributes_json, event_name)
            SELECT
                unnest($1::bigint[]), unnest($2::bigint[]),
                unnest($3::bigint[]), unnest($4::bigint[]),
                unnest($5::int[]),    unnest($6::text[]),
                unnest($7::text[]),   unnest($8::text[]),
                unnest($9::int[]),    unnest($10::int[]),
                unnest($11::text[]),  unnest($12::text[]),
                unnest($13::jsonb[]), unnest($14::text[])
            """;

        var n = records.Count;
        var resIds = new long[n];
        var scoIds = new long[n];
        var times = new long[n];
        var obsTimes = new long?[n];
        var sevNums = new int?[n];
        var sevTexts = new string?[n];
        var bodyTypes = new string?[n];
        var bodyValues = new string?[n];
        var dropped = new int[n];
        var flags = new int[n];
        var traceIds = new string?[n];
        var spanIds = new string?[n];
        var attrs = new string?[n];
        var eventNames = new string?[n];

        for (var i = 0; i < n; i++)
        {
            var r = records[i];
            resIds[i] = resourceIds[ResourceKey(r.Resource)];
            scoIds[i] = scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))];
            times[i] = r.TimeUnixNano ?? 0L;
            obsTimes[i] = r.ObservedTimeUnixNano;
            sevNums[i] = r.SeverityNumber;
            sevTexts[i] = r.SeverityText;
            bodyTypes[i] = r.BodyType?.ToString();
            bodyValues[i] = r.BodyValue;
            dropped[i] = r.DroppedAttributesCount;
            flags[i] = r.Flags;
            traceIds[i] = r.TraceIdHex;
            spanIds[i] = r.SpanIdHex;
            attrs[i] = r.Attributes.Count > 0 ? JsonSerializer.Serialize(r.Attributes) : null;
            eventNames[i] = r.EventName;
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = resIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scoIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = times, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = obsTimes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = sevNums, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = sevTexts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = bodyTypes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = bodyValues, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dropped, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = flags, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = traceIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spanIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = eventNames, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static async Task<Dictionary<(string TraceId, string SpanId), long>> BulkInsertSpansAsync(
        NpgsqlConnection conn,
        List<(SpanModel Span, ResourceModel? Resource, InstrumentationScopeModel? Scope)> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO spans (
                trace_id, span_id, parent_span_id, resource_id, scope_id,
                name, kind, start_time_unix_nano, end_time_unix_nano,
                dropped_attributes_count, dropped_events_count, dropped_links_count,
                trace_state, status_code, status_message, attributes_json, flags)
            SELECT
                unnest($1::text[]),   unnest($2::text[]),   unnest($3::text[]),
                unnest($4::bigint[]), unnest($5::bigint[]),
                unnest($6::text[]),   unnest($7::text[]),
                unnest($8::bigint[]), unnest($9::bigint[]),
                unnest($10::int[]),   unnest($11::int[]),   unnest($12::int[]),
                unnest($13::text[]),  unnest($14::text[]),  unnest($15::text[]),
                unnest($16::jsonb[]), unnest($17::int[])
            ON CONFLICT (trace_id, span_id) DO NOTHING
            RETURNING id, trace_id, span_id
            """;

        var n = spans.Count;
        var traceIds = new string[n];
        var spanIds = new string[n];
        var parentIds = new string?[n];
        var resIds = new long[n];
        var scoIds = new long[n];
        var names = new string[n];
        var kinds = new string[n];
        var startTimes = new long[n];
        var endTimes = new long[n];
        var droppedAttrs = new int[n];
        var droppedEvents = new int[n];
        var droppedLinks = new int[n];
        var traceStates = new string?[n];
        var statusCodes = new string[n];
        var statusMsgs = new string?[n];
        var attrs = new string?[n];
        var flags = new int[n];

        for (var i = 0; i < n; i++)
        {
            var (span, resource, scope) = spans[i];
            resIds[i] = resourceIds[ResourceKey(resource)];
            scoIds[i] = scopeIds[HashScope(NormalizeScope(scope))];
            traceIds[i] = span.TraceIdHex;
            spanIds[i] = span.SpanIdHex;
            parentIds[i] = span.ParentSpanIdHex;
            names[i] = span.Name;
            kinds[i] = span.Kind.ToString();
            startTimes[i] = span.StartTimeUnixNano;
            endTimes[i] = span.EndTimeUnixNano;
            droppedAttrs[i] = span.DroppedAttributesCount;
            droppedEvents[i] = span.DroppedEventsCount;
            droppedLinks[i] = span.DroppedLinksCount;
            traceStates[i] = span.TraceState;
            statusCodes[i] = span.StatusCode.ToString();
            statusMsgs[i] = span.StatusMessage;
            attrs[i] = span.Attributes?.Count > 0 ? JsonSerializer.Serialize(span.Attributes) : null;
            flags[i] = span.Flags;
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = traceIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = spanIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = parentIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = resIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scoIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = names, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = kinds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = startTimes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = endTimes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = droppedAttrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = droppedEvents, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = droppedLinks, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = traceStates, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = statusCodes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = statusMsgs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = flags, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });

        var inserted = new Dictionary<(string, string), long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            inserted[(reader.GetString(1), reader.GetString(2))] = reader.GetInt64(0);

        return inserted;
    }

    private static async Task BulkInsertSpanEventsAsync(
        NpgsqlConnection conn,
        List<(long SpanDbId, SpanEventModel Event)> events,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO span_events (span_id, name, time_unix_nano, dropped_attributes_count, attributes_json)
            SELECT unnest($1::bigint[]), unnest($2::text[]), unnest($3::bigint[]), unnest($4::int[]), unnest($5::jsonb[])
            """;

        var n = events.Count;
        var spanIds = new long[n];
        var names = new string[n];
        var times = new long[n];
        var dropped = new int[n];
        var attrs = new string?[n];

        for (var i = 0; i < n; i++)
        {
            var (spanId, e) = events[i];
            spanIds[i] = spanId;
            names[i] = e.Name;
            times[i] = e.TimeUnixNano;
            dropped[i] = e.DroppedAttributesCount;
            attrs[i] = e.Attributes?.Count > 0 ? JsonSerializer.Serialize(e.Attributes) : null;
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = spanIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = names, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = times, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dropped, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertSpanLinksAsync(
        NpgsqlConnection conn,
        List<(long SpanDbId, SpanLinkModel Link)> links,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO span_links (span_id, linked_trace_id, linked_span_id, trace_state, dropped_attributes_count, attributes_json, flags)
            SELECT unnest($1::bigint[]), unnest($2::text[]), unnest($3::text[]), unnest($4::text[]), unnest($5::int[]), unnest($6::jsonb[]), unnest($7::int[])
            """;

        var n = links.Count;
        var spanIds = new long[n];
        var linkedTraceIds = new string[n];
        var linkedSpanIds = new string[n];
        var traceStates = new string?[n];
        var dropped = new int[n];
        var attrs = new string?[n];
        var flags = new int[n];

        for (var i = 0; i < n; i++)
        {
            var (spanId, l) = links[i];
            spanIds[i] = spanId;
            linkedTraceIds[i] = l.LinkedTraceIdHex;
            linkedSpanIds[i] = l.LinkedSpanIdHex;
            traceStates[i] = l.TraceState;
            dropped[i] = l.DroppedAttributesCount;
            attrs[i] = l.Attributes?.Count > 0 ? JsonSerializer.Serialize(l.Attributes) : null;
            flags[i] = l.Flags;
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = spanIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = linkedTraceIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = linkedSpanIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = traceStates, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dropped, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = flags, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =========================================================================
    // RESOLVE: METRICS
    // =========================================================================
    // metrics is a reference table deduplicated on (resource_id, name, type, scope_id).
    // Mirrors ResolveResourcesAsync: dedup within the batch, consult the process cache,
    // upsert only what is left, cache the result. A warm process issues no statement here
    // at all -- which is also why description/unit refresh on a cold start rather than on
    // every export.

    private async Task<long[]> ResolveMetricIdsAsync(
        NpgsqlConnection conn,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // DO UPDATE rather than DO NOTHING so RETURNING fires for conflicting rows too, giving
        // one output row per input row. That is what lets this skip the "WITH ins AS (...)
        // UNION ALL SELECT ... LIMIT 1" fallback that UpsertResourceAsync needs.
        //
        // Do NOT add a WHERE to the DO UPDATE to skip no-op writes: it would suppress RETURNING
        // for unchanged rows and bring that fallback straight back.
        //
        // The key columns come back rather than relying on RETURNING row order, which PostgreSQL
        // does not actually guarantee.
        const string sql = """
            INSERT INTO metrics (resource_id, scope_id, name, description, unit, type, created_at)
            SELECT unnest($1::bigint[]), unnest($2::bigint[]), unnest($3::text[]),
                   unnest($4::text[]),   unnest($5::text[]),   unnest($6::text[]), NOW()
            ON CONFLICT (resource_id, name, type, scope_id) DO UPDATE
                SET description = EXCLUDED.description,
                    unit        = EXCLUDED.unit
            RETURNING id, resource_id, scope_id, name, type
            """;

        var n = metrics.Count;
        var keys = new string[n];
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new List<(string Key, long ResId, long ScoId, MetricModel M)>();
        var pendingKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < n; i++)
        {
            var m = metrics[i];
            var resId = resourceIds[ResourceKey(m.Resource)];
            var scoId = scopeIds[HashScope(NormalizeScope(m.InstrumentationScope))];
            var type = m.Type.ToString();
            keys[i] = MetricKey(resId, scoId, m.Name, type);

            if (result.ContainsKey(keys[i])) continue;
            if (cache.TryGetMetric(keys[i], out var cached)) { result[keys[i]] = cached; continue; }

            // Per-batch dedup is mandatory, not an optimization: the worker merges many OTLP
            // exports into one batch, so the same metric recurs many times, and PostgreSQL
            // raises 21000 "ON CONFLICT DO UPDATE command cannot affect row a second time" if
            // one statement touches the same key twice.
            if (pendingKeys.Add(keys[i]))
                pending.Add((keys[i], resId, scoId, m));
        }

        if (pending.Count > 0)
        {
            // Deterministic lock order, so two collectors upserting the same key set cannot deadlock.
            pending.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

            var c = pending.Count;
            var resIds = new long[c];
            var scoIds = new long[c];
            var names = new string[c];
            var descs = new string?[c];
            var units = new string?[c];
            var types = new string[c];

            for (var i = 0; i < c; i++)
            {
                var (_, resId, scoId, m) = pending[i];
                resIds[i] = resId;
                scoIds[i] = scoId;
                names[i] = m.Name;
                descs[i] = m.Description;
                units[i] = m.Unit;
                types[i] = m.Type.ToString();
            }

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = resIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = scoIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = names, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = descs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = units, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = types, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var key = MetricKey(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4));
                result[key] = id;
                cache.SetMetric(key, id);
            }
        }

        var ids = new long[n];
        for (var i = 0; i < n; i++) ids[i] = result[keys[i]];
        return ids;
    }

    private static async Task BulkInsertGaugeDataPointsAsync(
        NpgsqlConnection conn, long metricId,
        List<GaugeDataPointModel> dataPoints, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO gauge_data_points (metric_id, start_time_unix_nano, time_unix_nano, value_double, value_int, flags, attributes_json)
            SELECT unnest($1::bigint[]), unnest($2::bigint[]), unnest($3::bigint[]),
                   unnest($4::float8[]), unnest($5::bigint[]), unnest($6::int[]), unnest($7::jsonb[])
            """;

        var n = dataPoints.Count;
        var mids = Repeat(metricId, n);
        var startTimes = dataPoints.Select(d => d.StartTimeUnixNano).ToArray();
        var times = dataPoints.Select(d => d.TimeUnixNano).ToArray();
        var valDoubles = dataPoints.Select(d => d.ValueDouble).ToArray();
        var valInts = dataPoints.Select(d => d.ValueInt).ToArray();
        var flags = dataPoints.Select(d => d.Flags).ToArray();
        var attrs = dataPoints.Select(d => SerializeJsonOrNull(d.Attributes)).ToArray();

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = mids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = startTimes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = times, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = valDoubles, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = valInts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = flags, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertSumDataPointsAsync(
        NpgsqlConnection conn, long metricId,
        List<SumDataPointModel> dataPoints, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO sum_data_points (metric_id, start_time_unix_nano, time_unix_nano, value_double, value_int,
                                         aggregation_temporality, is_monotonic, flags, attributes_json)
            SELECT unnest($1::bigint[]), unnest($2::bigint[]), unnest($3::bigint[]),
                   unnest($4::float8[]), unnest($5::bigint[]),
                   unnest($6::text[]),   unnest($7::bool[]),   unnest($8::int[]), unnest($9::jsonb[])
            """;

        var n = dataPoints.Count;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Repeat(metricId, n), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.StartTimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.TimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.ValueDouble).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.ValueInt).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.AggregationTemporality.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.IsMonotonic).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Flags).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.Attributes)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertHistogramDataPointsAsync(
        NpgsqlConnection conn, long metricId,
        List<HistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO histogram_data_points (
                metric_id, start_time_unix_nano, time_unix_nano, count, sum_value,
                bucket_counts, explicit_bounds, aggregation_temporality,
                flags, min_value, max_value, attributes_json)
            SELECT unnest($1::bigint[]), unnest($2::bigint[]), unnest($3::bigint[]),
                   unnest($4::bigint[]), unnest($5::float8[]),
                   unnest($6::jsonb[]),  unnest($7::jsonb[]),  unnest($8::text[]),
                   unnest($9::int[]),    unnest($10::float8[]), unnest($11::float8[]),
                   unnest($12::jsonb[])
            """;

        var n = dataPoints.Count;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Repeat(metricId, n), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.StartTimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.TimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Count).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Sum).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.BucketCounts)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.ExplicitBounds)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.AggregationTemporality.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Flags).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Min).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Max).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.Attributes)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        NpgsqlConnection conn, long metricId,
        List<ExponentialHistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO exponential_histogram_data_points (
                metric_id, start_time_unix_nano, time_unix_nano, count, sum_value,
                scale, zero_count, positive_offset, positive_bucket_counts,
                negative_offset, negative_bucket_counts,
                aggregation_temporality, flags, min_value, max_value, attributes_json)
            SELECT unnest($1::bigint[]),  unnest($2::bigint[]),  unnest($3::bigint[]),
                   unnest($4::bigint[]),  unnest($5::float8[]),
                   unnest($6::int[]),     unnest($7::bigint[]),
                   unnest($8::int[]),     unnest($9::jsonb[]),
                   unnest($10::int[]),    unnest($11::jsonb[]),
                   unnest($12::text[]),   unnest($13::int[]),
                   unnest($14::float8[]), unnest($15::float8[]),
                   unnest($16::jsonb[])
            """;

        var n = dataPoints.Count;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Repeat(metricId, n), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.StartTimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.TimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Count).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Sum).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Scale).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.ZeroCount).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.PositiveOffset).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.PositiveBucketCounts)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.NegativeOffset).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.NegativeBucketCounts)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.AggregationTemporality.ToString()).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Flags).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Min).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Max).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.Attributes)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BulkInsertSummaryDataPointsAsync(
        NpgsqlConnection conn, long metricId,
        List<SummaryDataPointModel> dataPoints, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO summary_data_points (metric_id, start_time_unix_nano, time_unix_nano, count, sum_value, quantile_values, flags, attributes_json)
            SELECT unnest($1::bigint[]), unnest($2::bigint[]), unnest($3::bigint[]),
                   unnest($4::bigint[]), unnest($5::float8[]), unnest($6::jsonb[]),
                   unnest($7::int[]),    unnest($8::jsonb[])
            """;

        var n = dataPoints.Count;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = Repeat(metricId, n), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.StartTimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.TimeUnixNano).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Count).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => (double?)d.Sum).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Double });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.QuantileValues)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => d.Flags).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = dataPoints.Select(d => SerializeJsonOrNull(d.Attributes)).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =========================================================================
    // PROVIDER-LOCAL HELPERS
    // =========================================================================

    // Builds a constant-value array to broadcast a single metric_id across an unnest insert.
    private static long[] Repeat(long value, int count)
    {
        var arr = new long[count];
        Array.Fill(arr, value);
        return arr;
    }
}
