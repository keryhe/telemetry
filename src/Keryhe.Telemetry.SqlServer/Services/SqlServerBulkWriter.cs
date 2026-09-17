using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data;
using static Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SqlServer implementation of <see cref="ITelemetryBulkWriter"/>. Owns only the
/// dialect-specific flush logic — <see cref="SqlBulkCopy"/> for the high-volume tables,
/// staging-table <c>MERGE ... OUTPUT</c> for spans, and <c>MERGE ... WITH (HOLDLOCK)</c>
/// upserts for resource/scope dedup. The channel-draining loop and the
/// normalization/hashing helpers live in <c>Keryhe.Telemetry.Core.Data</c>.
///
/// Every <see cref="SqlBulkCopy"/> call site streams rows through <see cref="ArrayDataReader"/>
/// (a minimal <see cref="IDataReader"/> over a plain <c>List&lt;object?[]&gt;</c>) instead of
/// building a <see cref="DataTable"/> first -- no DataRow/DataColumn change-tracking overhead for
/// data that is only ever written once, streamed straight through. Every call site also goes
/// through <see cref="CreateBulkCopy"/>, which sets <c>BatchSize</c>, <c>TableLock</c> (safe here
/// because the copy already runs inside this flush's own transaction) and a
/// <c>BulkCopyTimeout</c> above the 30s default, instead of each of the seven call sites
/// constructing its own bare <see cref="SqlBulkCopy"/> with server defaults. <c>#spans_stage</c>
/// additionally declares <c>PRIMARY KEY CLUSTERED (trace_id, span_id)</c> matching the MERGE's own
/// join predicate, so the MERGE gets a pre-sorted source instead of sorting or hashing a heap (plus
/// a plan recompile) on every flush.
///
/// Each <c>Flush*Async</c> runs inside a single transaction, so a failure partway through
/// leaves zero rows from that batch rather than a partially-applied flush. Unlike Npgsql,
/// <c>SqlClient</c> requires every command -- and every <see cref="SqlBulkCopy"/> -- to be
/// explicitly enlisted in the transaction, or it throws at execution time; every
/// <c>SqlCommand</c> below sets <c>Transaction = tx</c>, and every <c>SqlBulkCopy</c> is
/// constructed with the transaction passed in directly, including the bulk copy into
/// <c>#spans_stage</c>, a temp table that must live in the same transaction as the
/// <c>MERGE</c> that reads it. The transaction is never explicitly rolled back:
/// <c>await using</c> disposes it without a matching <c>CommitAsync</c> whenever an
/// exception propagates out of the block, and disposing an uncommitted
/// <see cref="SqlTransaction"/> rolls it back.
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
public sealed class SqlServerBulkWriter(
    IConfiguration configuration,
    ResourceScopeCache cache,
    ILogger<SqlServerBulkWriter> logger) : ITelemetryBulkWriter
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
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

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
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
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            var postCommitCacheWrites = new List<Action>();
            var resourceIds = await ResolveResourcesAsync(conn, tx, metrics.Select(m => m.Resource), postCommitCacheWrites, ct);
            var scopeIds    = await ResolveScopesAsync(conn, tx, metrics.Select(m => m.InstrumentationScope), postCommitCacheWrites, ct);

            var metricIds = await ResolveMetricIdsAsync(conn, tx, metrics, resourceIds, scopeIds, postCommitCacheWrites, ct);

            // Group data points by target table across the WHOLE batch, attaching each row's already
            // -resolved metric_id as it is grouped. This is what turns a metric flush into AT MOST
            // FIVE SqlBulkCopy calls instead of one per metric: a naive per-metric loop here defeats
            // the whole point of batching (up to MaxMetricFlushBatchSize round trips per flush).
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
        SqlConnection conn,
        SqlTransaction tx,
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
        SqlConnection conn,
        SqlTransaction tx,
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

    // MERGE upserts when not matched; the trailing SELECT always returns the ID whether
    // the row was just inserted or already existed.  HOLDLOCK prevents race conditions
    // where two concurrent MERGEs both see NOT MATCHED and both attempt an insert.
    private static async Task<long> UpsertResourceAsync(
        SqlConnection conn, SqlTransaction tx, ResourceModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            MERGE resources WITH (HOLDLOCK) AS t
            USING (SELECT @hash AS resource_hash, @tenantId AS tenant_id) AS s
            ON t.resource_hash = s.resource_hash AND t.tenant_id = s.tenant_id
            WHEN NOT MATCHED THEN
                INSERT (attributes_json, created_at, resource_hash, schema_url, tenant_id)
                VALUES (@attrJson, SYSDATETIME(), @hash, @schemaUrl, @tenantId);
            SELECT id FROM resources WHERE resource_hash = @hash AND tenant_id = @tenantId;
            """;

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tenantId",  model.TenantId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> UpsertScopeAsync(
        SqlConnection conn, SqlTransaction tx, InstrumentationScopeModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            MERGE instrumentation_scopes WITH (HOLDLOCK) AS t
            USING (SELECT @hash AS scope_hash) AS s
            ON t.scope_hash = s.scope_hash
            WHEN NOT MATCHED THEN
                INSERT (name, version, schema_url, scope_hash, created_at, attributes_json)
                VALUES (@name, @version, @schemaUrl, @hash, SYSDATETIME(), @attrJson);
            SELECT id FROM instrumentation_scopes WHERE scope_hash = @hash;
            """;

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@name",      model.Name);
        cmd.Parameters.AddWithValue("@version",   (object?)model.Version   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    // =========================================================================
    // BULK INSERT: LOGS
    // =========================================================================

    private static readonly string[] LogColumns =
    [
        "resource_id", "scope_id", "time_unix_nano", "observed_time_unix_nano",
        "severity_number", "severity_text", "body_type", "body_value",
        "dropped_attributes_count", "flags", "trace_id", "span_id", "attributes_json", "event_name"
    ];

    private static async Task BulkInsertLogsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<LogRecordModel> records,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        var rows = new List<object?[]>(records.Count);
        foreach (var r in records)
            rows.Add(
            [
                resourceIds[ResourceKey(r.Resource)],
                scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))],
                r.TimeUnixNano ?? 0L,
                BoxOrNull(r.ObservedTimeUnixNano),
                BoxOrNull(r.SeverityNumber),
                r.SeverityText,
                r.BodyType?.ToString(),
                r.BodyValue,
                r.DroppedAttributesCount,
                r.Flags,
                r.TraceIdHex,
                r.SpanIdHex,
                SerializeJsonOrNull(r.Attributes),
                r.EventName
            ]);

        using var bulk = CreateBulkCopy(conn, tx, "log_records");
        for (var i = 0; i < LogColumns.Length; i++)
            bulk.ColumnMappings.Add(i, LogColumns[i]);
        using var reader = new ArrayDataReader(rows);
        await bulk.WriteToServerAsync(reader, ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static readonly string[] SpanStageColumns =
    [
        "trace_id", "span_id", "parent_span_id", "resource_id", "scope_id",
        "name", "kind", "start_time_unix_nano", "end_time_unix_nano",
        "dropped_attributes_count", "dropped_events_count", "dropped_links_count",
        "trace_state", "status_code", "status_message", "attributes_json", "flags"
    ];

    private static async Task<Dictionary<(string TraceId, string SpanId), long>> BulkInsertSpansAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<SpanModel> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // Stage spans into a temp table, then MERGE from it so we get OUTPUT rows
        // only for newly inserted spans (matching ON CONFLICT DO NOTHING + RETURNING).
        // The temp table lives only for the lifetime of this connection's session, so its
        // creation, the bulk copy into it, and the MERGE that reads it must all run inside
        // the same transaction as everything else in this flush.
        //
        // PRIMARY KEY CLUSTERED (trace_id, span_id) matches the MERGE's own join predicate: without
        // it, #spans_stage is a heap and the MERGE has to sort or hash it every flush (plus a plan
        // recompile) to match it against spans.uk_trace_span. Declaring the clustered key up front
        // gives the MERGE a pre-sorted source, at the cost of the bulk copy now inserting in key
        // order rather than append order -- a fine trade for a temp table that only ever exists to
        // be joined once and then dropped.
        await using (var createCmd = new SqlCommand("""
            CREATE TABLE #spans_stage (
                trace_id                 CHAR(32)      NOT NULL,
                span_id                  CHAR(16)      NOT NULL,
                parent_span_id           CHAR(16),
                resource_id              BIGINT        NOT NULL,
                scope_id                 BIGINT        NOT NULL,
                name                     NVARCHAR(255) NOT NULL,
                kind                     NVARCHAR(20)  NOT NULL,
                start_time_unix_nano     BIGINT        NOT NULL,
                end_time_unix_nano       BIGINT        NOT NULL,
                dropped_attributes_count INT           NOT NULL,
                dropped_events_count     INT           NOT NULL,
                dropped_links_count      INT           NOT NULL,
                trace_state              NVARCHAR(MAX),
                status_code              NVARCHAR(20)  NOT NULL,
                status_message           NVARCHAR(MAX),
                attributes_json          NVARCHAR(MAX),
                flags                    INT           NOT NULL,
                CONSTRAINT pk_spans_stage PRIMARY KEY CLUSTERED (trace_id, span_id)
            )
            """, conn, tx))
        {
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        var stageRows = new List<object?[]>(spans.Count);
        foreach (var span in spans)
            stageRows.Add(
            [
                span.TraceIdHex,
                span.SpanIdHex,
                span.ParentSpanIdHex,
                resourceIds[ResourceKey(span.Resource)],
                scopeIds[HashScope(NormalizeScope(span.InstrumentationScope))],
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
            ]);

        using (var bulk = CreateBulkCopy(conn, tx, "#spans_stage"))
        {
            for (var i = 0; i < SpanStageColumns.Length; i++)
                bulk.ColumnMappings.Add(i, SpanStageColumns[i]);
            using var stageReader = new ArrayDataReader(stageRows);
            await bulk.WriteToServerAsync(stageReader, ct);
        }

        const string mergeSql = """
            MERGE spans AS target
            USING #spans_stage AS source
            ON target.trace_id = source.trace_id AND target.span_id = source.span_id
            WHEN NOT MATCHED THEN
                INSERT (trace_id, span_id, parent_span_id, resource_id, scope_id,
                        name, kind, start_time_unix_nano, end_time_unix_nano,
                        dropped_attributes_count, dropped_events_count, dropped_links_count,
                        trace_state, status_code, status_message, created_at, attributes_json, flags)
                VALUES (source.trace_id, source.span_id, source.parent_span_id,
                        source.resource_id, source.scope_id,
                        source.name, source.kind, source.start_time_unix_nano, source.end_time_unix_nano,
                        source.dropped_attributes_count, source.dropped_events_count, source.dropped_links_count,
                        source.trace_state, source.status_code, source.status_message,
                        SYSDATETIME(), source.attributes_json, source.flags)
            OUTPUT INSERTED.id, INSERTED.trace_id, INSERTED.span_id;
            """;

        var inserted = new Dictionary<(string, string), long>();
        await using var mergeCmd = new SqlCommand(mergeSql, conn, tx);
        await using var reader = await mergeCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            inserted[(reader.GetString(1), reader.GetString(2))] = reader.GetInt64(0);

        return inserted;
    }

    private static readonly string[] SpanEventColumns =
        ["span_id", "name", "time_unix_nano", "dropped_attributes_count", "attributes_json"];

    private static async Task BulkInsertSpanEventsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long SpanDbId, SpanEventModel Event)> events,
        CancellationToken ct)
    {
        var rows = new List<object?[]>(events.Count);
        foreach (var (spanId, e) in events)
            rows.Add([spanId, e.Name, e.TimeUnixNano, e.DroppedAttributesCount, SerializeJsonOrNull(e.Attributes)]);

        using var bulk = CreateBulkCopy(conn, tx, "span_events");
        for (var i = 0; i < SpanEventColumns.Length; i++)
            bulk.ColumnMappings.Add(i, SpanEventColumns[i]);
        using var reader = new ArrayDataReader(rows);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly string[] SpanLinkColumns =
        ["span_id", "linked_trace_id", "linked_span_id", "trace_state", "dropped_attributes_count", "attributes_json", "flags"];

    private static async Task BulkInsertSpanLinksAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long SpanDbId, SpanLinkModel Link)> links,
        CancellationToken ct)
    {
        var rows = new List<object?[]>(links.Count);
        foreach (var (spanId, l) in links)
            rows.Add([spanId, l.LinkedTraceIdHex, l.LinkedSpanIdHex, l.TraceState, l.DroppedAttributesCount, SerializeJsonOrNull(l.Attributes), l.Flags]);

        using var bulk = CreateBulkCopy(conn, tx, "span_links");
        for (var i = 0; i < SpanLinkColumns.Length; i++)
            bulk.ColumnMappings.Add(i, SpanLinkColumns[i]);
        using var reader = new ArrayDataReader(rows);
        await bulk.WriteToServerAsync(reader, ct);
    }

    // =========================================================================
    // RESOLVE: METRICS (individually, to capture the IDENTITY-generated IDs)
    // =========================================================================
    // metrics is a reference table deduplicated on (resource_id, name, type, scope_id).
    // Mirrors ResolveResourcesAsync: dedup within the batch, consult the process cache,
    // upsert only what is left, cache the result. SqlBulkCopy still cannot return generated
    // ids, so this stays row-at-a-time -- but a warm process now issues zero statements here,
    // so the row-at-a-time cost is paid once per process rather than once per export.

    private async Task<long[]> ResolveMetricIdsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        List<Action> postCommitCacheWrites,
        CancellationToken ct)
    {
        // HOLDLOCK for the same reason UpsertResourceAsync needs it: without it two concurrent
        // MERGEs can both see NOT MATCHED and both try to insert. The trailing SELECT returns the
        // id whether the row was just inserted or already existed.
        const string sql = """
            MERGE metrics WITH (HOLDLOCK) AS t
            USING (SELECT @resourceId AS resource_id, @scopeId AS scope_id,
                          @name AS name, @type AS [type]) AS s
               ON t.resource_id = s.resource_id
              AND t.scope_id    = s.scope_id
              AND t.name        = s.name
              AND t.[type]      = s.[type]
            WHEN MATCHED THEN
                UPDATE SET description = @description, unit = @unit
            WHEN NOT MATCHED THEN
                INSERT (resource_id, scope_id, name, description, unit, [type], created_at)
                VALUES (@resourceId, @scopeId, @name, @description, @unit, @type, SYSDATETIME());
            SELECT id FROM metrics
             WHERE resource_id = @resourceId AND scope_id = @scopeId
               AND name = @name AND [type] = @type;
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
            // metric recurs many times and would otherwise cost one MERGE round trip each.
            if (pendingKeys.Add(keys[i]))
                pending.Add((keys[i], resId, scoId, m));
        }

        // Deterministic lock order, so two collectors upserting the same key set cannot deadlock.
        pending.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        foreach (var (key, resId, scoId, m) in pending)
        {
            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add("@resourceId", SqlDbType.BigInt).Value = resId;
            cmd.Parameters.Add("@scopeId",    SqlDbType.BigInt).Value = scoId;
            // Lengths are declared to match the columns so the plan cache does not get an entry
            // per distinct string length.
            cmd.Parameters.Add("@name",        SqlDbType.NVarChar, 255).Value = m.Name;
            cmd.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value  = (object?)m.Description ?? DBNull.Value;
            cmd.Parameters.Add("@unit",        SqlDbType.NVarChar, 63).Value  = (object?)m.Unit ?? DBNull.Value;
            cmd.Parameters.Add("@type",        SqlDbType.NVarChar, 30).Value  = m.Type.ToString();

            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
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

    private static readonly string[] GaugeColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int",
        "flags", "attributes_json", "exemplars_json"
    ];

    private static async Task BulkInsertGaugeDataPointsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long MetricId, GaugeDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add([metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt), d.Flags,
                SerializeJsonOrNull(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

        using var bulk = CreateBulkCopy(conn, tx, "gauge_data_points");
        for (var i = 0; i < GaugeColumns.Length; i++)
            bulk.ColumnMappings.Add(i, GaugeColumns[i]);
        using var reader = new ArrayDataReader(values);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly string[] SumColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "value_double", "value_int",
        "aggregation_temporality", "is_monotonic", "flags", "attributes_json", "exemplars_json"
    ];

    private static async Task BulkInsertSumDataPointsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long MetricId, SumDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add([metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt),
                d.AggregationTemporality.ToString(), d.IsMonotonic, d.Flags,
                SerializeJsonOrNull(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

        using var bulk = CreateBulkCopy(conn, tx, "sum_data_points");
        for (var i = 0; i < SumColumns.Length; i++)
            bulk.ColumnMappings.Add(i, SumColumns[i]);
        using var reader = new ArrayDataReader(values);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly string[] HistogramColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
        "bucket_counts", "explicit_bounds", "aggregation_temporality", "flags",
        "min_value", "max_value", "attributes_json", "exemplars_json"
    ];

    private static async Task BulkInsertHistogramDataPointsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long MetricId, HistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add([metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum),
                SerializeJsonOrNull(d.BucketCounts), SerializeJsonOrNull(d.ExplicitBounds),
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                SerializeJsonOrNull(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

        using var bulk = CreateBulkCopy(conn, tx, "histogram_data_points");
        for (var i = 0; i < HistogramColumns.Length; i++)
            bulk.ColumnMappings.Add(i, HistogramColumns[i]);
        using var reader = new ArrayDataReader(values);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly string[] ExpHistogramColumns =
    [
        "metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value",
        "scale", "zero_count", "positive_offset", "positive_bucket_counts",
        "negative_offset", "negative_bucket_counts", "aggregation_temporality",
        "flags", "min_value", "max_value", "attributes_json", "exemplars_json"
    ];

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long MetricId, ExponentialHistogramDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add([metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum), d.Scale, d.ZeroCount,
                BoxOrNull(d.PositiveOffset), SerializeJsonOrNull(d.PositiveBucketCounts),
                BoxOrNull(d.NegativeOffset), SerializeJsonOrNull(d.NegativeBucketCounts),
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                SerializeJsonOrNull(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

        using var bulk = CreateBulkCopy(conn, tx, "exponential_histogram_data_points");
        for (var i = 0; i < ExpHistogramColumns.Length; i++)
            bulk.ColumnMappings.Add(i, ExpHistogramColumns[i]);
        using var reader = new ArrayDataReader(values);
        await bulk.WriteToServerAsync(reader, ct);
    }

    private static readonly string[] SummaryColumns =
        ["metric_id", "start_time_unix_nano", "time_unix_nano", "count", "sum_value", "quantile_values", "flags", "attributes_json"];

    private static async Task BulkInsertSummaryDataPointsAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<(long MetricId, SummaryDataPointModel DataPoint)> rows, CancellationToken ct)
    {
        var values = new List<object?[]>(rows.Count);
        foreach (var (metricId, d) in rows)
            values.Add([metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, d.Sum, SerializeJsonOrNull(d.QuantileValues), d.Flags, SerializeJsonOrNull(d.Attributes)]);

        using var bulk = CreateBulkCopy(conn, tx, "summary_data_points");
        for (var i = 0; i < SummaryColumns.Length; i++)
            bulk.ColumnMappings.Add(i, SummaryColumns[i]);
        using var reader = new ArrayDataReader(values);
        await bulk.WriteToServerAsync(reader, ct);
    }

    // =========================================================================
    // PROVIDER-LOCAL HELPERS
    // =========================================================================

    // Boxes a nullable value type for ADO.NET, mapping null to DBNull.
    private static object BoxOrNull<T>(T? value) where T : struct
        => value.HasValue ? (object)value.Value : DBNull.Value;

    // Every SqlBulkCopy call site shares these: BatchSize caps how many rows SQL Server commits
    // per internal batch instead of the whole call as one giant implicit batch (bounds log/lock
    // growth on a large flush); TableLock takes a bulk-update table lock instead of row locks,
    // which is faster for a bulk load and safe here because every copy already runs inside this
    // flush's own transaction, so there is no cross-flush contention to protect against; and the
    // default 30s BulkCopyTimeout is too tight for a large batch on a loaded server.
    private const int BulkCopyBatchSize = 2_000;
    private const int BulkCopyTimeoutSeconds = 120;

    private static SqlBulkCopy CreateBulkCopy(SqlConnection conn, SqlTransaction tx, string destinationTable) => new(
        conn, SqlBulkCopyOptions.TableLock, tx)
    {
        DestinationTableName = destinationTable,
        BatchSize = BulkCopyBatchSize,
        BulkCopyTimeout = BulkCopyTimeoutSeconds
    };

    // Streams rows from a `List<object?[]>` straight into SqlBulkCopy without ever building a
    // DataTable: no DataRow/DataColumn change-tracking or boxing-into-a-second-container
    // overhead for what is already a plain in-memory array per row. Columns are mapped by
    // ordinal (source index i -> destinationColumns[i]), so this reader implements only what
    // SqlBulkCopy's IDataReader path actually calls -- GetValues/GetValue/IsDBNull/Read/
    // FieldCount -- and leaves the rest of IDataReader/IDataRecord throwing NotSupportedException,
    // which is the idiomatic shape for a write-only adapter like this one.
    private sealed class ArrayDataReader(IReadOnlyList<object?[]> rows) : IDataReader
    {
        private int _index = -1;

        public int FieldCount => rows.Count > 0 ? rows[0].Length : 0;

        public bool Read()
        {
            _index++;
            return _index < rows.Count;
        }

        public object GetValue(int i) => rows[_index][i] ?? DBNull.Value;

        public int GetValues(object[] values)
        {
            var row = rows[_index];
            var n = Math.Min(values.Length, row.Length);
            for (var i = 0; i < n; i++) values[i] = row[i] ?? DBNull.Value;
            return n;
        }

        public bool IsDBNull(int i) => rows[_index][i] is null;

        public object this[int i] => GetValue(i);
        public object this[string name] => throw new NotSupportedException();

        public int Depth => 0;
        public bool IsClosed => false;
        public int RecordsAffected => -1;

        public void Close() { }
        public void Dispose() { }
        public bool NextResult() => false;
        public DataTable? GetSchemaTable() => null;

        public string GetName(int i) => throw new NotSupportedException();
        public int GetOrdinal(string name) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => throw new NotSupportedException();
        public Type GetFieldType(int i) => throw new NotSupportedException();

        public bool GetBoolean(int i) => (bool)GetValue(i);
        public byte GetByte(int i) => (byte)GetValue(i);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => (char)GetValue(i);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
        public decimal GetDecimal(int i) => (decimal)GetValue(i);
        public double GetDouble(int i) => (double)GetValue(i);
        public float GetFloat(int i) => (float)GetValue(i);
        public Guid GetGuid(int i) => (Guid)GetValue(i);
        public short GetInt16(int i) => (short)GetValue(i);
        public int GetInt32(int i) => (int)GetValue(i);
        public long GetInt64(int i) => (long)GetValue(i);
        public string GetString(int i) => (string)GetValue(i);
    }
}
