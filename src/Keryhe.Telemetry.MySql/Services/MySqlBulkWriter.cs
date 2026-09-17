using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data;
using static Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="ITelemetryBulkWriter"/>. Owns only the
/// dialect-specific flush logic — batched multi-row <c>INSERT</c> for the high-volume
/// tables, <c>INSERT IGNORE</c> + a natural-key <c>SELECT</c> to resolve span ids, and
/// <c>INSERT ... ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)</c> upserts for
/// resource/scope dedup. The channel-draining loop and the normalization/hashing helpers
/// live in <c>Keryhe.Telemetry.Core.Data</c>. Targets MySQL 8.0+.
///
/// Each <c>Flush*Async</c> runs inside a single transaction, so a failure partway through
/// leaves zero rows from that batch rather than a partially-applied flush. Every command
/// below is enlisted via <c>Transaction = tx</c> -- MySqlConnector, unlike Npgsql, does not
/// document ambient-transaction tracking as guaranteed, so this follows the explicit,
/// always-correct pattern every provider in this codebase now uses. The transaction is
/// never explicitly rolled back: <c>await using</c> disposes it without a matching
/// <c>CommitAsync</c> whenever an exception propagates out of the block, and disposing an
/// uncommitted <see cref="MySqlTransaction"/> rolls it back.
///
/// <see cref="ResourceScopeCache"/> writes for newly-upserted resources/scopes/metrics are
/// DEFERRED until after <c>CommitAsync</c> succeeds, collected in a per-flush
/// <c>postCommitCacheWrites</c> list rather than written the moment each upsert returns an
/// id. This is load-bearing, not cosmetic: caching immediately, before commit, was verified
/// live (against PostgreSQL, but the failure mode is provider-agnostic) to poison the cache
/// on a rollback -- a resource upsert can succeed and be cached mid-transaction, then a
/// later statement in the SAME flush fails and rolls the whole transaction back, leaving the
/// cache pointing at a resources.id that was never actually persisted. Every subsequent
/// flush that resolves that resource then hits the cache, skips re-inserting it, and fails
/// its own FK constraint on the span/log/metric it tries to write -- permanently, since
/// <see cref="ResourceScopeCache"/> entries are never evicted, until the process restarts.
/// Deferring the write until after the commit that makes it true is what closes that gap.
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
        var tx = (MySqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            var postCommitCacheWrites = new List<Action>();
            var resourceIds = await ResolveResourcesAsync(conn, tx, records.Select(r => r.Resource), postCommitCacheWrites, ct);
            var scopeIds    = await ResolveScopesAsync(conn, tx, records.Select(r => r.InstrumentationScope), postCommitCacheWrites, ct);

            await BulkInsertLogsAsync(conn, tx, records, resourceIds, scopeIds, ct);
            await tx.CommitAsync(ct);
            foreach (var write in postCommitCacheWrites) write();
            logger.LogDebug("Flushed {Count} log records", records.Count);
        }
    }

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public async Task FlushTracesAsync(List<SpanModel> spans, CancellationToken ct = default)
    {
        if (spans.Count == 0) return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var tx = (MySqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            var postCommitCacheWrites = new List<Action>();
            var resourceIds = await ResolveResourcesAsync(conn, tx, spans.Select(s => s.Resource), postCommitCacheWrites, ct);
            var scopeIds    = await ResolveScopesAsync(conn, tx, spans.Select(s => s.InstrumentationScope), postCommitCacheWrites, ct);

            var insertedSpanIds = await BulkInsertSpansAsync(conn, tx, spans, resourceIds, scopeIds, ct);

            var events = new List<(long SpanDbId, SpanEventModel Event)>();
            var links  = new List<(long SpanDbId, SpanLinkModel  Link)>();

            foreach (var span in spans)
            {
                if (!insertedSpanIds.TryGetValue((span.TraceIdHex, span.SpanIdHex), out var dbId))
                    continue;
                foreach (var e in span.Events) events.Add((dbId, e));
                foreach (var l in span.Links)  links.Add((dbId, l));
            }

            if (events.Count > 0) await BulkInsertSpanEventsAsync(conn, tx, events, ct);
            if (links.Count  > 0) await BulkInsertSpanLinksAsync(conn, tx, links, ct);

            await tx.CommitAsync(ct);
            foreach (var write in postCommitCacheWrites) write();
            logger.LogDebug("Flushed {SpanCount} spans", spans.Count);
        }
    }

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var tx = (MySqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            var postCommitCacheWrites = new List<Action>();
            var resourceIds = await ResolveResourcesAsync(conn, tx, metrics.Select(m => m.Resource), postCommitCacheWrites, ct);
            var scopeIds    = await ResolveScopesAsync(conn, tx, metrics.Select(m => m.InstrumentationScope), postCommitCacheWrites, ct);

            var metricIds = await ResolveMetricIdsAsync(conn, tx, metrics, resourceIds, scopeIds, postCommitCacheWrites, ct);

            // Group data points by target table across the WHOLE batch, attaching each row's already
            // -resolved metric_id as it is grouped. This is what turns a metric flush into AT MOST
            // FIVE calls to BulkInsertAsync instead of one per metric: a naive per-metric loop here
            // defeats the whole point of batching (up to MaxMetricFlushBatchSize round trips per
            // flush) -- BulkInsertAsync's own 500-row chunking then applies once, across the grouped
            // rows, rather than once per metric.
            var gaugeRows = new List<(long MetricId, GaugeDataPointModel DataPoint)>();
            var sumRows = new List<(long MetricId, SumDataPointModel DataPoint)>();
            var histogramRows = new List<(long MetricId, HistogramDataPointModel DataPoint)>();
            var expHistogramRows = new List<(long MetricId, ExponentialHistogramDataPointModel DataPoint)>();
            var summaryRows = new List<(long MetricId, SummaryDataPointModel DataPoint)>();

            for (var i = 0; i < metrics.Count; i++)
            {
                var metric   = metrics[i];
                var metricId = metricIds[i];
                switch (metric.Type)
                {
                    case MetricType.GAUGE when metric.GaugeDataPoints?.Count > 0:
                        gaugeRows.AddRange(metric.GaugeDataPoints.Select(d => (metricId, d)));
                        break;
                    case MetricType.SUM when metric.SumDataPoints?.Count > 0:
                        sumRows.AddRange(metric.SumDataPoints.Select(d => (metricId, d)));
                        break;
                    case MetricType.HISTOGRAM when metric.HistogramDataPoints?.Count > 0:
                        histogramRows.AddRange(metric.HistogramDataPoints.Select(d => (metricId, d)));
                        break;
                    case MetricType.EXPONENTIAL_HISTOGRAM when metric.ExponentialHistogramDataPoints?.Count > 0:
                        expHistogramRows.AddRange(metric.ExponentialHistogramDataPoints.Select(d => (metricId, d)));
                        break;
                    case MetricType.SUMMARY when metric.SummaryDataPoints?.Count > 0:
                        summaryRows.AddRange(metric.SummaryDataPoints.Select(d => (metricId, d)));
                        break;
                }
            }

            if (gaugeRows.Count > 0) await BulkInsertGaugeDataPointsAsync(conn, tx, gaugeRows, ct);
            if (sumRows.Count > 0) await BulkInsertSumDataPointsAsync(conn, tx, sumRows, ct);
            if (histogramRows.Count > 0) await BulkInsertHistogramDataPointsAsync(conn, tx, histogramRows, ct);
            if (expHistogramRows.Count > 0) await BulkInsertExpHistogramDataPointsAsync(conn, tx, expHistogramRows, ct);
            if (summaryRows.Count > 0) await BulkInsertSummaryDataPointsAsync(conn, tx, summaryRows, ct);

            await tx.CommitAsync(ct);
            foreach (var write in postCommitCacheWrites) write();
            logger.LogDebug("Flushed {Count} metrics", metrics.Count);
        }
    }

    // =========================================================================
    // RESOURCE / SCOPE RESOLUTION
    // =========================================================================

    private async Task<Dictionary<string, long>> ResolveResourcesAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        IEnumerable<ResourceModel?> resources,
        List<Action> postCommitCacheWrites,
        CancellationToken ct)
    {
        // Keyed by ResourceKey, never by the bare hash: two tenants running the same service with the
        // same attributes share a hash, and collapsing them here would give the second tenant the
        // first tenant's resources.id -- silently storing its telemetry under the wrong owner.
        var result  = new Dictionary<string, long>(StringComparer.Ordinal);
        var pending = new Dictionary<string, (string Hash, ResourceModel Model)>(StringComparer.Ordinal);

        foreach (var r in resources)
        {
            var model = NormalizeResource(r);
            var hash  = HashResource(model);
            var key   = ResourceKey(model.TenantId, hash);
            if (result.ContainsKey(key)) continue;
            if (cache.TryGetResource(model.TenantId, hash, out var id))
                result[key] = id;
            else
                pending.TryAdd(key, (hash, model));
        }

        foreach (var (key, entry) in pending)
        {
            var id = await UpsertResourceAsync(conn, tx, entry.Model, entry.Hash, ct);
            // Deferred: caching now, before the transaction commits, would let a later failure in
            // this SAME flush roll back the row while the cache still claims it exists -- see the
            // class doc comment.
            var tenantId = entry.Model.TenantId;
            var hash2 = entry.Hash;
            postCommitCacheWrites.Add(() => cache.SetResource(tenantId, hash2, id));
            result[key] = id;
        }

        return result;
    }

    private async Task<Dictionary<string, long>> ResolveScopesAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        IEnumerable<InstrumentationScopeModel?> scopes,
        List<Action> postCommitCacheWrites,
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
            var id = await UpsertScopeAsync(conn, tx, model, hash, ct);
            // Deferred -- see ResolveResourcesAsync.
            postCommitCacheWrites.Add(() => cache.SetScope(hash, id));
            result[hash] = id;
        }

        return result;
    }

    // INSERT ... ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id) is the MySQL upsert-and-return
    // idiom: on a fresh insert LAST_INSERT_ID() is the new auto-increment id, and on a duplicate
    // key the assignment sets LAST_INSERT_ID() to the existing row's id. Either way
    // cmd.LastInsertedId yields the surrogate id the read repos join on.
    private static async Task<long> UpsertResourceAsync(
        MySqlConnection conn, MySqlTransaction tx, ResourceModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO resources (attributes_json, resource_hash, schema_url, tenant_id)
            VALUES (@attrJson, @hash, @schemaUrl, @tenantId)
            ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
            """;

        await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tenantId",  model.TenantId);
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    private static async Task<long> UpsertScopeAsync(
        MySqlConnection conn, MySqlTransaction tx, InstrumentationScopeModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO instrumentation_scopes (name, version, schema_url, scope_hash, attributes_json)
            VALUES (@name, @version, @schemaUrl, @hash, @attrJson)
            ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)
            """;

        await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
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
        MySqlTransaction tx,
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
                resourceIds[ResourceKey(r.Resource)],
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

        await BulkInsertAsync(conn, tx, "log_records", columns, rows, ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static readonly string[] SpanColumns =
    [
        "trace_id", "span_id", "parent_span_id", "resource_id", "scope_id",
        "name", "kind", "start_time_unix_nano", "end_time_unix_nano",
        "dropped_attributes_count", "dropped_events_count", "dropped_links_count",
        "trace_state", "status_code", "status_message", "attributes_json", "flags"
    ];

    // Deliberately NOT a before/after diff against the whole key set (two extra chunked SELECTs
    // -- up to 8 round trips per 2,000 spans -- to detect duplicates that usually do not exist,
    // since span re-delivery is the exception, not the rule).
    //
    // Instead: for each chunk, run INSERT IGNORE and compare ExecuteNonQueryAsync's affected-row
    // count against the chunk's row count. When they match -- the common case, zero duplicates --
    // every row in the chunk was newly inserted, and MySQL/InnoDB assigns a fresh multi-row
    // INSERT contiguous auto-increment ids in VALUES order (confirmed live: a 3-row all-new
    // INSERT IGNORE returned affected=3 with ids starting at LAST_INSERT_ID() and running
    // consecutively), so `LastInsertedId + rowIndex` gives every span's id with zero extra
    // queries. Only when affected < chunk size (some keys already existed) does this fall back
    // to a SELECT -- and only for that one chunk, not the whole batch. Confirmed live that a
    // partial-duplicate chunk does NOT preserve that contiguous mapping (a skipped row can still
    // reserve -- and waste -- an id), so the fallback there is load-bearing, not a belt-and-braces
    // extra.
    private static async Task<Dictionary<(string TraceId, string SpanId), long>> BulkInsertSpansAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<SpanModel> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // Distinct natural keys, built in the same pass as their rows so index i of one always
        // matches index i of the other -- both dedup against the same `spans` list in the same
        // order, so there is no need to build the key list separately and trust the two loops
        // agree.
        var keys = new List<(string, string)>();
        var rows = new List<object?[]>();
        var added = new HashSet<(string, string)>();
        foreach (var span in spans)
        {
            var key = (span.TraceIdHex, span.SpanIdHex);
            if (!added.Add(key)) continue;
            keys.Add(key);
            rows.Add(new object?[]
            {
                span.TraceIdHex,
                span.SpanIdHex,
                (object?)span.ParentSpanIdHex ?? DBNull.Value,
                resourceIds[ResourceKey(span.Resource)],
                scopeIds[HashScope(NormalizeScope(span.InstrumentationScope))],
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

        var inserted = new Dictionary<(string, string), long>();
        if (rows.Count == 0) return inserted;

        var colList = string.Join(", ", SpanColumns);

        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, rows.Count - offset);
            var sb = new StringBuilder("INSERT IGNORE INTO spans (").Append(colList).Append(") VALUES ");
            await using var cmd = new MySqlCommand { Connection = conn, Transaction = tx };
            for (var r = 0; r < count; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append('(');
                var row = rows[offset + r];
                for (var c = 0; c < SpanColumns.Length; c++)
                {
                    if (c > 0) sb.Append(',');
                    var p = $"@r{r}c{c}";
                    sb.Append(p);
                    cmd.Parameters.AddWithValue(p, row[c] ?? DBNull.Value);
                }
                sb.Append(')');
            }
            cmd.CommandText = sb.ToString();

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                // Every key in this chunk already existed -- nothing new, no query needed: the
                // whole point of this chunk's span_events/span_links is to attach only to spans
                // THIS flush inserted, and it inserted none of them.
                continue;
            }

            var firstId = (long)cmd.LastInsertedId;
            if (affected == count)
            {
                // Zero duplicates -- the common case. A multi-row INSERT IGNORE where every row
                // succeeds assigns auto-increment ids consecutively in VALUES order (confirmed
                // live), so every id is derivable with no query at all.
                for (var r = 0; r < count; r++)
                    inserted[keys[offset + r]] = firstId + r;
            }
            else
            {
                // Some rows in this chunk were duplicates. MySQL still hands out ids for a
                // partial-success multi-row insert from a pool starting at firstId, but which
                // input position got skipped isn't recoverable client-side (confirmed live: a
                // skipped row does not reserve/waste its positional slot, so `firstId + rowIndex`
                // is NOT valid here the way it is in the zero-duplicate branch above).
                //
                // Resolve with one SELECT instead, and use firstId as a floor rather than a
                // before/after diff: MySQL's auto-increment counter is strictly monotonic and
                // global, so ANY row that already existed before this statement ran necessarily
                // has an id < firstId (its id was allocated by some earlier statement, and the
                // counter never goes backwards or reuses a value), while every id THIS statement
                // produced is >= firstId. A key resolving to an id below firstId is therefore
                // provably pre-existing -- no need to have captured a separate "before" snapshot.
                var chunkKeys = keys.GetRange(offset, count);
                var resolved = await SelectSpanIdsAsync(conn, tx, chunkKeys, ct);
                foreach (var (key, id) in resolved)
                    if (id >= firstId)
                        inserted[key] = id;
            }
        }

        return inserted;
    }

    private static async Task<Dictionary<(string, string), long>> SelectSpanIdsAsync(
        MySqlConnection conn, MySqlTransaction tx, List<(string, string)> keys, CancellationToken ct)
    {
        var map = new Dictionary<(string, string), long>();
        if (keys.Count == 0) return map;

        for (var offset = 0; offset < keys.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, keys.Count - offset);
            var sb = new StringBuilder("SELECT id, trace_id, span_id FROM spans WHERE (trace_id, span_id) IN (");
            await using var cmd = new MySqlCommand { Connection = conn, Transaction = tx };
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
        MySqlTransaction tx,
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

        await BulkInsertAsync(conn, tx, "span_events", columns, rows, ct);
    }

    private static async Task BulkInsertSpanLinksAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
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

        await BulkInsertAsync(conn, tx, "span_links", columns, rows, ct);
    }

    // =========================================================================
    // RESOLVE: METRICS (individually, to capture AUTO_INCREMENT-generated IDs)
    // =========================================================================
    // metrics is a reference table deduplicated on (resource_id, name, type, scope_id) -- the
    // comment above about it being "the low-volume reference table" is finally true. Mirrors
    // ResolveResourcesAsync: dedup within the batch, consult the process cache, upsert only what
    // is left, cache the result. A warm process issues no statement here at all.

    private async Task<long[]> ResolveMetricIdsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        List<Action> postCommitCacheWrites,
        CancellationToken ct)
    {
        // Same upsert-and-return idiom as UpsertResourceAsync: id = LAST_INSERT_ID(id) is what
        // makes cmd.LastInsertedId yield the EXISTING row's id on a duplicate key. It fires even
        // when the row is byte-identical and affected_rows is 0.
        //
        // VALUES(col) is deprecated in MySQL 8.0.20+ in favour of the "AS new" alias form, but the
        // alias form needs 8.0.19+ and is not supported by MariaDB, so keep VALUES() here.
        const string sql = """
            INSERT INTO metrics (resource_id, scope_id, name, description, unit, type)
            VALUES (@resourceId, @scopeId, @name, @description, @unit, @type)
            ON DUPLICATE KEY UPDATE
                description = VALUES(description),
                unit        = VALUES(unit),
                id          = LAST_INSERT_ID(id)
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
            keys[i] = MetricKey(resId, scoId, m.Name, m.Type.ToString());

            if (result.ContainsKey(keys[i])) continue;
            if (cache.TryGetMetric(keys[i], out var cached)) { result[keys[i]] = cached; continue; }

            // Per-batch dedup: the worker merges many OTLP exports into one batch, so the same
            // metric recurs many times. Each duplicate hit would also burn an AUTO_INCREMENT value.
            if (pendingKeys.Add(keys[i]))
                pending.Add((keys[i], resId, scoId, m));
        }

        // Deterministic lock order, so two collectors upserting the same key set cannot deadlock.
        pending.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        foreach (var (key, resId, scoId, m) in pending)
        {
            await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
            cmd.Parameters.AddWithValue("@resourceId",  resId);
            cmd.Parameters.AddWithValue("@scopeId",     scoId);
            cmd.Parameters.AddWithValue("@name",        m.Name);
            cmd.Parameters.AddWithValue("@description", (object?)m.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@unit",        (object?)m.Unit        ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type",        m.Type.ToString());
            await cmd.ExecuteNonQueryAsync(ct);

            var id = cmd.LastInsertedId;
            result[key] = id;
            // Deferred -- see ResolveResourcesAsync.
            postCommitCacheWrites.Add(() => cache.SetMetric(key, id));
        }

        var ids = new long[n];
        for (var i = 0; i < n; i++) ids[i] = result[keys[i]];
        return ids;
    }

    // =========================================================================
    // BULK INSERT: DATA POINTS
    // =========================================================================

    private static async Task BulkInsertGaugeDataPointsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<(long MetricId, GaugeDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano",
            "value_double", "value_int", "flags", "attributes_json"
        };

        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt), d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, tx, "gauge_data_points", columns, values, ct);
    }

    private static async Task BulkInsertSumDataPointsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<(long MetricId, SumDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int",
            "aggregation_temporality", "is_monotonic", "flags", "attributes_json"
        };

        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt),
                d.AggregationTemporality.ToString(), d.IsMonotonic, d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, tx, "sum_data_points", columns, values, ct);
    }

    private static async Task BulkInsertHistogramDataPointsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<(long MetricId, HistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "bucket_counts", "explicit_bounds", "aggregation_temporality", "flags",
            "min_value", "max_value", "attributes_json"
        };

        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum),
                (object?)SerializeJsonOrNull(d.BucketCounts)   ?? DBNull.Value,
                (object?)SerializeJsonOrNull(d.ExplicitBounds) ?? DBNull.Value,
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, tx, "histogram_data_points", columns, values, ct);
    }

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<(long MetricId, ExponentialHistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "scale", "zero_count", "positive_offset", "positive_bucket_counts",
            "negative_offset", "negative_bucket_counts", "aggregation_temporality",
            "flags", "min_value", "max_value", "attributes_json"
        };

        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add(new object?[]
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

        await BulkInsertAsync(conn, tx, "exponential_histogram_data_points", columns, values, ct);
    }

    private static async Task BulkInsertSummaryDataPointsAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        List<(long MetricId, SummaryDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var columns = new[]
        {
            "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
            "quantile_values", "flags", "attributes_json"
        };

        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add(new object?[]
            {
                metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, d.Sum,
                (object?)SerializeJsonOrNull(d.QuantileValues) ?? DBNull.Value,
                d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value
            });

        await BulkInsertAsync(conn, tx, "summary_data_points", columns, values, ct);
    }

    // =========================================================================
    // PROVIDER-LOCAL HELPERS
    // =========================================================================

    // Builds and executes chunked, parameterized multi-row INSERT statements for every
    // high-volume table except spans (which has its own id-recovering path above).
    //
    // Every full-size chunk has the IDENTICAL shape -- same column count, same row count, same
    // placeholder layout -- so the command is built and Prepare()'d ONCE and then reused across
    // every full chunk, just overwriting each parameter's Value in place, instead of rebuilding
    // the SQL text and allocating a fresh MySqlCommand + ChunkSize*columns.Length MySqlParameters
    // for every chunk. Prepare() also lets the server itself skip re-parsing/re-planning a shape
    // it has already seen earlier in this same connection. Only the final, possibly-short,
    // remainder chunk falls back to an ad hoc unprepared command, since it is a one-off shape by
    // definition and not worth preparing.
    private static async Task BulkInsertAsync(
        MySqlConnection conn, MySqlTransaction tx, string table, string[] columns,
        List<object?[]> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;

        var colList = string.Join(", ", columns);
        var fullChunks = rows.Count / ChunkSize;

        if (fullChunks > 0)
        {
            var sql = BuildInsertSql(table, colList, columns.Length, ChunkSize);
            await using var cmd = new MySqlCommand(sql, conn) { Transaction = tx };
            for (var r = 0; r < ChunkSize; r++)
                for (var c = 0; c < columns.Length; c++)
                    cmd.Parameters.Add(new MySqlParameter($"@r{r}c{c}", null));
            await cmd.PrepareAsync(ct);

            for (var chunk = 0; chunk < fullChunks; chunk++)
            {
                var offset = chunk * ChunkSize;
                for (var r = 0; r < ChunkSize; r++)
                {
                    var row = rows[offset + r];
                    for (var c = 0; c < columns.Length; c++)
                        cmd.Parameters[r * columns.Length + c].Value = row[c] ?? DBNull.Value;
                }
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        var remainder = rows.Count - fullChunks * ChunkSize;
        if (remainder == 0) return;

        var remainderOffset = fullChunks * ChunkSize;
        var remainderSql = BuildInsertSql(table, colList, columns.Length, remainder);
        await using var remainderCmd = new MySqlCommand(remainderSql, conn) { Transaction = tx };
        for (var r = 0; r < remainder; r++)
        {
            var row = rows[remainderOffset + r];
            for (var c = 0; c < columns.Length; c++)
                remainderCmd.Parameters.AddWithValue($"@r{r}c{c}", row[c] ?? DBNull.Value);
        }
        await remainderCmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildInsertSql(string table, string colList, int columnCount, int rowCount)
    {
        var sb = new StringBuilder("INSERT INTO ").Append(table).Append(" (").Append(colList).Append(") VALUES ");
        for (var r = 0; r < rowCount; r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append('(');
            for (var c = 0; c < columnCount; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append('@').Append('r').Append(r).Append('c').Append(c);
            }
            sb.Append(')');
        }
        return sb.ToString();
    }

    // Boxes a nullable value type for ADO.NET, mapping null to DBNull.
    private static object BoxOrNull<T>(T? value) where T : struct
        => value.HasValue ? value.Value : DBNull.Value;
}
