using System.Data.Common;
using ClickHouse.Client.ADO;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.ClickHouse.Services;

// =============================================================================
// ClickHouse read repositories. The shared Dapper bases hold the dialect-neutral read SQL
// and shape rows in C# (attributes are deserialized from JSON text, so no Postgres `->>` on
// the hot path). ClickHouse overrides only where its dialect differs: JSON extraction and the
// alert-rule CRUD (no identity columns / transactional UPDATE — see below). Connections come
// from ConnectionStrings:Api.
// =============================================================================

internal static class ClickHouseConnectionFactory
{
    public static async Task<DbConnection> OpenReadAsync(string connectionString, CancellationToken ct)
    {
        var conn = new ClickHouseConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>
/// Shared body for the <c>AttributePredicate</c> dialect hook (list-pages-server-side plan,
/// Phase 1), duplicated as an override on every ClickHouse read repository class below (there is
/// no mixin) but sharing one implementation. <c>JSONExtractRaw</c>, not <c>JSONExtractString</c>,
/// is deliberate: <c>JSONExtractString</c> returns <c>''</c> for a non-string (number/boolean)
/// value, which would silently break a filter like <c>http.status_code:500</c> (decision 7).
/// Trimming the surrounding quotes in SQL normalizes a JSON string's raw form (<c>"500"</c>) to
/// the same text as a JSON number's raw form (<c>500</c>), so both match the same bound value.
/// <c>AttributeKeyParamValue</c> needs no override: ClickHouse's key parameter is the raw key,
/// same as the base default.
/// </summary>
internal static class ClickHouseJsonAttributeHooks
{
    public static string Predicate(string column, string keyParam, string valueParam, bool negated)
    {
        var col = $"coalesce({column}, '')";
        var expr = $"lowerUTF8(trim(BOTH '\"' FROM JSONExtractRaw({col}, {keyParam})))";
        var valueExpr = $"lowerUTF8({valueParam})";
        return negated
            ? $"(JSONHas({col}, {keyParam}) = 0 OR {expr} != {valueExpr})"
            : $"JSONHas({col}, {keyParam}) = 1 AND {expr} = {valueExpr}";
    }
}

public class ClickHouseTraceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext, configuration)
{
    protected override string ResourcesTable => "(SELECT * FROM resources LIMIT 1 BY id)";
    protected override string ScopesTable => "(SELECT * FROM instrumentation_scopes LIMIT 1 BY id)";

    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // Same dialect hooks as ClickHouseLogReadRepository, needed here too now that the service/tag
    // filters run in SQL (list-page-scale plan, Phase 4). JSONHas tests key presence regardless
    // of the value's type, unlike JSONExtractString which returns '' for a non-scalar value.
    protected override string JsonHasKeyExpr(string jsonColumn, string keyParam)
        => $"JSONHas(coalesce({jsonColumn}, ''), {keyParam}) = 1";

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // Decision 3/Phase 3 pin helper: ClickHouse's created_at default is evaluated at statement
    // execution, so no 5-second back-off is needed here -- see ClickHouseLogReadRepository's
    // identical override.
    protected override string DatabaseClockNowExpr => "now64(9)";

    // ClickHouse can't reliably correlate a subquery to the outer row, so "any span in this trace
    // matches" is expressed as an uncorrelated membership test instead of EXISTS (decision 9,
    // list-pages-server-side plan Phase 1). The subquery's own spans alias is still needed so
    // innerPredicate/innerTimeClause (built against that alias by the caller) resolve.
    protected override string SpanLevelMatchPredicate(string traceIdColumn, string innerTimeClause, string innerPredicate, string spanAlias = "s2")
        => $"{traceIdColumn} IN (SELECT {spanAlias}.trace_id FROM spans {spanAlias} WHERE 1=1{innerTimeClause} AND {innerPredicate})";

    // Same uncorrelated shape, joined to the matching span's own resource for a resource-attribute
    // search term (list-pages-server-side plan, Phase 3).
    protected override string SpanLevelMatchPredicateWithResource(string traceIdColumn, string innerTimeClause, string innerPredicate)
        => $"{traceIdColumn} IN (SELECT s2.trace_id FROM spans s2 JOIN resources rs2 ON rs2.id = s2.resource_id WHERE 1=1{innerTimeClause} AND {innerPredicate})";

    /// <summary>
    /// The trace's start-time bounds from <c>trace_index</c> (fed by a materialized view from
    /// <c>spans</c>): <c>spans</c> is sorted by <c>(tenant_id, service_name, start_time_unix_nano)</c> and
    /// has no trace-id seek, so a by-trace read is narrowed with tenant and time bounds taken from this
    /// small index first. Null when the index has no row for the traces (nothing to narrow with).
    /// </summary>
    // The trace's extent from the list stands in for the trace_index lookup (one round trip fewer): [start - margin, end + margin].
    protected override (long Min, long Max)? HintedTraceTimeBounds(long startHintNano, long endHintNano)
        => (startHintNano - TraceHintMarginNanos, endHintNano + TraceHintMarginNanos);

    protected override async Task<(long Min, long Max)?> ResolveTraceTimeBoundsAsync(
        DbConnection conn, IReadOnlyList<string> traceIds, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        var inList = IdInPredicate("trace_id", "bt", traceIds, 32, parameters);
        var row = await conn.QuerySingleAsync<TraceBoundsRow>(new CommandDefinition($"""
            SELECT count() AS Cnt, minMerge(min_start) AS MinStart, maxMerge(max_start) AS MaxStart
            FROM trace_index
            WHERE tenant_id = @tenantId AND {inList}
            """, parameters, cancellationToken: ct));
        return row.Cnt == 0 ? null : (row.MinStart, row.MaxStart);
    }

    private sealed class TraceBoundsRow
    {
        public long Cnt { get; set; }
        public long MinStart { get; set; }
        public long MaxStart { get; set; }
    }

    /// <summary>
    /// The anchors derived table as a <c>GROUP BY trace_id</c> over the tenant's spans in
    /// <c>[@anchorFrom, @end]</c>: <c>argMin</c> over <c>(start, id)</c> picks the earliest span's
    /// columns, <c>max(status_code = 'ERROR')</c> is the in-scope error flag, and <c>HAVING</c> drops
    /// anchors that start before <c>@start</c> (the look-back margin). The tenant-and-service-led sort key
    /// and the daily partitions prune the scan. No <c>LIMIT 1 BY</c> dedup: a re-delivered span is
    /// stored twice and reads tolerate it (decision 7) -- a duplicate has the same start, duration
    /// and error flag, so it cannot change an anchor. The aggregates are computed in an inner query
    /// under non-colliding aliases and renamed outside it: ClickHouse resolves a SELECT alias over a
    /// same-named column in WHERE, so aliasing <c>argMin(service_name, ...)</c> as <c>service_name</c>
    /// would turn the service filter into an aggregate.
    /// </summary>
    protected override bool SupportsSeekAnchors => false;

    // No join to the reference tables: they are ReplacingMergeTree, read by id in their own queries (ReferenceRowsSql).
    protected override bool JoinsReferenceRows => false;

    // The reference tables are ReplacingMergeTree: an id can exist twice until a merge, so the lookup collapses duplicates
    // with LIMIT 1 BY id. The filter must be on the raw table (not on the ResourcesTable/ScopesTable subquery, which would
    // collapse the whole table first and only then filter it).
    protected override string ReferenceRowsSql(bool resources, string columns, string idPredicate)
        => $"SELECT r.id AS Id, {columns} FROM {(resources ? "resources" : "instrumentation_scopes")} r WHERE {idPredicate} LIMIT 1 BY r.id";

    protected override string AnchorsSql(bool hasService, bool pinAsOf, bool errorsOnly = false)
    {
        var service = hasService ? " AND service_name = @service" : "";
        var pin = pinAsOf ? " AND created_at <= @asOf" : "";
        const string earliest = "tuple(start_time_unix_nano, id)";
        return $"""
            (
                SELECT trace_id, anchor_span_pk, anchor_span_id, anchor_service AS service_name, root_name, anchor_kind,
                       anchor_start, anchor_end, anchor_created_at, has_error
                FROM (
                    SELECT trace_id,
                           argMin(id, {earliest}) AS anchor_span_pk,
                           argMin(span_id, {earliest}) AS anchor_span_id,
                           argMin(service_name, {earliest}) AS anchor_service,
                           argMin(name, {earliest}) AS root_name,
                           argMin(kind, {earliest}) AS anchor_kind,
                           min(start_time_unix_nano) AS anchor_start,
                           argMin(end_time_unix_nano, {earliest}) AS anchor_end,
                           argMin(created_at, {earliest}) AS anchor_created_at,
                           max(status_code = 'ERROR') AS has_error
                    FROM spans
                    WHERE tenant_id = @tenantId
                      AND start_time_unix_nano >= @anchorFrom AND start_time_unix_nano <= @end{service}{pin}
                    GROUP BY trace_id
                    HAVING anchor_start >= @start
                )
            )
            """;
    }
}

public class ClickHouseMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // Load-bearing for the metric label-filter fix (list-pages-server-side plan, Phase 1):
    // MetricReadRepositoryBase's data-point getters call AttributePredicate polymorphically, so
    // ClickHouse needs its own override here too, not just on the trace/log repos.
    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // Real bug found while wiring Phase 4's bucketed series queries (same shape as the
    // DatabaseClockNowExpr gap Phase 3 found on SqlServer/MySql/ClickHouse's trace repositories):
    // this class had no BucketIndexExpr override, so the new bucket-index SQL every series loader
    // builds would have fallen back to the Postgres-only `numerator / denominator`, which ClickHouse
    // promotes to Float64 instead of truncating — see DapperReadRepository's own doc comment.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";

    // Real bug found via the Phase 4 integration tests: ClickHouse's COALESCE requires one common
    // supertype across its branches and refuses to promote Int64 (value_int) to Float64
    // (value_double) implicitly — "NO_COMMON_TYPE". Cast the integer branch explicitly first; see
    // MetricReadRepositoryBase.CoalesceValueExpr's own doc comment.
    protected override string CoalesceValueExpr() => "COALESCE(dp.value_double, CAST(dp.value_int AS Nullable(Float64)))";

    // Decision 22/Phase 4: ClickHouse's "last point per stream-bucket" query uses argMax per
    // column instead of ROW_NUMBER() OVER (...) — see MetricReadRepositoryBase's own doc comment
    // on BuildLastPerStreamBucketSql for why.
    protected override string BuildLastPerStreamBucketSql(string table, string idInList, string timeClause,
        string labelClause, string bucketExpr, IReadOnlyList<string> valueColumns)
    {
        var cols = string.Join(", ", valueColumns.Select(c => $"argMax(dp.{c}, dp.time_unix_nano) AS {c}"));
        return $"""
            SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json, {bucketExpr} AS bucket,
                   max(dp.time_unix_nano) AS time_unix_nano, {cols}
            FROM {table} dp
            WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json, {bucketExpr}
            """;
    }

    protected override string BuildLastPerStreamSql(string table, string idInList, string timeClause,
        string labelClause, IReadOnlyList<string> valueColumns)
    {
        var cols = string.Join(", ", valueColumns.Select(c => $"argMax(dp.{c}, dp.time_unix_nano) AS {c}"));
        return $"""
            SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json,
                   max(dp.time_unix_nano) AS time_unix_nano, {cols}
            FROM {table} dp
            WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json
            """;
    }

    // Analytics-tier exemplar keyset paging (decision 26) is inherited unchanged from
    // MetricReadRepositoryBase.GetMetricExemplarsAsync — see that method's own doc comment for why
    // the base implementation (not the standard-tier newest-500 scan) is what every analytics
    // provider uses, and for the documented simplification versus the plan's literal
    // per-exemplar-ordinal SQL unnesting.

    // Load-bearing for the metrics catalog's service filter (list-pages-server-side plan, Phase
    // 5): ClickHouseTraceReadRepository/ClickHouseLogReadRepository already override this for the
    // same reason; MetricReadRepositoryBase's catalog query calls it polymorphically too.

    // Decision 27: metric_last_seen is an AggregatingMergeTree holding partial maxState(...)
    // states on this provider (fed by materialized views, not MetricTouchWorker — see
    // ClickHouseMetricTouchStore's doc comment), so every read must collapse it with maxMerge
    // first. The relational default reads the table directly.
    protected override string MetricLastSeenSql => "(SELECT metric_id, maxMerge(last_seen_state) AS last_seen_unix_nano FROM metric_last_seen GROUP BY metric_id)";

    // Real bug found via the new Phase 5 integration tests: the base class's exact-EXISTS fallback
    // (decision 27, end more than 1 hour in the past) correlates the subquery to the outer row via
    // "dp.metric_id = m.id", which ClickHouse rejects ("Resolve identifier 'm.id' from parent scope
    // only supported for constants and CTE") — the same correlated-subquery limitation
    // SpanLevelMatchPredicate's own ClickHouse override already documents. Use an uncorrelated
    // membership test instead, same shape as that override.
    protected override string ExactSeenInRangePredicate(string metricsAlias)
    {
        var inClauses = TelemetryIngestionHelpers.TimePrunedMetricTables.Select(t =>
            $"{metricsAlias}.id IN (SELECT metric_id FROM {t} WHERE time_unix_nano >= @seenStartNano AND time_unix_nano <= @seenEndNano)");
        return "(" + string.Join(" OR ", inClauses) + ")";
    }

    // Real bug found via the Phase 5 integration tests — see CatalogQuerySettingsClause's own doc
    // comment on MetricReadRepositoryBase for the full symptom and why this is the fix.
    protected override string CatalogQuerySettingsClause => " SETTINGS optimize_read_in_order = 0";

    // The actual root cause of the Phase 5 "next page comes back empty" bug — see
    // CatalogTimeExpr's own doc comment on MetricReadRepositoryBase for the full story
    // (ClickHouse.Client's DateTime parameter binding losing DateTime64(9) precision).
    protected override string CatalogTimeExpr(string timeExpr) => $"toUnixTimestamp64Nano({timeExpr})";
    protected override object CatalogCursorKeyParam(long nanos) => nanos;
}

public class ClickHouseLogReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext, configuration)
{
    protected override string ResourcesTable => "(SELECT * FROM resources LIMIT 1 BY id)";
    protected override string ScopesTable => "(SELECT * FROM instrumentation_scopes LIMIT 1 BY id)";

    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => ClickHouseJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);

    // attributes_json is a Nullable(String) holding JSON text; extract service.name with
    // JSONExtractString (coalesce guards NULL rows). ILIKE, LIMIT/OFFSET paging, and backslash
    // LIKE-escaping all match the Postgres defaults, so those hooks are inherited unchanged.

    // ClickHouse's `/` on Int64 operands promotes to Float64; intDiv keeps histogram
    // bucket-index math as true integer floor division.
    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";

    // Decision 3/Phase 2 pin helper: ClickHouse's created_at default is evaluated at statement
    // execution, so no 5-second back-off is needed here.
    protected override string DatabaseClockNowExpr => "now64(9)";
}

public class ClickHouseResourceReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);
}

/// <summary>
/// ClickHouse retention (schema 3.0.0): <c>ALTER TABLE ... DROP PARTITION</c> for every fully expired
/// day, no row deletes and no mutations. Spans, log records and the data-point tables are
/// partitioned by day, so retention granularity is the day: a partition is dropped once the whole
/// day is older than the cutoff, and rows in the cutoff's own day survive until it ends. The count
/// returned is the rows in the dropped partitions, read from <c>system.parts</c> just before the drop.
/// </summary>
public class ClickHouseRetentionSweeper(IConfiguration configuration)
    : RetentionSweeperBase
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    // trace_index and the rollup tables are partitioned by the same days as the table they derive from,
    // so their expired partitions go with it (not counted in the returned rows).
    public override Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropExpiredPartitionsAsync(["spans"], ["trace_index", "request_rollup_minute"], retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropExpiredPartitionsAsync(["log_records"], ["log_rollup_minute"], retentionPeriod, cancellationToken);

    public override Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropExpiredPartitionsAsync(TelemetryIngestionHelpers.TimePrunedMetricTables, [], retentionPeriod, cancellationToken);

    private async Task<int> DropExpiredPartitionsAsync(
        IReadOnlyList<string> countedTables, IReadOnlyList<string> uncountedTables, TimeSpan retentionPeriod, CancellationToken ct)
    {
        // CutoffNano guards a negative period (a cutoff in the future would drop everything).
        var cutoff = TimeConversion.UnixNanoToDateTime(CutoffNano(retentionPeriod));
        // A day partition (yyyymmdd) is fully expired once its whole day is before the cutoff's day.
        var cutoffDay = uint.Parse(cutoff.ToString("yyyyMMdd"));

        await using var conn = await OpenConnectionAsync(ct);

        long removed = 0;
        foreach (var table in countedTables.Concat(uncountedTables))
        {
            var partitions = (await conn.QueryAsync<PartitionRow>(new CommandDefinition(
                """
                SELECT partition AS Partition, sum(rows) AS Rows
                FROM system.parts
                WHERE database = currentDatabase() AND table = @table AND active AND toUInt32OrZero(partition) < @cutoffDay
                GROUP BY partition
                """,
                new { table, cutoffDay }, cancellationToken: ct))).ToList();

            foreach (var partition in partitions)
            {
                // The partition id comes from system.parts (digits only), not from a caller.
                await conn.ExecuteAsync(new CommandDefinition(
                    $"ALTER TABLE {table} DROP PARTITION ID '{partition.Partition}'", cancellationToken: ct));
                if (countedTables.Contains(table)) removed += partition.Rows;
            }
        }
        return (int)Math.Min(removed, int.MaxValue);
    }

    private sealed class PartitionRow
    {
        public string Partition { get; set; } = null!;
        public long Rows { get; set; }
    }
}

public class ClickHouseRollupReadRepository(IConfiguration configuration, ITenantContext tenantContext)
    : RollupReadRepositoryBase(tenantContext, configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Api")!;

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    protected override string BucketIndexExpr(string numerator, string denominator) => $"intDiv({numerator}, {denominator})";
    protected override string BigintExpr(string expression) => $"toInt64({expression})";
}
