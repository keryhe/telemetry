using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data;
using static Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.PostgreSQL.Services;

/// <summary>
/// PostgreSQL (plain) implementation of <see cref="ITelemetryBulkWriter"/>. Owns
/// only the Npgsql bulk path. The channel-draining loop and the normalization/hashing
/// helpers live in <c>Keryhe.Telemetry.Core.Data</c>. The hypertable nature of the target
/// tables is transparent to these inserts.
///
/// Two insert shapes, chosen per table by whether it needs conflict handling:
/// - <b>Binary <c>COPY</c></b> (<c>NpgsqlBinaryImporter</c>, via <c>BeginBinaryImportAsync</c>) for
///   every table that is a pure append with no dedup key to violate: <c>log_records</c> and the
///   five metric data-point tables. This is
///   Npgsql's fastest bulk-load path, and safe here specifically because none of these tables
///   can raise a conflict -- COPY has no <c>ON CONFLICT</c> equivalent, so it is NOT used for
///   <c>spans</c>, where a re-delivered span hitting <c>uk_trace_span</c> must be silently
///   skipped, not thrown.
/// - <b><c>unnest()</c> array expansion with <c>ON CONFLICT</c></b> for every table
///   that dedups: <c>spans</c> (DO NOTHING, re-delivery is expected and must not error),
///   <c>resources</c>/<c>instrumentation_scopes</c>/<c>metrics</c> (DO UPDATE, so RETURNING fires
///   for both the newly-inserted AND the already-existing half of the batch in one round trip
///   -- avoiding a DO-NOTHING-then-separate-SELECT fallback for the conflicting rows).
///
/// Both shapes run inside the connection's ambient transaction without explicit enlistment
/// (confirmed live for COPY specifically, not just ordinary commands: a COPY that runs inside a
/// flush that later fails rolls back with everything else, the same as the unnest form it
/// replaced) -- Npgsql tracks it automatically, unlike <c>SqlClient</c>.
///
/// <b>Resource/scope/metric upserts run BEFORE the data transaction opens</b>, each as its own
/// separately-committed statement against the same connection, rather than inside the same
/// transaction as the bulk data insert. This used to be one transaction end to end, with
/// <see cref="ResourceScopeCache"/> writes deferred until after commit to avoid caching an id
/// that a later failure in the same flush would roll back. That shape deadlocked under
/// concurrent flushes: on Timescale specifically, inserting into a time range with no existing
/// chunk creates the chunk inside the same transaction, and attaching that chunk's foreign keys
/// takes a lock on <c>resources</c>/<c>instrumentation_scopes</c> that conflicts with the
/// upsert's own row lock on those tables -- two flushes, one creating a chunk while holding an
/// upsert lock the other needs and vice versa, deadlock. Resolving the upserts first, each in
/// its own auto-committed statement, means the data transaction that goes on to (maybe) create a
/// chunk never itself holds a lock on the reference tables.
///
/// A consequence: an upserted resource, scope, or metric-catalog row can survive even when the
/// data insert that needed it fails and the batch is retried. That is fine -- these are
/// deduplicated reference rows (<c>ON CONFLICT DO UPDATE</c>), so the retry's upsert simply finds
/// the row it already wrote via <c>RETURNING</c> instead of inserting a duplicate. Because each
/// upsert is already committed by the time it returns an id, <see cref="ResourceScopeCache"/> is
/// populated immediately rather than deferred -- there is no longer a same-flush rollback that
/// could invalidate it.
///
/// The data transaction itself is unaffected: it still wraps only the bulk insert into
/// <c>log_records</c>/<c>spans</c>/the data-point tables, so a failure there still leaves zero
/// data rows for that batch. It is never explicitly rolled back: <c>await using</c> disposes it
/// without a matching <c>CommitAsync</c> whenever an exception propagates out of the block, and
/// disposing an uncommitted <see cref="NpgsqlTransaction"/> rolls it back. Every command
/// enlisted in it sets <c>Transaction = tx</c> even though Npgsql's own docs say this is not
/// strictly required (unlike <c>SqlClient</c>, it tracks the connection's ambient transaction
/// automatically) -- explicit enlistment costs nothing and keeps every provider in this codebase
/// following the same, unambiguous pattern. The upsert statements, running before the
/// transaction exists, pass no <c>Transaction</c> at all and commit on their own.
/// </summary>
public sealed class PostgreSqlBulkWriter(
    NpgsqlDataSource dataSource,
    ResourceScopeCache cache,
    ILogger<PostgreSqlBulkWriter> logger) : ITelemetryBulkWriter
{
    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await BulkInsertLogsAsync(conn, tx, records, resourceIds, scopeIds, ct);
        await tx.CommitAsync(ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    public async Task FlushTracesAsync(List<SpanModel> spans, CancellationToken ct = default)
    {
        if (spans.Count == 0) return;

        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, spans.Select(s => s.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, spans.Select(s => s.InstrumentationScope), ct);

        await using var tx = await conn.BeginTransactionAsync(ct);
        await BulkInsertSpansAsync(conn, tx, spans, resourceIds, scopeIds, ct);
        await tx.CommitAsync(ct);
        logger.LogDebug("Flushed {SpanCount} spans", spans.Count);
    }

    // =========================================================================
    // FLUSH: METRICS
    // =========================================================================

    public async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, metrics.Select(m => m.Resource), ct);
        var scopeIds = await ResolveScopesAsync(conn, metrics.Select(m => m.InstrumentationScope), ct);
        var metricIds = await ResolveMetricIdsAsync(conn, metrics, resourceIds, scopeIds, ct);

        // Group data points by target table across the WHOLE batch, attaching each row's already
        // -resolved metric_id as it is grouped. This is what turns a metric flush into AT MOST
        // FIVE bulk inserts instead of one per metric: a naive per-metric loop here defeats the
        // whole point of batching (up to MaxMetricFlushBatchSize round trips per flush), and on
        // ClickHouse it also explodes into one tiny part per metric.
        var gaugeRows = new List<(long MetricId, GaugeDataPointModel DataPoint)>();
        var sumRows = new List<(long MetricId, SumDataPointModel DataPoint)>();
        var histogramRows = new List<(long MetricId, HistogramDataPointModel DataPoint)>();
        var expHistogramRows = new List<(long MetricId, ExponentialHistogramDataPointModel DataPoint)>();
        var summaryRows = new List<(long MetricId, SummaryDataPointModel DataPoint)>();

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
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

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (gaugeRows.Count > 0) await BulkInsertGaugeDataPointsAsync(conn, tx, gaugeRows, ct);
        if (sumRows.Count > 0) await BulkInsertSumDataPointsAsync(conn, tx, sumRows, ct);
        if (histogramRows.Count > 0) await BulkInsertHistogramDataPointsAsync(conn, tx, histogramRows, ct);
        if (expHistogramRows.Count > 0) await BulkInsertExpHistogramDataPointsAsync(conn, tx, expHistogramRows, ct);
        if (summaryRows.Count > 0) await BulkInsertSummaryDataPointsAsync(conn, tx, summaryRows, ct);

        await tx.CommitAsync(ct);
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

        if (pending.Count > 0)
            await UpsertResourcesAsync(conn, pending, result, cache, ct);

        return result;
    }

    // Batches every distinct cold-start resource in this flush into ONE round trip instead of
    // one UpsertResourceAsync call per resource (this is what most flushes hit, since a warm
    // process resolves everything from the cache and never reaches here at all -- but the FIRST
    // flush after a process start, or one touching brand-new resources, previously paid one round
    // trip per distinct resource). DO UPDATE rather than DO NOTHING so RETURNING fires for
    // conflicting rows too, one output row per input row -- same reasoning as ResolveMetricIdsAsync,
    // and the same reason `pending` must already be deduped by key before this runs: PostgreSQL
    // raises 21000 if one statement's ON CONFLICT DO UPDATE touches the same conflict target twice.
    // Runs BEFORE the data transaction opens and commits on its own -- see the class doc
    // comment for why, and note this means `cache` is populated immediately below rather than
    // deferred: there is no same-flush rollback left that could invalidate it.
    private static async Task UpsertResourcesAsync(
        NpgsqlConnection conn,
        Dictionary<string, (string Hash, ResourceModel Model)> pending,
        Dictionary<string, long> result, ResourceScopeCache cache, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO resources (attributes_json, created_at, resource_hash, schema_url, tenant_id)
            SELECT unnest($1::jsonb[]), NOW(), unnest($2::text[]), unnest($3::text[]), unnest($4::bigint[])
            ON CONFLICT (tenant_id, resource_hash) DO UPDATE
                SET schema_url = EXCLUDED.schema_url
            RETURNING id, tenant_id, resource_hash
            """;

        // Deterministic lock order, so two collectors upserting the same key set cannot deadlock:
        // unnest() feeds rows to the INSERT in array order, which is the order row locks are taken.
        var entries = pending.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
        var n = entries.Count;
        var attrs = new string?[n];
        var hashes = new string[n];
        var schemaUrls = new string?[n];
        var tenantIds = new long[n];
        for (var i = 0; i < n; i++)
        {
            attrs[i] = SerializeDeterministicJson(entries[i].Model.Attributes);
            hashes[i] = entries[i].Hash;
            schemaUrls[i] = entries[i].Model.SchemaUrl;
            tenantIds[i] = entries[i].Model.TenantId;
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = schemaUrls, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = tenantIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var tenantId = reader.GetInt64(1);
            var hash = reader.GetString(2);
            var key = ResourceKey(tenantId, hash);
            result[key] = id;
            cache.SetResource(tenantId, hash, id);
        }
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

        if (pending.Count > 0)
            await UpsertScopesAsync(conn, pending, result, cache, ct);

        return result;
    }

    // Batched cold-start scope upsert -- see UpsertResourcesAsync, same reasoning, including
    // running before the data transaction and caching immediately.
    private static async Task UpsertScopesAsync(
        NpgsqlConnection conn,
        Dictionary<string, InstrumentationScopeModel> pending,
        Dictionary<string, long> result, ResourceScopeCache cache, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO instrumentation_scopes (name, version, schema_url, scope_hash, created_at, attributes_json)
            SELECT unnest($1::text[]), unnest($2::text[]), unnest($3::text[]), unnest($4::text[]), NOW(), unnest($5::jsonb[])
            ON CONFLICT (scope_hash) DO UPDATE
                SET schema_url = EXCLUDED.schema_url
            RETURNING id, scope_hash
            """;

        // Deterministic lock order -- see UpsertResourcesAsync.
        var entries = pending.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var n = entries.Count;
        var names = new string[n];
        var versions = new string?[n];
        var schemaUrls = new string?[n];
        var hashes = new string[n];
        var attrs = new string?[n];
        for (var i = 0; i < n; i++)
        {
            var model = entries[i].Value;
            names[i] = model.Name;
            versions[i] = model.Version;
            schemaUrls[i] = model.SchemaUrl;
            hashes[i] = entries[i].Key;
            attrs[i] = SerializeDeterministicJson(model.Attributes);
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = names, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = versions, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = schemaUrls, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = hashes, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = attrs, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var hash = reader.GetString(1);
            result[hash] = id;
            cache.SetScope(hash, id);
        }
    }

    // =========================================================================
    // BULK INSERT: LOGS
    // =========================================================================

    private const string LogsCopySql = """
        COPY log_records (
            resource_id, scope_id, time_unix_nano, observed_time_unix_nano,
            severity_number, severity_text, body_type, body_value,
            dropped_attributes_count, flags, trace_id, span_id, attributes_json, event_name)
        FROM STDIN (FORMAT BINARY)
        """;

    // Binary COPY, not unnest($1::type[], ...) array parameters: log_records has no dedup /
    // ON CONFLICT / RETURNING need (unlike spans), so nothing about COPY's "no conflict handling"
    // restriction applies here -- it is a pure straight-line append, which is exactly what COPY's
    // binary protocol is fastest at. The connection's ambient transaction covers COPY the same way
    // it covers ordinary commands (confirmed live: a COPY inside this flush's transaction rolls
    // back with everything else on failure, same as the unnest form it replaces).
    private static async Task BulkInsertLogsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<LogRecordModel> records,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(LogsCopySql, ct);
        foreach (var r in records)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(resourceIds[ResourceKey(r.Resource)], NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))], NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(r.TimeUnixNano ?? 0L, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, r.ObservedTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, r.SeverityNumber, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, r.SeverityText, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, r.BodyType?.ToString(), NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, r.BodyValue, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(r.DroppedAttributesCount, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(r.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, r.TraceIdHex, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, r.SpanIdHex, NpgsqlDbType.Text, ct);
            await WriteNullableAsync(writer, r.Attributes.Count > 0 ? JsonSerializer.Serialize(r.Attributes) : null, NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, r.EventName, NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static async Task BulkInsertSpansAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<SpanModel> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO spans (
                trace_id, span_id, parent_span_id, resource_id, scope_id,
                name, kind, start_time_unix_nano, end_time_unix_nano,
                dropped_attributes_count, dropped_events_count, dropped_links_count,
                trace_state, status_code, status_message, attributes_json, flags,
                events_json, links_json)
            SELECT
                unnest($1::text[]),   unnest($2::text[]),   unnest($3::text[]),
                unnest($4::bigint[]), unnest($5::bigint[]),
                unnest($6::text[]),   unnest($7::text[]),
                unnest($8::bigint[]), unnest($9::bigint[]),
                unnest($10::int[]),   unnest($11::int[]),   unnest($12::int[]),
                unnest($13::text[]),  unnest($14::text[]),  unnest($15::text[]),
                unnest($16::jsonb[]), unnest($17::int[]),
                unnest($18::jsonb[]), unnest($19::jsonb[])
            ON CONFLICT (trace_id, span_id) DO NOTHING
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
        var eventsJson = new string?[n];
        var linksJson = new string?[n];

        for (var i = 0; i < n; i++)
        {
            var span = spans[i];
            resIds[i] = resourceIds[ResourceKey(span.Resource)];
            scoIds[i] = scopeIds[HashScope(NormalizeScope(span.InstrumentationScope))];
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
            eventsJson[i] = SerializeListOrNull(span.Events);
            linksJson[i] = SerializeListOrNull(span.Links);
        }

        await using var cmd = new NpgsqlCommand(sql, conn) { Transaction = tx };
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
        cmd.Parameters.Add(new NpgsqlParameter { Value = eventsJson, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = linksJson, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });

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

    // Runs BEFORE the data transaction opens and commits on its own -- see the class doc
    // comment for why, and note this means `cache` is populated immediately below rather than
    // deferred: there is no same-flush rollback left that could invalidate it.
    private async Task<long[]> ResolveMetricIdsAsync(
        NpgsqlConnection conn,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // DO UPDATE rather than DO NOTHING so RETURNING fires for conflicting rows too, giving
        // one output row per input row -- the same "WITH ins AS (...) ON CONFLICT DO NOTHING ...
        // UNION ALL SELECT ... LIMIT 1" fallback UpsertResourcesAsync would otherwise need is
        // avoided the same way there.
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

    private const string GaugeCopySql = """
        COPY gauge_data_points (metric_id, start_time_unix_nano, time_unix_nano, value_double, value_int,
                                flags, attributes_json, exemplars_json)
        FROM STDIN (FORMAT BINARY)
        """;

    private static async Task BulkInsertGaugeDataPointsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<(long MetricId, GaugeDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(GaugeCopySql, ct);
        foreach (var (metricId, d) in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(metricId, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.StartTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.TimeUnixNano, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.ValueDouble, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, d.ValueInt, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Attributes), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Exemplars), NpgsqlDbType.Jsonb, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private const string SumCopySql = """
        COPY sum_data_points (metric_id, start_time_unix_nano, time_unix_nano, value_double, value_int,
                              aggregation_temporality, is_monotonic, flags, attributes_json, exemplars_json)
        FROM STDIN (FORMAT BINARY)
        """;

    private static async Task BulkInsertSumDataPointsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<(long MetricId, SumDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(SumCopySql, ct);
        foreach (var (metricId, d) in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(metricId, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.StartTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.TimeUnixNano, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.ValueDouble, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, d.ValueInt, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.AggregationTemporality.ToString(), NpgsqlDbType.Text, ct);
            await writer.WriteAsync(d.IsMonotonic, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(d.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Attributes), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Exemplars), NpgsqlDbType.Jsonb, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private const string HistogramCopySql = """
        COPY histogram_data_points (
            metric_id, start_time_unix_nano, time_unix_nano, count, sum_value,
            bucket_counts, explicit_bounds, aggregation_temporality,
            flags, min_value, max_value, attributes_json, exemplars_json)
        FROM STDIN (FORMAT BINARY)
        """;

    private static async Task BulkInsertHistogramDataPointsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<(long MetricId, HistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(HistogramCopySql, ct);
        foreach (var (metricId, d) in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(metricId, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.StartTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.TimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.Count, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.Sum, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.BucketCounts), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.ExplicitBounds), NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(d.AggregationTemporality.ToString(), NpgsqlDbType.Text, ct);
            await writer.WriteAsync(d.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, d.Min, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, d.Max, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Attributes), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Exemplars), NpgsqlDbType.Jsonb, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private const string ExpHistogramCopySql = """
        COPY exponential_histogram_data_points (
            metric_id, start_time_unix_nano, time_unix_nano, count, sum_value,
            scale, zero_count, positive_offset, positive_bucket_counts,
            negative_offset, negative_bucket_counts,
            aggregation_temporality, flags, min_value, max_value, attributes_json, exemplars_json)
        FROM STDIN (FORMAT BINARY)
        """;

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<(long MetricId, ExponentialHistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(ExpHistogramCopySql, ct);
        foreach (var (metricId, d) in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(metricId, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.StartTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.TimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.Count, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.Sum, NpgsqlDbType.Double, ct);
            await writer.WriteAsync(d.Scale, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(d.ZeroCount, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.PositiveOffset, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.PositiveBucketCounts), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, d.NegativeOffset, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.NegativeBucketCounts), NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(d.AggregationTemporality.ToString(), NpgsqlDbType.Text, ct);
            await writer.WriteAsync(d.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, d.Min, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, d.Max, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Attributes), NpgsqlDbType.Jsonb, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Exemplars), NpgsqlDbType.Jsonb, ct);
        }
        await writer.CompleteAsync(ct);
    }

    private const string SummaryCopySql =
        "COPY summary_data_points (metric_id, start_time_unix_nano, time_unix_nano, count, sum_value, quantile_values, flags, attributes_json) FROM STDIN (FORMAT BINARY)";

    private static async Task BulkInsertSummaryDataPointsAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        List<(long MetricId, SummaryDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        await using var writer = await conn.BeginBinaryImportAsync(SummaryCopySql, ct);
        foreach (var (metricId, d) in rows)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(metricId, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, d.StartTimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.TimeUnixNano, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(d.Count, NpgsqlDbType.Bigint, ct);
            await WriteNullableAsync(writer, (double?)d.Sum, NpgsqlDbType.Double, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.QuantileValues), NpgsqlDbType.Jsonb, ct);
            await writer.WriteAsync(d.Flags, NpgsqlDbType.Integer, ct);
            await WriteNullableAsync(writer, SerializeJsonOrNull(d.Attributes), NpgsqlDbType.Jsonb, ct);
        }
        await writer.CompleteAsync(ct);
    }

    // =========================================================================
    // PROVIDER-LOCAL HELPERS
    // =========================================================================

    // Every binary-COPY writer above needs the same null-vs-value branch for optional columns;
    // NpgsqlBinaryImporter has no single WriteAsync overload that accepts a nullable value type
    // directly, so this is the one place that branch lives instead of being repeated inline at
    // every optional column of every COPY.
    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken ct)
        where T : struct
        => value.HasValue ? writer.WriteAsync(value.Value, type, ct) : writer.WriteNullAsync(ct);

    private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
        => value != null ? writer.WriteAsync(value, type, ct) : writer.WriteNullAsync(ct);
}
