using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data;

namespace Keryhe.Telemetry.SqlServer.Services;

public sealed class TelemetryIngestionWorker(IConfiguration configuration, TelemetryIngestionChannel ingestionChannel, ResourceScopeCache cache, ILogger<TelemetryIngestionWorker> logger) : BackgroundService
{
    private readonly string _connectionString = configuration.GetConnectionString("Write")!;
    private const int MaxBatchSize = 2000;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            ProcessChannelAsync(ingestionChannel.Logs.Reader,    FlushLogsAsync,    "logs",    stoppingToken),
            ProcessChannelAsync(ingestionChannel.Traces.Reader,  FlushTracesAsync,  "traces",  stoppingToken),
            ProcessChannelAsync(ingestionChannel.Metrics.Reader, FlushMetricsAsync, "metrics", stoppingToken));

    private async Task ProcessChannelAsync<T>(
        ChannelReader<List<T>> reader,
        Func<List<T>, CancellationToken, Task> flush,
        string signalName,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(ct)) break;

                var batch = new List<T>(capacity: MaxBatchSize);
                while (batch.Count < MaxBatchSize && reader.TryRead(out var items))
                    batch.AddRange(items);

                if (batch.Count > 0)
                    await flush(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing {Signal} batch — batch dropped", signalName);
                await Task.Delay(100, ct);
            }
        }
    }

    // =========================================================================
    // FLUSH: LOGS
    // =========================================================================

    private async Task FlushLogsAsync(List<LogRecordModel> records, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var resourceIds = await ResolveResourcesAsync(conn, records.Select(r => r.Resource), ct);
        var scopeIds    = await ResolveScopesAsync(conn, records.Select(r => r.InstrumentationScope), ct);

        await BulkInsertLogsAsync(conn, records, resourceIds, scopeIds, ct);
        logger.LogDebug("Flushed {Count} log records", records.Count);
    }

    // =========================================================================
    // FLUSH: TRACES
    // =========================================================================

    private async Task FlushTracesAsync(List<TraceModel> traces, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
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

    private async Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
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
        SqlConnection conn,
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

        foreach (var (hash, model) in pending)
        {
            var id = await UpsertScopeAsync(conn, model, hash, ct);
            cache.SetScope(hash, id);
            result[hash] = id;
        }

        return result;
    }

    // MERGE upserts when not matched; the trailing SELECT always returns the ID whether
    // the row was just inserted or already existed.  HOLDLOCK prevents race conditions
    // where two concurrent MERGEs both see NOT MATCHED and both attempt an insert.
    private static async Task<long> UpsertResourceAsync(
        SqlConnection conn, ResourceModel model, string hash, CancellationToken ct)
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

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@attrJson",  (object?)SerializeDeterministicJson(model.Attributes) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash",      hash);
        cmd.Parameters.AddWithValue("@schemaUrl", (object?)model.SchemaUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tenantId",  model.TenantId);
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

    private static async Task BulkInsertLogsAsync(
        SqlConnection conn,
        List<LogRecordModel> records,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("resource_id",             typeof(long));
        dt.Columns.Add("scope_id",                typeof(long));
        dt.Columns.Add("time_unix_nano",           typeof(long));
        dt.Columns.Add("observed_time_unix_nano",  typeof(long));
        dt.Columns.Add("severity_number",          typeof(int));
        dt.Columns.Add("severity_text",            typeof(string));
        dt.Columns.Add("body_type",                typeof(string));
        dt.Columns.Add("body_value",               typeof(string));
        dt.Columns.Add("dropped_attributes_count", typeof(int));
        dt.Columns.Add("flags",                    typeof(int));
        dt.Columns.Add("trace_id",                 typeof(string));
        dt.Columns.Add("span_id",                  typeof(string));
        dt.Columns.Add("attributes_json",          typeof(string));

        foreach (var r in records)
        {
            dt.Rows.Add(
                resourceIds[HashResource(NormalizeResource(r.Resource))],
                scopeIds[HashScope(NormalizeScope(r.InstrumentationScope))],
                r.TimeUnixNano ?? 0L,
                BoxOrNull(r.ObservedTimeUnixNano),
                BoxOrNull(r.SeverityNumber),
                (object?)r.SeverityText          ?? DBNull.Value,
                (object?)r.BodyType?.ToString()  ?? DBNull.Value,
                (object?)r.BodyValue             ?? DBNull.Value,
                r.DroppedAttributesCount,
                r.Flags,
                (object?)r.TraceIdHex            ?? DBNull.Value,
                (object?)r.SpanIdHex             ?? DBNull.Value,
                (object?)SerializeJsonOrNull(r.Attributes) ?? DBNull.Value);
        }

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "log_records" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // =========================================================================
    // BULK INSERT: SPANS
    // =========================================================================

    private static async Task<Dictionary<(string TraceId, string SpanId), long>> BulkInsertSpansAsync(
        SqlConnection conn,
        List<(SpanModel Span, ResourceModel? Resource, InstrumentationScopeModel? Scope)> spans,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        // Stage spans into a temp table, then MERGE from it so we get OUTPUT rows
        // only for newly inserted spans (matching ON CONFLICT DO NOTHING + RETURNING).
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
                attributes_json          NVARCHAR(MAX)
            )
            """, conn))
        {
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        var dt = new DataTable();
        dt.Columns.Add("trace_id",                 typeof(string));
        dt.Columns.Add("span_id",                  typeof(string));
        dt.Columns.Add("parent_span_id",           typeof(string));
        dt.Columns.Add("resource_id",              typeof(long));
        dt.Columns.Add("scope_id",                 typeof(long));
        dt.Columns.Add("name",                     typeof(string));
        dt.Columns.Add("kind",                     typeof(string));
        dt.Columns.Add("start_time_unix_nano",     typeof(long));
        dt.Columns.Add("end_time_unix_nano",       typeof(long));
        dt.Columns.Add("dropped_attributes_count", typeof(int));
        dt.Columns.Add("dropped_events_count",     typeof(int));
        dt.Columns.Add("dropped_links_count",      typeof(int));
        dt.Columns.Add("trace_state",              typeof(string));
        dt.Columns.Add("status_code",              typeof(string));
        dt.Columns.Add("status_message",           typeof(string));
        dt.Columns.Add("attributes_json",          typeof(string));

        foreach (var (span, resource, scope) in spans)
        {
            dt.Rows.Add(
                span.TraceIdHex,
                span.SpanIdHex,
                (object?)span.ParentSpanIdHex   ?? DBNull.Value,
                resourceIds[HashResource(NormalizeResource(resource))],
                scopeIds[HashScope(NormalizeScope(scope))],
                span.Name,
                span.Kind.ToString(),
                span.StartTimeUnixNano,
                span.EndTimeUnixNano,
                span.DroppedAttributesCount,
                span.DroppedEventsCount,
                span.DroppedLinksCount,
                (object?)span.TraceState        ?? DBNull.Value,
                span.StatusCode.ToString(),
                (object?)span.StatusMessage     ?? DBNull.Value,
                (object?)SerializeJsonOrNull(span.Attributes) ?? DBNull.Value);
        }

        using (var bulk = new SqlBulkCopy(conn) { DestinationTableName = "#spans_stage" })
            await bulk.WriteToServerAsync(dt, ct);

        const string mergeSql = """
            MERGE spans AS target
            USING #spans_stage AS source
            ON target.trace_id = source.trace_id AND target.span_id = source.span_id
            WHEN NOT MATCHED THEN
                INSERT (trace_id, span_id, parent_span_id, resource_id, scope_id,
                        name, kind, start_time_unix_nano, end_time_unix_nano,
                        dropped_attributes_count, dropped_events_count, dropped_links_count,
                        trace_state, status_code, status_message, created_at, attributes_json)
                VALUES (source.trace_id, source.span_id, source.parent_span_id,
                        source.resource_id, source.scope_id,
                        source.name, source.kind, source.start_time_unix_nano, source.end_time_unix_nano,
                        source.dropped_attributes_count, source.dropped_events_count, source.dropped_links_count,
                        source.trace_state, source.status_code, source.status_message,
                        SYSDATETIME(), source.attributes_json)
            OUTPUT INSERTED.id, INSERTED.trace_id, INSERTED.span_id;
            """;

        var inserted = new Dictionary<(string, string), long>();
        await using var mergeCmd = new SqlCommand(mergeSql, conn);
        await using var reader = await mergeCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            inserted[(reader.GetString(1), reader.GetString(2))] = reader.GetInt64(0);

        return inserted;
    }

    private static async Task BulkInsertSpanEventsAsync(
        SqlConnection conn,
        List<(long SpanDbId, SpanEventModel Event)> events,
        CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("span_id",                  typeof(long));
        dt.Columns.Add("name",                     typeof(string));
        dt.Columns.Add("time_unix_nano",            typeof(long));
        dt.Columns.Add("dropped_attributes_count",  typeof(int));
        dt.Columns.Add("attributes_json",           typeof(string));

        foreach (var (spanId, e) in events)
            dt.Rows.Add(spanId, e.Name, e.TimeUnixNano, e.DroppedAttributesCount,
                (object?)SerializeJsonOrNull(e.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "span_events" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertSpanLinksAsync(
        SqlConnection conn,
        List<(long SpanDbId, SpanLinkModel Link)> links,
        CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("span_id",                  typeof(long));
        dt.Columns.Add("linked_trace_id",           typeof(string));
        dt.Columns.Add("linked_span_id",            typeof(string));
        dt.Columns.Add("trace_state",               typeof(string));
        dt.Columns.Add("dropped_attributes_count",  typeof(int));
        dt.Columns.Add("attributes_json",           typeof(string));

        foreach (var (spanId, l) in links)
            dt.Rows.Add(spanId, l.LinkedTraceIdHex, l.LinkedSpanIdHex,
                (object?)l.TraceState ?? DBNull.Value,
                l.DroppedAttributesCount,
                (object?)SerializeJsonOrNull(l.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "span_links" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // =========================================================================
    // INSERT: METRICS (individually to capture IDENTITY-generated IDs)
    // =========================================================================
    // SqlBulkCopy does not return generated IDs, so metrics (the reference table)
    // are inserted one at a time.  The bulk path is reserved for the much-higher-volume
    // data point tables below.

    private static async Task<long[]> InsertMetricsAsync(
        SqlConnection conn,
        List<MetricModel> metrics,
        Dictionary<string, long> resourceIds,
        Dictionary<string, long> scopeIds,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO metrics (resource_id, scope_id, name, description, unit, [type], created_at)
            OUTPUT INSERTED.id
            VALUES (@resourceId, @scopeId, @name, @description, @unit, @type, SYSDATETIME());
            """;

        var ids = new long[metrics.Count];
        for (var i = 0; i < metrics.Count; i++)
        {
            var m = metrics[i];
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@resourceId",  resourceIds[HashResource(NormalizeResource(m.Resource))]);
            cmd.Parameters.AddWithValue("@scopeId",     scopeIds[HashScope(NormalizeScope(m.InstrumentationScope))]);
            cmd.Parameters.AddWithValue("@name",        m.Name);
            cmd.Parameters.AddWithValue("@description", (object?)m.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@unit",        (object?)m.Unit        ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type",        m.Type.ToString());
            ids[i] = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        return ids;
    }

    // =========================================================================
    // BULK INSERT: DATA POINTS
    // =========================================================================

    private static async Task BulkInsertGaugeDataPointsAsync(
        SqlConnection conn, long metricId,
        List<GaugeDataPointModel> dataPoints, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("metric_id",            typeof(long));
        dt.Columns.Add("start_time_unix_nano", typeof(long));
        dt.Columns.Add("time_unix_nano",       typeof(long));
        dt.Columns.Add("value_double",         typeof(double));
        dt.Columns.Add("value_int",            typeof(long));
        dt.Columns.Add("flags",               typeof(int));
        dt.Columns.Add("attributes_json",     typeof(string));

        foreach (var d in dataPoints)
            dt.Rows.Add(metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt), d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "gauge_data_points" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertSumDataPointsAsync(
        SqlConnection conn, long metricId,
        List<SumDataPointModel> dataPoints, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("metric_id",                typeof(long));
        dt.Columns.Add("start_time_unix_nano",     typeof(long));
        dt.Columns.Add("time_unix_nano",           typeof(long));
        dt.Columns.Add("value_double",             typeof(double));
        dt.Columns.Add("value_int",                typeof(long));
        dt.Columns.Add("aggregation_temporality",  typeof(string));
        dt.Columns.Add("is_monotonic",             typeof(bool));
        dt.Columns.Add("flags",                    typeof(int));
        dt.Columns.Add("attributes_json",          typeof(string));

        foreach (var d in dataPoints)
            dt.Rows.Add(metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                BoxOrNull(d.ValueDouble), BoxOrNull(d.ValueInt),
                d.AggregationTemporality.ToString(), d.IsMonotonic, d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "sum_data_points" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertHistogramDataPointsAsync(
        SqlConnection conn, long metricId,
        List<HistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("metric_id",                typeof(long));
        dt.Columns.Add("start_time_unix_nano",     typeof(long));
        dt.Columns.Add("time_unix_nano",           typeof(long));
        dt.Columns.Add("count",                    typeof(long));
        dt.Columns.Add("sum_value",                typeof(double));
        dt.Columns.Add("bucket_counts",            typeof(string));
        dt.Columns.Add("explicit_bounds",          typeof(string));
        dt.Columns.Add("aggregation_temporality",  typeof(string));
        dt.Columns.Add("flags",                    typeof(int));
        dt.Columns.Add("min_value",                typeof(double));
        dt.Columns.Add("max_value",                typeof(double));
        dt.Columns.Add("attributes_json",          typeof(string));

        foreach (var d in dataPoints)
            dt.Rows.Add(metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum),
                (object?)SerializeJsonOrNull(d.BucketCounts)   ?? DBNull.Value,
                (object?)SerializeJsonOrNull(d.ExplicitBounds) ?? DBNull.Value,
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "histogram_data_points" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertExpHistogramDataPointsAsync(
        SqlConnection conn, long metricId,
        List<ExponentialHistogramDataPointModel> dataPoints, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("metric_id",               typeof(long));
        dt.Columns.Add("start_time_unix_nano",    typeof(long));
        dt.Columns.Add("time_unix_nano",          typeof(long));
        dt.Columns.Add("count",                   typeof(long));
        dt.Columns.Add("sum_value",               typeof(double));
        dt.Columns.Add("scale",                   typeof(int));
        dt.Columns.Add("zero_count",              typeof(long));
        dt.Columns.Add("positive_offset",         typeof(int));
        dt.Columns.Add("positive_bucket_counts",  typeof(string));
        dt.Columns.Add("negative_offset",         typeof(int));
        dt.Columns.Add("negative_bucket_counts",  typeof(string));
        dt.Columns.Add("aggregation_temporality", typeof(string));
        dt.Columns.Add("flags",                   typeof(int));
        dt.Columns.Add("min_value",               typeof(double));
        dt.Columns.Add("max_value",               typeof(double));
        dt.Columns.Add("attributes_json",         typeof(string));

        foreach (var d in dataPoints)
            dt.Rows.Add(metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, BoxOrNull(d.Sum), d.Scale, d.ZeroCount,
                BoxOrNull(d.PositiveOffset),
                (object?)SerializeJsonOrNull(d.PositiveBucketCounts) ?? DBNull.Value,
                BoxOrNull(d.NegativeOffset),
                (object?)SerializeJsonOrNull(d.NegativeBucketCounts) ?? DBNull.Value,
                d.AggregationTemporality.ToString(), d.Flags,
                BoxOrNull(d.Min), BoxOrNull(d.Max),
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "exponential_histogram_data_points" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    private static async Task BulkInsertSummaryDataPointsAsync(
        SqlConnection conn, long metricId,
        List<SummaryDataPointModel> dataPoints, CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add("metric_id",            typeof(long));
        dt.Columns.Add("start_time_unix_nano", typeof(long));
        dt.Columns.Add("time_unix_nano",       typeof(long));
        dt.Columns.Add("count",               typeof(long));
        dt.Columns.Add("sum_value",           typeof(double));
        dt.Columns.Add("quantile_values",     typeof(string));
        dt.Columns.Add("flags",              typeof(int));
        dt.Columns.Add("attributes_json",    typeof(string));

        foreach (var d in dataPoints)
            dt.Rows.Add(metricId, BoxOrNull(d.StartTimeUnixNano), d.TimeUnixNano,
                d.Count, d.Sum,
                (object?)SerializeJsonOrNull(d.QuantileValues) ?? DBNull.Value,
                d.Flags,
                (object?)SerializeJsonOrNull(d.Attributes) ?? DBNull.Value);

        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "summary_data_points" };
        foreach (DataColumn col in dt.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        await bulk.WriteToServerAsync(dt, ct);
    }

    // =========================================================================
    // STATIC HELPERS
    // =========================================================================

    private static object BoxOrNull<T>(T? value) where T : struct
        => value.HasValue ? (object)value.Value : DBNull.Value;

    private static ResourceModel NormalizeResource(ResourceModel? model)
    {
        if (model == null)
            return new ResourceModel { Attributes = new() { { "service.name", "unknown" } } };
        if (model.TenantId <= 0)
            model.TenantId = ResourceModel.DefaultTenantId;
        return model;
    }

    private static InstrumentationScopeModel NormalizeScope(InstrumentationScopeModel? model)
        => model ?? new InstrumentationScopeModel { Name = "unknown" };

    private static string HashResource(ResourceModel model)
    {
        var content = $"{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static string HashScope(InstrumentationScopeModel model)
    {
        var content = $"{model.Name}__{model.Version ?? ""}__{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static string SerializeDeterministicJson(Dictionary<string, object>? attributes)
    {
        var ordered = (attributes ?? new Dictionary<string, object>())
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonSerializer.Serialize(ordered);
    }

    private static string? SerializeJsonOrNull(object? value)
        => value == null ? null : JsonSerializer.Serialize(value);
}
