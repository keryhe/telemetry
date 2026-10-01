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
/// dialect-specific flush logic — <see cref="SqlBulkCopy"/> straight into every high-volume table
/// (spans included: since schema 3.0.0 a span is a plain append with no unique key, so there is no
/// staging table and no <c>WHERE NOT EXISTS</c>), and <c>MERGE ... WITH (HOLDLOCK)</c> upserts for
/// resource/scope/metric dedup. The channel-draining loop and the
/// normalization/hashing helpers live in <c>Keryhe.Telemetry.Core.Data</c>.
///
/// Every <see cref="SqlBulkCopy"/> call site streams rows through <see cref="ArrayDataReader"/>
/// (a minimal <see cref="IDataReader"/> over a plain <c>List&lt;object?[]&gt;</c>) instead of
/// building a <see cref="DataTable"/> first -- no DataRow/DataColumn change-tracking overhead for
/// data that is only ever written once, streamed straight through. Every call site also goes
/// through <see cref="CreateBulkCopy"/>, which sets <c>BatchSize</c> and a <c>BulkCopyTimeout</c>
/// above the 30s default, instead of each call site constructing its own bare
/// <see cref="SqlBulkCopy"/> with server defaults. <c>TableLock</c> is never applied -- see
/// <see cref="CreateBulkCopy"/> for why.
///
/// <b>Resource/scope/metric upserts run BEFORE the data transaction opens</b>, each as its own
/// auto-committed statement on the same connection -- the same shape as
/// <c>PostgreSqlBulkWriter</c>, for the same reason. Those upserts are <c>MERGE ... WITH
/// (HOLDLOCK)</c>, i.e. serializable key-range locks; inside the data transaction they were held
/// through the bulk copy until commit, and concurrent flushes (<c>FlushConcurrency</c> loops per
/// signal, times three signals) deadlocked on each other's ranges -- worst on a cold
/// <see cref="ResourceScopeCache"/>, when every loop misses the cache at once. Auto-committed,
/// HOLDLOCK still closes the both-see-NOT-MATCHED insert race, but its range locks now last one
/// statement instead of one flush.
///
/// A consequence: a reference row can outlive a data insert that fails and is retried. That is
/// fine -- they are deduplicated, so the retry's MERGE simply finds the row it already wrote.
/// And because each row is committed by the time its id is returned,
/// <see cref="ResourceScopeCache"/> is populated immediately: there is no longer a same-flush
/// rollback that could leave the cache pointing at an id that was never persisted.
///
/// The data transaction wraps only the bulk inserts, so a failure
/// partway through still leaves zero data rows from that batch. Unlike Npgsql,
/// <c>SqlClient</c> requires every command -- and every <see cref="SqlBulkCopy"/> -- to be
/// explicitly enlisted in the transaction, or it throws at execution time; every
/// data-path <c>SqlCommand</c> below sets <c>Transaction = tx</c>, and every <c>SqlBulkCopy</c> is
/// constructed with the transaction passed in directly. The transaction is never explicitly rolled back:
/// <c>await using</c> disposes it without a matching <c>CommitAsync</c> whenever an
/// exception propagates out of the block, and disposing an uncommitted
/// <see cref="SqlTransaction"/> rolls it back. The upsert commands, running before the
/// transaction exists, pass no transaction and commit on their own.
/// </summary>
public sealed class SqlServerBulkWriter(
    IConfiguration configuration,
    ResourceScopeCache cache,
    MetricTouchTracker metricTouchTracker,
    ILogger<SqlServerBulkWriter> logger) : ITelemetryBulkWriter
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    public async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            await BulkInsertLogsAsync(conn, tx, records, resourceIds, scopeIds, ct);
            await tx.CommitAsync(ct);
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

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, spans.Select(s => s.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, spans.Select(s => s.InstrumentationScope), ct);

        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
            await BulkInsertSpansAsync(conn, tx, spans, resourceIds, scopeIds, ct);

            await tx.CommitAsync(ct);
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

        // Resolved BEFORE the data transaction opens -- see the class doc comment.
        var resourceIds = await ResolveResourcesAsync(conn, metrics.Select(m => m.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, metrics.Select(m => m.InstrumentationScope), ct);
        var metricIds   = await ResolveMetricIdsAsync(conn, metrics, resourceIds, scopeIds, ct);

        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (tx)
        {
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

            // metric_last_seen maintenance (list-pages-server-side plan, Phase 5, decision 27):
            // recorded in-process after commit, flushed by MetricTouchWorker on its own interval.
            TelemetryIngestionHelpers.MarkMetricTouches(metricTouchTracker,
                gaugeRows.Select(r => (r.MetricId, r.DataPoint.TimeUnixNano))
                    .Concat(sumRows.Select(r => (r.MetricId, r.DataPoint.TimeUnixNano)))
                    .Concat(histogramRows.Select(r => (r.MetricId, r.DataPoint.TimeUnixNano)))
                    .Concat(expHistogramRows.Select(r => (r.MetricId, r.DataPoint.TimeUnixNano)))
                    .Concat(summaryRows.Select(r => (r.MetricId, r.DataPoint.TimeUnixNano))));

            logger.LogDebug("Flushed {Count} metrics", metrics.Count);
        }
    }

    // =========================================================================
    // RESOURCE / SCOPE RESOLUTION
    // =========================================================================

    private async Task<Dictionary<string, long>> ResolveResourcesAsync(
        SqlConnection conn,
        IEnumerable<ResourceModel?> resources,
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

        // Deterministic lock order, so two collectors upserting the same key set cannot deadlock.
        foreach (var (key, entry) in pending.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var id = await UpsertResourceAsync(conn, entry.Model, entry.Hash, ct);
            // Safe to cache immediately: the upsert auto-committed -- see the class doc comment.
            cache.SetResource(entry.Model.TenantId, entry.Hash, id);
            result[key] = id;
        }

        return result;
    }

    private async Task<Dictionary<string, long>> ResolveScopesAsync(
        SqlConnection conn,
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

        // Deterministic lock order, so two collectors upserting the same key set cannot deadlock.
        foreach (var (hash, model) in pending.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var id = await UpsertScopeAsync(conn, model, hash, ct);
            cache.SetScope(hash, id);
            result[hash] = id;
        }

        return result;
    }

    // MERGE upserts when not matched; the trailing SELECT always returns the ID whether
    // the row was just inserted or already existed.  HOLDLOCK prevents race conditions
    // where two concurrent MERGEs both see NOT MATCHED and both attempt an insert. Runs outside
    // any transaction, so its key-range locks are released when the statement completes.
    private static async Task<long> UpsertResourceAsync(
        SqlConnection conn, ResourceModel model, string hash, CancellationToken ct)
    {
        const string sql = """
            MERGE resources WITH (HOLDLOCK) AS t
            USING (SELECT @hash AS resource_hash, @tenantId AS tenant_id) AS s
            ON t.resource_hash = s.resource_hash AND t.tenant_id = s.tenant_id
            WHEN NOT MATCHED THEN
                INSERT (attributes_json, created_at, resource_hash, schema_url, tenant_id, service_name)
                VALUES (@attrJson, SYSDATETIME(), @hash, @schemaUrl, @tenantId, @serviceName);
            SELECT id FROM resources WHERE resource_hash = @hash AND tenant_id = @tenantId;
            """;

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tenantId",  model.TenantId);
        // service_name (schema 2.13.3, Phase 7, decision 8) -- see PostgreSqlBulkWriter's
        // identical comment; written only on first insert here, matching this MERGE's existing
        // NOT MATCHED-only shape (it never updates an already-existing resource row).
        cmd.Parameters.AddWithValue("@serviceName", (object?)ExtractServiceName(model.Attributes) ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> UpsertScopeAsync(
        SqlConnection conn, InstrumentationScopeModel model, string hash, CancellationToken ct)
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

        await using var cmd = new SqlCommand(sql, conn);
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
        "dropped_attributes_count", "flags", "trace_id", "span_id", "attributes_json", "event_name",
        "tenant_id", "service_name"
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
        {
            var (tenantId, serviceName) = TenantAndService(r.Resource);
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
                r.EventName,
                tenantId,
                serviceName
            ]);
        }

        using var bulk = CreateBulkCopy(conn, tx, "log_records");
        for (var i = 0; i < LogColumns.Length; i++)
            bulk.ColumnMappings.Add(i, LogColumns[i]);
        using var reader = new ArrayDataReader(rows);
        await bulk.WriteToServerAsync(reader, ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static readonly string[] SpanColumns =
    [
        "trace_id", "span_id", "parent_span_id", "resource_id", "scope_id",
        "name", "kind", "start_time_unix_nano", "end_time_unix_nano",
        "dropped_attributes_count", "dropped_events_count", "dropped_links_count",
        "trace_state", "status_code", "status_message", "attributes_json", "flags",
        "events_json", "links_json", "tenant_id", "service_name"
    ];

    // A plain bulk copy into spans: no unique key to violate (a re-delivered span is stored again
    // and reads tolerate it), no foreign keys, and created_at/id come from the column defaults.
    // Rows are ordered by the clustered key (tenant_id, start_time_unix_nano) so the load appends
    // within each tenant's tail instead of scattering inserts through the index.
    private static async Task BulkInsertSpansAsync(
        SqlConnection conn,
        SqlTransaction tx,
        List<SpanModel> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        var rows = new List<(long TenantId, long Start, object?[] Row)>(spans.Count);
        foreach (var span in spans)
        {
            var (tenantId, serviceName) = TenantAndService(span.Resource);
            rows.Add((tenantId, span.StartTimeUnixNano,
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
                span.Flags,
                SerializeListOrNull(span.Events),
                SerializeListOrNull(span.Links),
                tenantId,
                serviceName
            ]));
        }
        rows.Sort(static (a, b) => a.TenantId != b.TenantId ? a.TenantId.CompareTo(b.TenantId) : a.Start.CompareTo(b.Start));

        using var bulk = CreateBulkCopy(conn, tx, "spans");
        for (var i = 0; i < SpanColumns.Length; i++)
            bulk.ColumnMappings.Add(i, SpanColumns[i]);
        using var reader = new ArrayDataReader(rows.Select(r => r.Row).ToList());
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
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // HOLDLOCK for the same reason UpsertResourceAsync needs it: without it two concurrent
        // MERGEs can both see NOT MATCHED and both try to insert. The trailing SELECT returns the
        // id whether the row was just inserted or already existed.
        //
        // WHEN MATCHED only fires when description/unit actually changed (EXISTS ... EXCEPT is the
        // null-safe, pre-2022 spelling of IS DISTINCT FROM). An unconditional UPDATE took an
        // exclusive lock on the existing row on every cache miss -- on a cold cache, every flush
        // loop at once, against the same rows.
        const string sql = """
            MERGE metrics WITH (HOLDLOCK) AS t
            USING (SELECT @resourceId AS resource_id, @scopeId AS scope_id,
                          @name AS name, @type AS [type]) AS s
               ON t.resource_id = s.resource_id
              AND t.scope_id    = s.scope_id
              AND t.name        = s.name
              AND t.[type]      = s.[type]
            WHEN MATCHED AND EXISTS (SELECT t.description, t.unit EXCEPT SELECT @description, @unit) THEN
                UPDATE SET description = @description, unit = @unit
            WHEN NOT MATCHED THEN
                INSERT (resource_id, scope_id, name, description, unit, [type], created_at, tenant_id, service_name)
                VALUES (@resourceId, @scopeId, @name, @description, @unit, @type, SYSDATETIME(), @tenantId, @serviceName);
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
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add("@resourceId", SqlDbType.BigInt).Value = resId;
            cmd.Parameters.Add("@scopeId",    SqlDbType.BigInt).Value = scoId;
            // Lengths are declared to match the columns so the plan cache does not get an entry
            // per distinct string length.
            cmd.Parameters.Add("@name",        SqlDbType.NVarChar, 255).Value = m.Name;
            cmd.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value  = (object?)m.Description ?? DBNull.Value;
            cmd.Parameters.Add("@unit",        SqlDbType.NVarChar, 63).Value  = (object?)m.Unit ?? DBNull.Value;
            cmd.Parameters.Add("@type",        SqlDbType.NVarChar, 30).Value  = m.Type.ToString();
            // Written on the insert only (a cache miss); they cannot go stale -- the resource hash
            // includes service.name, so a rename produces a new resource and new metrics rows.
            var (tenantId, serviceName) = TenantAndService(m.Resource);
            cmd.Parameters.Add("@tenantId",    SqlDbType.BigInt).Value = tenantId;
            cmd.Parameters.Add("@serviceName", SqlDbType.NVarChar, 255).Value = (object?)serviceName ?? DBNull.Value;

            var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
            result[key] = id;
            cache.SetMetric(key, id);
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
                SerializeDeterministicJson(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

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
                SerializeDeterministicJson(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

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
                SerializeDeterministicJson(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

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
                SerializeDeterministicJson(d.Attributes), SerializeJsonOrNull(d.Exemplars)]);

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
                d.Count, d.Sum, SerializeJsonOrNull(d.QuantileValues), d.Flags, SerializeDeterministicJson(d.Attributes)]);

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
    // growth on a large flush -- and, at 2,000 rows per batch statement, keeps each one below the
    // 5,000-lock escalation threshold, so a large flush does not escalate its row locks into the
    // very table lock avoided below); and the default 30s BulkCopyTimeout is too tight for a large
    // batch on a loaded server.
    //
    // tableLock must stay false for every shared table (spans, log_records, the *_data_points
    // tables). TableLock on a clustered table is an exclusive table lock held until this flush's
    // transaction commits -- serializing every flush of that table across all of this process's
    // flush loops AND every other collector instance behind the same load balancer.
    private const int BulkCopyBatchSize = 2_000;
    private const int BulkCopyTimeoutSeconds = 120;

    private static SqlBulkCopy CreateBulkCopy(SqlConnection conn, SqlTransaction tx, string destinationTable, bool tableLock = false) => new(
        conn, tableLock ? SqlBulkCopyOptions.TableLock : SqlBulkCopyOptions.Default, tx)
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
