using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IMetricReadRepository"/>.
///
/// Phase 4 (list-pages-server-side plan) replaced the former per-metric-row-loop raw/grouped
/// series readers with a single bucketed, database-side aggregation pipeline
/// (<see cref="GetMetricSeriesAsync(MetricSeriesQuery, CancellationToken)"/>):
///
/// 1. One query per metric type over every metric row sharing the requested name
///    (<c>metric_id IN (...)</c>), grouped/bucketed in SQL by time (decision 21).
/// 2. Rows are shaped into per-<b>stream</b> (metric_id + attributes_json) per-bucket aggregates.
///    All stateful math — cumulative-to-delta conversion with reset detection, histogram bucket
///    deltas, exponential-histogram downscaling — happens at this stream level (decision 22).
/// 3. Streams are merged into <b>display series</b> (service name + label set) only after that
///    per-stream math.
/// 4. Display series are ranked and the top <c>N</c> kept; the remainder folds into one "other"
///    series aggregated the same way as a real series (decision 23).
///
/// Metadata reads (name/type/id lookups, the metrics summary) are unchanged from the pre-Phase-4
/// implementation and still do their own lightweight SQL.
/// </summary>
public abstract class MetricReadRepositoryBase : DapperReadRepository, IMetricReadRepository
{
    /// <summary>Series/exemplar query timeout (decision 31); shares <c>Telemetry:Query:SummaryTimeoutSeconds</c> with the logs/traces summary paths.</summary>
    private readonly int _summaryTimeoutSeconds;

    protected MetricReadRepositoryBase(ITenantContext tenantContext, IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
    }

    private const string MetricSelect = """
        SELECT m.id AS Id, m.name AS Name, m.description AS Description, m.unit AS Unit,
               m.type AS Type, m.created_at AS CreatedAt, m.service_name AS ServiceName
        FROM metrics m
        WHERE m.tenant_id = @tenantId
        """;

    // =========================================================================
    // METRIC METADATA READS (unchanged by Phase 4)
    // =========================================================================

    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var exists = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT m.id FROM metrics m WHERE m.id = @id AND m.tenant_id = @tenantId",
            new { id, tenantId = TenantId }, cancellationToken: cancellationToken));
        // Parity with the former EF repository: a found metric maps to an (intentionally empty) MetricModel.
        return exists == null ? null : new MetricModel();
    }

    public async Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            MetricSelect + " AND m.name = @name", new { tenantId = TenantId, name }, cancellationToken: cancellationToken))).ToList();

        var stats = await GetMetricStatsAsync(conn, rows, cancellationToken);
        return rows.Select(m => ToMetricInfo(m, Svc(m.ServiceName) ?? "", stats.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            MetricSelect + " AND m.type = @type", new { tenantId = TenantId, type = type.ToString() }, cancellationToken: cancellationToken))).ToList();

        var stats = await GetMetricStatsAsync(conn, rows, cancellationToken);
        return rows.Select(m => ToMetricInfo(m, Svc(m.ServiceName) ?? "", stats.GetValueOrDefault(m.Id))).ToList();
    }

    /// <summary>
    /// True (unbounded) distinct-metric-name counts per type for a time range — reimplemented in
    /// Phase 5 (list-pages-server-side plan) over <c>metric_last_seen</c> instead of scanning the
    /// five data-point tables. Uses the same "seen in range" approximation as
    /// <see cref="GetMetricCatalogPageAsync"/> (decision 27).
    /// </summary>
    public async Task<MetricsSummary> GetMetricsSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        var where = "m.tenant_id = @tenantId";
        if (startTime.HasValue && endTime.HasValue)
            where += " AND " + SeenInRangeClause(startTime.Value, endTime.Value, parameters);

        var sql = $"""
            SELECT DISTINCT m.name AS Name, m.type AS Type
            FROM metrics m
            LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
            WHERE {where}
            """;

        var rows = await conn.QueryAsync<NameTypeRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        // A name could in principle appear with more than one type across instances (rare);
        // first-seen wins, matching the frontend's own uniqueMetrics grouping (items[0].type).
        var byName = new Dictionary<string, MetricType>();
        foreach (var r in rows)
            if (!byName.ContainsKey(r.Name)) byName[r.Name] = Enum.Parse<MetricType>(r.Type);

        var counts = byName.Values
            .GroupBy(t => t)
            .Select(g => new MetricTypeCount { Type = g.Key, Count = g.Count() })
            .ToList();

        return new MetricsSummary { UniqueMetricCount = byName.Count, CountsByType = counts };
    }

    private sealed class NameTypeRow
    {
        public string Name { get; set; } = null!;
        public string Type { get; set; } = null!;
    }

    // =========================================================================
    // METRICS CATALOG (Phase 5: server-side paging, decisions 27-28)
    // =========================================================================

    /// <summary>
    /// SQL source for <c>metric_last_seen</c> reads, aliased by the caller as <c>mls</c>. The
    /// relational default reads the table directly; ClickHouse overrides this to a
    /// <c>maxMerge(...)</c>-collapsing subquery, since its table holds partial aggregate states
    /// (decision 27).
    /// </summary>
    protected virtual string MetricLastSeenSql => "metric_last_seen";

    /// <summary>
    /// Decision 27's "seen in range" predicate: approximate by default
    /// (<c>last_seen_unix_nano >= start AND metrics.created_at &lt;= end</c>, reading the LEFT
    /// JOINed <c>mls</c> alias every caller of this method joins in), or an exact per-candidate
    /// <c>EXISTS</c> over the five data-point tables when <paramref name="end"/> is more than an
    /// hour in the past — cheaper than a full scan because it is index-served
    /// (<c>metric_id, time_unix_nano</c>) and only runs against the metrics already matching every
    /// other filter, not the whole catalog. A metric with no <c>metric_last_seen</c> row yet (not
    /// yet touched by <c>MetricTouchWorker</c>, which runs on its own interval — see that type's
    /// doc comment) is excluded by the approximate branch until its first touch; documented, not
    /// worked around, since the alternative (always falling back to the exact EXISTS check) would
    /// give up the whole point of this table for any metric younger than one flush interval.
    /// </summary>
    protected string SeenInRangeClause(DateTime start, DateTime end, DynamicParameters parameters, string metricsAlias = "m", string mlsAlias = "mls")
    {
        var startNano = TimeConversion.DateTimeToUnixNano(start);
        var endNano = TimeConversion.DateTimeToUnixNano(end);
        parameters.Add("seenStartNano", startNano);
        parameters.Add("seenEndNano", endNano);
        parameters.Add("seenEnd", end);

        if (DateTime.UtcNow - end <= TimeSpan.FromHours(1))
            return $"{mlsAlias}.last_seen_unix_nano >= @seenStartNano AND {metricsAlias}.created_at <= @seenEnd";

        return ExactSeenInRangePredicate(metricsAlias);
    }

    /// <summary>
    /// The exact per-candidate check for decision 27's &gt;1-hour-in-the-past fallback: does
    /// <paramref name="metricsAlias"/>.id have a matching row in any of the five data-point tables
    /// within <c>@seenStartNano</c>/<c>@seenEndNano</c> (bound by the caller,
    /// <see cref="SeenInRangeClause"/>)? PostgreSQL/Timescale/SqlServer/MySql use a correlated
    /// <c>EXISTS</c>, index-served on each table's <c>(metric_id, time_unix_nano)</c>. ClickHouse
    /// overrides this: it can't resolve a correlated subquery referencing the outer row
    /// ("Resolve identifier 'm.id' from parent scope only supported for constants and CTE" —
    /// real error surfaced by the Phase 5 integration tests, the same class of gap as
    /// <c>SpanLevelMatchPredicate</c>'s ClickHouse override), so it uses an uncorrelated
    /// <c>m.id IN (SELECT metric_id FROM ...)</c> instead.
    /// </summary>
    protected virtual string ExactSeenInRangePredicate(string metricsAlias)
    {
        var existsClauses = TelemetryIngestionHelpers.TimePrunedMetricTables.Select(t =>
            $"EXISTS (SELECT 1 FROM {t} dp WHERE dp.metric_id = {metricsAlias}.id AND dp.time_unix_nano >= @seenStartNano AND dp.time_unix_nano <= @seenEndNano)");
        return "(" + string.Join(" OR ", existsClauses) + ")";
    }

    /// <summary>
    /// Wraps a SQL time expression (a bare column like <c>"m.created_at"</c>, or an aggregate like
    /// <c>"MAX(m.created_at)"</c>) for use in the catalog's keyset cursor comparison. Identity on
    /// every relational provider: <c>metrics.created_at</c> is a plain timestamp column there, and
    /// comparing it directly against a bound <see cref="DateTime"/> parameter
    /// (<see cref="CatalogCursorKeyParam"/>) works correctly at those providers' native precision.
    ///
    /// ClickHouse overrides both this and <see cref="CatalogCursorKeyParam"/> together: a real bug
    /// found via the Phase 5 integration tests, where a "next" page after a non-empty "first" page
    /// (whose own <c>NextCursor</c> was correctly non-null) came back with ZERO rows. Root cause:
    /// <c>created_at</c> is <c>DateTime64(9)</c> there, and ClickHouse.Client's parameter binding
    /// for a plain .NET <see cref="DateTime"/> does not preserve that precision — the bound
    /// <c>@cursorK</c> silently lost enough precision to sort BEFORE every row sharing the same
    /// wall-clock second (all 25 rows in the reproducing test, inserted in one flush), so the
    /// keyset predicate's <c>created_at &lt;= @cursorK</c> half excluded everything. ClickHouse's
    /// override compares raw nanoseconds instead: the column via <c>toUnixTimestamp64Nano(...)</c>
    /// here, and the parameter as the already-nanosecond-precision <see cref="long"/> cursor value
    /// directly (no DateTime round trip) via <see cref="CatalogCursorKeyParam"/>.
    /// </summary>
    protected virtual string CatalogTimeExpr(string timeExpr) => timeExpr;

    /// <summary>The value bound for a decoded cursor's <c>K</c> against <see cref="CatalogTimeExpr"/>'s column expression — see that method's doc comment for why ClickHouse overrides this to the raw nanosecond <see cref="long"/> rather than a converted <see cref="DateTime"/>.</summary>
    protected virtual object CatalogCursorKeyParam(long nanos) => TimeConversion.UnixNanoToDateTime(nanos);

    public async Task<MetricCatalogPage> GetMetricCatalogPageAsync(MetricCatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var size = Math.Clamp(query.Size, 1, 500);
        var groupByName = string.Equals(query.GroupBy, "name", StringComparison.OrdinalIgnoreCase);

        return groupByName
            ? await GetMetricCatalogByNameAsync(conn, query, size, cancellationToken)
            : await GetMetricCatalogByInstanceAsync(conn, query, size, cancellationToken);
    }

    /// <summary>Common WHERE fragment (name/service/type filters) shared by both catalog views, ANDed onto the caller's own tenant/seen-in-range predicate.</summary>
    private (string Clause, DynamicParameters Parameters) BuildCatalogFilterClauses(MetricCatalogQuery query)
    {
        var parameters = new DynamicParameters();
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            parameters.Add("nameSearch", $"%{EscapeLike(query.Q)}%");
            clauses.Add(FreeTextPredicate("m.name", "@nameSearch"));
        }
        if (!string.IsNullOrWhiteSpace(query.Service))
        {
            parameters.Add("service", query.Service);
            clauses.Add("m.service_name = @service");
        }
        if (query.Type.HasValue)
        {
            parameters.Add("metricType", query.Type.Value.ToString());
            clauses.Add("m.type = @metricType");
        }

        return (clauses.Count == 0 ? "" : " AND " + string.Join(" AND ", clauses), parameters);
    }

    private async Task<MetricCatalogPage> GetMetricCatalogByInstanceAsync(DbConnection conn, MetricCatalogQuery query, int size, CancellationToken cancellationToken)
    {
        var (filterClause, filterParams) = BuildCatalogFilterClauses(query);
        var parameters = new DynamicParameters();
        parameters.AddDynamicParams(filterParams);
        var seenClause = SeenInRangeClause(query.Start, query.End, parameters);
        parameters.Add("tenantId", TenantId);

        var filterHashText = $"{query.Start:O}|{query.End:O}|{query.Q}|{query.Service}|{query.Type}|instance";
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        DecodedCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = KeysetCursor.Decode(query.Cursor);
            if (cursor == null || !KeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        var baseWhere = $"m.tenant_id = @tenantId AND {seenClause}{filterClause}";

        List<MetricRow> rows;
        bool forward;
        int requestedSize;
        switch (nav)
        {
            case "next":
                forward = true;
                requestedSize = size;
                var mergedNext = new DynamicParameters(parameters);
                mergedNext.Add("cursorK", CatalogCursorKeyParam(cursor!.K));
                mergedNext.Add("cursorId", cursor.Id);
                rows = await FetchCatalogInstancePageAsync(conn, baseWhere,
                    KeysetCursor.Predicate(CatalogTimeExpr("m.created_at"), "m.id", "cursorK", "cursorId", descending: true),
                    mergedNext,
                    requestedSize, descending: true, cancellationToken);
                break;
            case "prev":
                forward = false;
                requestedSize = size;
                rows = await FetchCatalogInstancePageAsync(conn, baseWhere,
                    KeysetCursor.Predicate(CatalogTimeExpr("m.created_at"), "m.id", "cursorK", "cursorId", descending: false),
                    Merge(parameters, new { cursorK = CatalogCursorKeyParam(cursor!.K), cursorId = cursor.Id }),
                    requestedSize, descending: false, cancellationToken);
                break;
            case "last":
                forward = false;
                var lastCount = await TryGetExactCatalogInstanceCountAsync(conn, baseWhere, parameters, cancellationToken);
                requestedSize = lastCount.HasValue ? KeysetCursor.LastPageRowCount(lastCount.Value, size) : size;
                rows = await FetchCatalogInstancePageAsync(conn, baseWhere, "", parameters, requestedSize, descending: false, cancellationToken);
                break;
            default:
                forward = true;
                requestedSize = size;
                rows = await FetchCatalogInstancePageAsync(conn, baseWhere, "", parameters, requestedSize, descending: true, cancellationToken);
                break;
        }

        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<MetricRow> displayRows;
        string? nextCursor;
        string? prevCursor;
        if (forward)
        {
            displayRows = rows;
            nextCursor = hasExtra ? EncodeInstance(displayRows[^1], filterHash) : null;
            prevCursor = nav == "first" ? null : (displayRows.Count > 0 ? EncodeInstance(displayRows[0], filterHash) : null);
        }
        else
        {
            displayRows = [.. rows];
            displayRows.Reverse();
            if (nav == "last")
            {
                nextCursor = null;
                prevCursor = !hasExtra || displayRows.Count == 0 ? null : EncodeInstance(displayRows[0], filterHash);
            }
            else
            {
                nextCursor = displayRows.Count > 0 ? EncodeInstance(displayRows[^1], filterHash) : null;
                prevCursor = hasExtra && displayRows.Count > 0 ? EncodeInstance(displayRows[0], filterHash) : null;
            }
        }

        var items = displayRows.Select(m => ToMetricInfo(m, Svc(m.ServiceName))).ToList();

        var (total, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM metrics m LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id WHERE {baseWhere}",
                parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);

        return new MetricCatalogPage
        {
            Items = items,
            NextCursor = nextCursor,
            PrevCursor = prevCursor,
            Total = timedOut ? null : total,
            TotalIsLowerBound = timedOut
        };
    }

    private static string EncodeInstance(MetricRow row, string filterHash)
        => KeysetCursor.Encode(TimeConversion.DateTimeToUnixNano(row.CreatedAt), row.Id, filterHash);

    private async Task<List<MetricRow>> FetchCatalogInstancePageAsync(
        DbConnection conn, string baseWhere, string cursorPredicate, object parameters, int size, bool descending, CancellationToken cancellationToken)
    {
        var where = string.IsNullOrEmpty(cursorPredicate) ? baseWhere : $"{baseWhere} AND {cursorPredicate}";
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT m.id AS Id, m.name AS Name, m.description AS Description, m.unit AS Unit,
                   m.type AS Type, m.created_at AS CreatedAt, m.service_name AS ServiceName
            FROM metrics m
            LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
            WHERE {where}
            ORDER BY m.created_at {order}, m.id {order}
            {LiteralPagingClause(size + 1, 0)}{CatalogQuerySettingsClause}
            """;
        var rows = await conn.QueryAsync<MetricRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// Trailing SQL appended after the catalog's <c>ORDER BY ... LIMIT</c> clause. Empty on every
    /// relational provider. ClickHouse overrides this to <c>SETTINGS optimize_read_in_order = 0</c>
    /// as an extra safety margin alongside <see cref="LiteralPagingClause"/> (that override's doc
    /// comment has the actual bug this catalog query hit and how it was root-caused) — disabling
    /// this optimizer forces a correct full sort for the multi-way OR'd EXISTS/IN predicate
    /// (decision 27's exact-seen-in-range fallback) that <c>LiteralPagingClause</c> alone already
    /// fixes, at a cost that's negligible for a list capped at 500 rows per page.
    /// </summary>
    protected virtual string CatalogQuerySettingsClause => "";

    private async Task<long?> TryGetExactCatalogInstanceCountAsync(DbConnection conn, string where, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                $"SELECT COUNT(*) FROM metrics m LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id WHERE {where}",
                parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return timedOut ? null : result;
    }

    /// <summary>
    /// <c>groupBy=name</c> view (decision 28): one row per metric name, keyset on
    /// <c>(MAX(created_at), name)</c>. The GROUP BY/paging query itself stays portable across all
    /// five providers (plain COUNT/MAX/HAVING); a second, narrow follow-up query fetches the page's
    /// own rows to compute the per-name type and distinct service list in C#, instead of a
    /// per-provider ARRAY_AGG/GROUP_CONCAT/groupUniqArray dialect for what is at most `size` names.
    /// </summary>
    private async Task<MetricCatalogPage> GetMetricCatalogByNameAsync(DbConnection conn, MetricCatalogQuery query, int size, CancellationToken cancellationToken)
    {
        var (filterClause, filterParams) = BuildCatalogFilterClauses(query);
        var parameters = new DynamicParameters();
        parameters.AddDynamicParams(filterParams);
        var seenClause = SeenInRangeClause(query.Start, query.End, parameters);
        parameters.Add("tenantId", TenantId);

        var filterHashText = $"{query.Start:O}|{query.End:O}|{query.Q}|{query.Service}|{query.Type}|name";
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        DecodedNameCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = NameKeysetCursor.Decode(query.Cursor);
            if (cursor == null || !NameKeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        var baseWhere = $"m.tenant_id = @tenantId AND {seenClause}{filterClause}";

        List<NameGroupRow> rows;
        bool forward;
        int requestedSize;
        switch (nav)
        {
            case "next":
                forward = true;
                requestedSize = size;
                rows = await FetchCatalogNameOfPageAsync(conn, baseWhere,
                    NameKeysetCursor.Predicate(CatalogTimeExpr("MAX(m.created_at)"), "m.name", "cursorK", "cursorName", descending: true),
                    Merge(parameters, new { cursorK = CatalogCursorKeyParam(cursor!.K), cursorName = cursor.Name }),
                    requestedSize, descending: true, cancellationToken);
                break;
            case "prev":
                forward = false;
                requestedSize = size;
                rows = await FetchCatalogNameOfPageAsync(conn, baseWhere,
                    NameKeysetCursor.Predicate(CatalogTimeExpr("MAX(m.created_at)"), "m.name", "cursorK", "cursorName", descending: false),
                    Merge(parameters, new { cursorK = CatalogCursorKeyParam(cursor!.K), cursorName = cursor.Name }),
                    requestedSize, descending: false, cancellationToken);
                break;
            case "last":
                forward = false;
                var lastCount = await TryGetExactCatalogNameCountAsync(conn, baseWhere, parameters, cancellationToken);
                requestedSize = lastCount.HasValue ? KeysetCursor.LastPageRowCount(lastCount.Value, size) : size;
                rows = await FetchCatalogNameOfPageAsync(conn, baseWhere, "", parameters, requestedSize, descending: false, cancellationToken);
                break;
            default:
                forward = true;
                requestedSize = size;
                rows = await FetchCatalogNameOfPageAsync(conn, baseWhere, "", parameters, requestedSize, descending: true, cancellationToken);
                break;
        }

        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<NameGroupRow> displayRows;
        string? nextCursor;
        string? prevCursor;
        if (forward)
        {
            displayRows = rows;
            nextCursor = hasExtra ? EncodeName(displayRows[^1], filterHash) : null;
            prevCursor = nav == "first" ? null : (displayRows.Count > 0 ? EncodeName(displayRows[0], filterHash) : null);
        }
        else
        {
            displayRows = [.. rows];
            displayRows.Reverse();
            if (nav == "last")
            {
                nextCursor = null;
                prevCursor = !hasExtra || displayRows.Count == 0 ? null : EncodeName(displayRows[0], filterHash);
            }
            else
            {
                nextCursor = displayRows.Count > 0 ? EncodeName(displayRows[^1], filterHash) : null;
                prevCursor = hasExtra && displayRows.Count > 0 ? EncodeName(displayRows[0], filterHash) : null;
            }
        }

        // Second, narrow follow-up query: the page's own instances only, to derive per-name type
        // (first-seen wins) and the distinct service list in C# — see this method's doc comment.
        var names = displayRows.Select(r => r.Name).ToList();
        var items = new List<UniqueMetricSummary>();
        if (names.Count > 0)
        {
            var nameInParams = new DynamicParameters(parameters);
            var namesList = string.Join(",", names.Select((_, i) => $"@nameFilter{i}"));
            for (var i = 0; i < names.Count; i++) nameInParams.Add($"nameFilter{i}", names[i]);

            var detailSql = $"""
                SELECT m.name AS Name, m.description AS Description, m.unit AS Unit, m.type AS Type,
                       m.created_at AS CreatedAt, m.service_name AS ServiceName
                FROM metrics m
                LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
                WHERE {baseWhere} AND m.name IN ({namesList})
                """;
            var detailRows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(detailSql, nameInParams, cancellationToken: cancellationToken))).ToList();

            var byName = detailRows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.OrderBy(r => r.CreatedAt).ToList());
            foreach (var row in displayRows)
            {
                var group = byName.GetValueOrDefault(row.Name) ?? new List<MetricRow>();
                var services = group
                    .Select(r => Svc(r.ServiceName))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct()
                    .OrderBy(s => s)
                    .Select(s => s!)
                    .ToList();
                items.Add(new UniqueMetricSummary
                {
                    Name = row.Name,
                    Type = group.Count > 0 ? Enum.Parse<MetricType>(group[0].Type) : MetricType.GAUGE,
                    Unit = group.Count > 0 ? group[0].Unit : null,
                    Description = group.Count > 0 ? group[0].Description : null,
                    InstanceCount = row.InstanceCount,
                    Services = services,
                    LastSeen = row.NewestCreatedAt
                });
            }
        }

        var (total, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                $"""
                SELECT COUNT(*) FROM (
                    SELECT m.name
                    FROM metrics m
                    LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
                    WHERE {baseWhere}
                    GROUP BY m.name
                ) named
                """,
                parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);

        return new MetricCatalogPage
        {
            Names = items,
            NextCursor = nextCursor,
            PrevCursor = prevCursor,
            Total = timedOut ? null : total,
            TotalIsLowerBound = timedOut
        };
    }

    private static string EncodeName(NameGroupRow row, string filterHash)
        => NameKeysetCursor.Encode(TimeConversion.DateTimeToUnixNano(row.NewestCreatedAt), row.Name, filterHash);

    private async Task<List<NameGroupRow>> FetchCatalogNameOfPageAsync(
        DbConnection conn, string baseWhere, string cursorPredicate, object parameters, int size, bool descending, CancellationToken cancellationToken)
    {
        var having = string.IsNullOrEmpty(cursorPredicate) ? "" : $"HAVING {cursorPredicate}";
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT m.name AS Name, MAX(m.created_at) AS NewestCreatedAt, COUNT(*) AS InstanceCount
            FROM metrics m
            LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
            WHERE {baseWhere}
            GROUP BY m.name
            {having}
            ORDER BY MAX(m.created_at) {order}, m.name {order}
            {LiteralPagingClause(size + 1, 0)}{CatalogQuerySettingsClause}
            """;
        var rows = await conn.QueryAsync<NameGroupRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// <c>LIMIT n OFFSET m</c> with the values inlined as literals rather than bound parameters —
    /// safe because both are C#-computed page-size integers, never user input. Used only by the
    /// two catalog page fetches above, in place of the shared <see cref="PagingClause"/> (which
    /// stays parameterized for every other query in this file and the rest of the codebase). Real
    /// bug found via the Phase 5 integration tests: on ClickHouse specifically, binding `@limit`/
    /// `@offset` as parameters alongside this query's other bound parameters (the multi-way OR'd
    /// EXISTS/IN predicate's own parameters) intermittently returned FEWER rows than a literal
    /// LIMIT of the same value — a plain re-query with a larger LIMIT, or the same query paged via
    /// keyset `next`, found the missing rows, so this was never a genuine data-visibility race
    /// (confirmed by re-running the small-LIMIT query after a 500ms delay with no change) but some
    /// interaction between ClickHouse.Client's parameter binding and this query's shape. Inlining
    /// sidesteps it entirely rather than chasing the exact mechanism.
    /// </summary>
    protected virtual string LiteralPagingClause(int limit, int offset) => $"LIMIT {limit} OFFSET {offset}";

    private async Task<long?> TryGetExactCatalogNameCountAsync(DbConnection conn, string where, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                $"""
                SELECT COUNT(*) FROM (
                    SELECT m.name
                    FROM metrics m
                    LEFT JOIN {MetricLastSeenSql} mls ON mls.metric_id = m.id
                    WHERE {where}
                    GROUP BY m.name
                ) named
                """,
                parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return timedOut ? null : result;
    }

    private sealed class NameGroupRow
    {
        public string Name { get; set; } = null!;
        public DateTime NewestCreatedAt { get; set; }
        public int InstanceCount { get; set; }
    }

    // =========================================================================
    // TIME SERIES READS (Phase 4: database-side bucketed aggregation)
    // =========================================================================

    /// <summary>Resolved bucketing parameters for one series request (decision 21).</summary>
    private readonly record struct Bucketing(long StartNano, long EndNano, long BucketNanos, int Points);

    /// <summary>Minimum bucket width — guards against a division-by-near-zero window producing a degenerate bucket count.</summary>
    private const long MinBucketNanos = 1_000_000; // 1 ms

    private static Bucketing ComputeBucketing(DateTime start, DateTime end, int requestedPoints)
    {
        var points = Math.Clamp(requestedPoints, 1, 1000);
        var startNano = TimeConversion.DateTimeToUnixNano(start);
        var endNano = TimeConversion.DateTimeToUnixNano(end);
        var windowNano = Math.Max(1L, endNano - startNano);
        var bucketNanos = Math.Max(windowNano / points, MinBucketNanos);
        return new Bucketing(startNano, endNano, bucketNanos, points);
    }

    /// <summary>
    /// The clamped bucket-index SQL expression shared by every per-type loader: floor((time -
    /// start) / bucketNanos), clamped into [0, points-1] so a point exactly on (or fractionally
    /// past, from clock skew) the window edge still lands in a real bucket rather than falling out
    /// of every GROUP BY bucket the caller expects to see. Same pattern as
    /// <c>LogReadRepositoryBase</c>'s raw-path histogram bucketing.
    /// </summary>
    private string ClampedBucketExpr()
    {
        var raw = BucketIndexExpr("(dp.time_unix_nano - @start)", "@bucketNanos");
        return $"CASE WHEN {raw} < 0 THEN 0 WHEN {raw} > @pointsMinus1 THEN @pointsMinus1 ELSE {raw} END";
    }

    /// <summary>
    /// Gauge/Sum's "value" is whichever of <c>value_double</c>/<c>value_int</c> is non-null — every
    /// relational provider accepts a plain <c>COALESCE(dp.value_double, dp.value_int)</c> (Int64
    /// silently promotes to Float64). ClickHouse refuses that promotion inside <c>COALESCE</c>
    /// itself: it requires one common supertype across the branches and, per its own error text,
    /// "there is no floating point type that can exactly represent all required integers" — so an
    /// Int64/Float64 pair has no common type there, `NO_COMMON_TYPE`. Overridden on ClickHouse to
    /// cast the integer branch first. Found via the Phase 4 integration tests
    /// (<c>LoadGaugeAsync</c>/<c>LoadSumDeltaAsync</c>).
    /// </summary>
    protected virtual string CoalesceValueExpr() => "COALESCE(dp.value_double, dp.value_int)";

    public async Task<MetricSeriesResult?> GetMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var sql = MetricSelect + " AND m.name = @name";
        if (query.MetricId.HasValue) sql += " AND m.id = @metricId";
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            sql, new { tenantId = TenantId, name = query.MetricName, metricId = query.MetricId }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0) return null;

        var type = Enum.Parse<MetricType>(metrics[0].Type);
        var unit = metrics[0].Unit;
        var metricIds = metrics.Select(m => m.Id).ToList();
        var serviceNameByMetricId = ToDictionaryFirst(metrics, m => m.Id,
            m => Svc(m.ServiceName) ?? "unknown");

        var points = Math.Clamp(query.Points, 1, 1000);
        var top = Math.Max(1, query.Top);

        // Decision 31: full-resolution attempt, then one retry at a quarter of the requested
        // points, then a "window too large" (empty) result rather than a 500.
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await RunSeriesQueryAsync(conn, type, metricIds, serviceNameByMetricId, query, points, top, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);

        if (timedOut)
        {
            var retryPoints = Math.Max(1, points / 4);
            var (retryResult, retryTimedOut) = await TimedQuery.RunAsync(
                async (timeoutSeconds, ct) => await RunSeriesQueryAsync(conn, type, metricIds, serviceNameByMetricId, query, retryPoints, top, timeoutSeconds, ct),
                _summaryTimeoutSeconds, cancellationToken);

            if (retryTimedOut)
            {
                return new MetricSeriesResult { Name = query.MetricName, Type = type, Unit = unit, TimedOut = true };
            }

            retryResult!.TimedOut = false; // succeeded, just at a coarser resolution — not surfaced as a timeout
            return retryResult;
        }

        return result;
    }

    /// <summary>
    /// Phase 8 export (decision 29): the exact same aggregation pipeline as
    /// <see cref="GetMetricSeriesAsync"/>, with <see cref="MetricSeriesQuery.Top"/> set to
    /// <see cref="int.MaxValue"/> so <c>BuildDisplaySeriesAndOther</c>'s <c>Skip(top)</c> is always
    /// empty — every display series comes back in <c>Series</c> and <c>Other</c> is always null.
    /// This is a deliberate reuse rather than a fresh unbuffered-per-row implementation: the
    /// bucketed query already collapses raw data points down to at most (streams × points) rows
    /// before this method ever sees them, so the "don't hold the whole resultset in memory" concern
    /// behind the plan's "unbuffered" wording doesn't apply to data of this shape — see this
    /// project's Phase 8 CLAUDE.md notes for the full reasoning.
    /// </summary>
    public async IAsyncEnumerable<MetricExportRow> ExportMetricSeriesAsync(
        MetricExportQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seriesQuery = new MetricSeriesQuery
        {
            MetricName = query.MetricName,
            MetricId = query.MetricId,
            Start = query.Start,
            End = query.End,
            LabelFilters = query.LabelFilters,
            Points = query.Points,
            Top = int.MaxValue
        };

        var result = await GetMetricSeriesAsync(seriesQuery, cancellationToken);
        if (result == null) yield break;

        foreach (var series in result.Series.OrderBy(s => s.SeriesName, StringComparer.Ordinal))
        {
            foreach (var point in series.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MetricExportRow
                {
                    MetricName = result.Name,
                    SeriesName = series.SeriesName,
                    ServiceName = series.ServiceName,
                    Labels = series.Labels,
                    BucketStart = point.Timestamp,
                    Value = point.Value,
                    Min = point.Min,
                    Max = point.Max,
                    Count = point.Count,
                    Sum = point.Sum,
                    BucketCounts = point.BucketCounts,
                    BucketBounds = point.BucketBounds,
                    Quantiles = point.Quantiles,
                    QuantileValues = point.QuantileValues,
                    Rate = point.Rate
                };
            }
        }
    }

    private async Task<MetricSeriesResult> RunSeriesQueryAsync(DbConnection conn, MetricType type, List<long> metricIds,
        Dictionary<long, string> serviceNameByMetricId, MetricSeriesQuery query, int points, int top, int timeoutSeconds, CancellationToken ct)
    {
        var bucketing = ComputeBucketing(query.Start, query.End, points);

        var streams = type switch
        {
            MetricType.GAUGE => await LoadGaugeAsync(conn, metricIds, bucketing, query.LabelFilters, timeoutSeconds, ct),
            MetricType.SUM => await LoadSumAsync(conn, metricIds, bucketing, query.LabelFilters, timeoutSeconds, ct),
            MetricType.HISTOGRAM => await LoadDistributionAsync(conn, "histogram_data_points", isExponential: false, metricIds, bucketing, query.LabelFilters, timeoutSeconds, ct),
            MetricType.EXPONENTIAL_HISTOGRAM => await LoadDistributionAsync(conn, "exponential_histogram_data_points", isExponential: true, metricIds, bucketing, query.LabelFilters, timeoutSeconds, ct),
            MetricType.SUMMARY => await LoadSummaryAsync(conn, metricIds, bucketing, query.LabelFilters, timeoutSeconds, ct),
            _ => new Dictionary<string, StreamAgg>()
        };

        foreach (var stream in streams.Values)
            stream.ServiceName = serviceNameByMetricId.GetValueOrDefault(stream.MetricId, "unknown");

        var (series, other) = BuildDisplaySeriesAndOther(type, streams.Values.ToList(), bucketing, top);

        return new MetricSeriesResult
        {
            Name = query.MetricName,
            Type = type,
            BucketWidthMs = bucketing.BucketNanos / 1_000_000,
            Series = series,
            Other = other
        };
    }

    // -------------------------------------------------------------------
    // Stream loaders — one per metric type. Each returns a Dictionary keyed by
    // "{metricId}\u0001{attributesJson}" (a stream identity, decision 22) mapping to per-bucket
    // aggregates. All SQL here is provider-neutral through the existing DapperReadRepository hooks
    // (BucketIndexExpr, AttributePredicate/LabelFilterClause) plus the two new hooks this phase
    // adds (BuildLastPerStreamBucketSql/BuildLastPerStreamSql — ROW_NUMBER by default, ClickHouse
    // overrides with argMax; see that type's own doc comment).
    // -------------------------------------------------------------------

    private static StreamAgg GetOrAddStream(Dictionary<string, StreamAgg> streams, long metricId, string? attributesJson)
    {
        var json = attributesJson ?? "";
        var key = $"{metricId}\u0001{json}";
        if (!streams.TryGetValue(key, out var s))
        {
            s = new StreamAgg { MetricId = metricId, AttributesJson = json, Attributes = DeserializeAttributes(json) };
            streams[key] = s;
        }
        return s;
    }

    /// <summary>Peeks the most recent point's <c>aggregation_temporality</c> for a metric name's streams, used to pick the delta vs. cumulative loading path. Defaults to CUMULATIVE (the more expensive, more careful path) when no data or an unrecognized value is found.</summary>
    private async Task<AggregationTemporality> PeekTemporalityAsync(DbConnection conn, string table, IList<long> metricIds, CancellationToken ct)
    {
        var value = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition($"""
            SELECT dp.aggregation_temporality
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metricIds)})
            ORDER BY dp.time_unix_nano DESC
            {PagingClause}
            """, new { limit = 1, offset = 0 }, cancellationToken: ct));

        return value != null && Enum.TryParse<AggregationTemporality>(value, out var t) ? t : AggregationTemporality.CUMULATIVE;
    }

    private async Task<Dictionary<string, StreamAgg>> LoadGaugeAsync(DbConnection conn, IList<long> metricIds,
        Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var valueExpr = CoalesceValueExpr();
        // Aliased `max_value` (with underscore), not `MaxValue`: MySQL reserves the bare identifier
        // MAXVALUE (used in its partitioning syntax, e.g. `PARTITION p VALUES LESS THAN MAXVALUE`),
        // so an unquoted `AS MaxValue` alias is a syntax error there — found via the Phase 4
        // integration tests. `max_value` sidesteps it and still binds to MetricPointRow.MaxValue via
        // Dapper's case/underscore-insensitive column matching, the same convention already relied
        // on for the raw `max_value` column selected (unaliased) elsewhere in this file.
        var rows = await conn.QueryAsync<MetricPointRow>(new CommandDefinition($"""
            SELECT dp.metric_id AS MetricId, dp.attributes_json AS AttributesJson, {bucketExpr} AS Bucket,
                   AVG({valueExpr}) AS AvgValue,
                   MIN({valueExpr}) AS MinValue,
                   MAX({valueExpr}) AS max_value
            FROM gauge_data_points dp
            WHERE dp.metric_id IN ({IdInList(metricIds)}) AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json, {bucketExpr}
            """, Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp),
            commandTimeout: timeoutSeconds, cancellationToken: ct));

        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in rows)
        {
            var stream = GetOrAddStream(streams, r.MetricId, r.AttributesJson);
            stream.Buckets[r.Bucket] = new BucketAgg { Avg = r.AvgValue, Min = r.MinValue, Max = r.MaxValue };
        }
        return streams;
    }

    /// <summary>
    /// The interval one delta point covers, <c>time − start</c>, or NULL when the point carries no
    /// usable start time (unset/0, or not before its own time) — so <c>SUM</c> over it totals only
    /// the intervals actually known.
    /// </summary>
    private const string CoveredNanosExpr =
        "CASE WHEN dp.start_time_unix_nano > 0 AND dp.start_time_unix_nano < dp.time_unix_nano " +
        "THEN dp.time_unix_nano - dp.start_time_unix_nano END";

    /// <summary>
    /// Per-second rate of a delta bucket: its total over the intervals its points cover. Falls back
    /// to the bucket width only for points with no start time, where no better interval exists.
    /// </summary>
    private static double? DeltaRate(double total, double? coveredNanos, Bucketing b)
    {
        var nanos = coveredNanos is > 0 ? coveredNanos.Value : b.BucketNanos;
        return total / (nanos / 1e9);
    }

    /// <summary>How a cumulative point's value relates to the stream's previous observation.</summary>
    private enum CumulativeStep
    {
        /// <summary>Same counter as the previous observation: the increase is the plain difference.</summary>
        Diff,
        /// <summary>
        /// A fresh counter — reset (value decreased or start time changed) or first observed inside
        /// the window after starting there — so its whole value accrued since its start time.
        /// </summary>
        Whole,
        /// <summary>No previous observation, and the counter started before the window: its increase within the window can't be known.</summary>
        Unknown
    }

    /// <summary>
    /// Classifies one cumulative point against the stream's previous observation (the pre-window
    /// baseline, or the last point of an earlier bucket) and returns the seconds its increase
    /// covers — used for every cumulative type so Sum, Histogram, ExpHistogram and Summary share
    /// one rule.
    /// </summary>
    private static (CumulativeStep Step, double? Seconds) ClassifyCumulativeStep(
        MetricPointRow row, long? prevTime, long? prevStart, bool decreased, long windowStartNano)
    {
        var start = row.StartTimeUnixNano is long s && s > 0 && s < row.TimeUnixNano ? s : (long?)null;

        if (prevTime is null)
        {
            return start is { } st && st >= windowStartNano
                ? (CumulativeStep.Whole, (row.TimeUnixNano - st) / 1e9)
                : (CumulativeStep.Unknown, null);
        }

        var elapsed = row.TimeUnixNano > prevTime.Value ? (row.TimeUnixNano - prevTime.Value) / 1e9 : (double?)null;
        if (decreased || row.StartTimeUnixNano != prevStart)
            return (CumulativeStep.Whole, start is { } rs ? (row.TimeUnixNano - rs) / 1e9 : elapsed);

        return (CumulativeStep.Diff, elapsed);
    }

    private static double? PerSecond(double increase, double? seconds) => seconds is > 0 ? increase / seconds.Value : null;

    private async Task<Dictionary<string, StreamAgg>> LoadSumAsync(DbConnection conn, IList<long> metricIds,
        Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var temporality = await PeekTemporalityAsync(conn, "sum_data_points", metricIds, ct);
        return temporality == AggregationTemporality.DELTA
            ? await LoadSumDeltaAsync(conn, metricIds, b, labelFilters, timeoutSeconds, ct)
            : await LoadSumCumulativeAsync(conn, metricIds, b, labelFilters, timeoutSeconds, ct);
    }

    private async Task<Dictionary<string, StreamAgg>> LoadSumDeltaAsync(DbConnection conn, IList<long> metricIds,
        Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var rows = await conn.QueryAsync<MetricPointRow>(new CommandDefinition($"""
            SELECT dp.metric_id AS MetricId, dp.attributes_json AS AttributesJson, {bucketExpr} AS Bucket,
                   SUM({CoalesceValueExpr()}) AS SumOfValue, SUM({CoveredNanosExpr}) AS CoveredNanos
            FROM sum_data_points dp
            WHERE dp.metric_id IN ({IdInList(metricIds)}) AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json, {bucketExpr}
            """, Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp),
            commandTimeout: timeoutSeconds, cancellationToken: ct));

        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in rows)
        {
            var stream = GetOrAddStream(streams, r.MetricId, r.AttributesJson);
            var value = r.SumOfValue ?? 0;
            stream.Buckets[r.Bucket] = new BucketAgg { Value = value, Rate = DeltaRate(value, r.CoveredNanos, b) };
        }
        return streams;
    }

    private static readonly string[] SumCumulativeCols = { "value_double", "value_int", "start_time_unix_nano" };

    private async Task<Dictionary<string, StreamAgg>> LoadSumCumulativeAsync(DbConnection conn, IList<long> metricIds,
        Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var idList = IdInList(metricIds);

        var windowClause = " AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end";
        var lastPerBucketSql = BuildLastPerStreamBucketSql("sum_data_points", idList, windowClause, labelClause, bucketExpr, SumCumulativeCols);
        var bucketRows = (await conn.QueryAsync<MetricPointRow>(new CommandDefinition(lastPerBucketSql,
            Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp),
            commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList();

        var baselineSql = BuildLastPerStreamSql("sum_data_points", idList, " AND dp.time_unix_nano < @start", labelClause, SumCumulativeCols);
        var baselineRows = (await conn.QueryAsync<MetricPointRow>(new CommandDefinition(baselineSql,
            Merge(new { start = b.StartNano }, lp), commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList();

        return ComputeCumulativeDeltas(bucketRows, baselineRows, b.StartNano);
    }

    /// <summary>
    /// Reset-detecting delta walk for cumulative sums: per stream, buckets are visited oldest-first
    /// (preceded by the pre-window baseline, if any) and each bucket's increase is classified by
    /// <see cref="ClassifyCumulativeStep"/> — the plain difference from the previous observation,
    /// the whole value for a counter that reset or started inside the window, and no bucket at all
    /// when neither applies (a long-running counter's first in-window point with no baseline: its
    /// in-window increase is unknowable, and reporting 0 would draw a false dip).
    /// </summary>
    private static Dictionary<string, StreamAgg> ComputeCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        static double ValueOf(MetricPointRow r) => r.ValueDouble ?? r.ValueInt ?? 0;

        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = baselineRows.ToDictionary(r => $"{r.MetricId}\u0001{r.AttributesJson}");

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            double? prevValue = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevValue = ValueOf(baseline);
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in group.OrderBy(r => r.Bucket))
            {
                var curVal = ValueOf(row);
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, curVal < prevValue, windowStartNano);
                if (step != CumulativeStep.Unknown)
                {
                    var delta = step == CumulativeStep.Diff ? curVal - prevValue!.Value : curVal;
                    GetOrAddStream(streams, row.MetricId, row.AttributesJson).Buckets[row.Bucket] =
                        new BucketAgg { Value = delta, Rate = PerSecond(delta, seconds) };
                }
                prevValue = curVal;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    private static readonly string[] HistogramCumulativeCols =
        { "count", "sum_value", "min_value", "max_value", "bucket_counts", "explicit_bounds", "start_time_unix_nano" };

    private static readonly string[] ExpHistogramCumulativeCols =
        { "count", "sum_value", "min_value", "max_value", "scale", "zero_count", "positive_offset", "positive_bucket_counts", "start_time_unix_nano" };

    private async Task<Dictionary<string, StreamAgg>> LoadDistributionAsync(DbConnection conn, string table, bool isExponential,
        IList<long> metricIds, Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var temporality = await PeekTemporalityAsync(conn, table, metricIds, ct);
        return temporality == AggregationTemporality.DELTA
            ? await LoadDistributionDeltaAsync(conn, table, isExponential, metricIds, b, labelFilters, timeoutSeconds, ct)
            : await LoadDistributionCumulativeAsync(conn, table, isExponential, metricIds, b, labelFilters, timeoutSeconds, ct);
    }

    private async Task<Dictionary<string, StreamAgg>> LoadDistributionCumulativeAsync(DbConnection conn, string table, bool isExponential,
        IList<long> metricIds, Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var idList = IdInList(metricIds);
        var cols = isExponential ? ExpHistogramCumulativeCols : HistogramCumulativeCols;

        var windowClause = " AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end";
        var lastPerBucketSql = BuildLastPerStreamBucketSql(table, idList, windowClause, labelClause, bucketExpr, cols);
        var bucketRows = (await conn.QueryAsync<MetricPointRow>(new CommandDefinition(lastPerBucketSql,
            Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp),
            commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList();

        var baselineSql = BuildLastPerStreamSql(table, idList, " AND dp.time_unix_nano < @start", labelClause, cols);
        var baselineRows = (await conn.QueryAsync<MetricPointRow>(new CommandDefinition(baselineSql,
            Merge(new { start = b.StartNano }, lp), commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList();

        return isExponential
            ? ComputeExpHistogramCumulativeDeltas(bucketRows, baselineRows, b.StartNano)
            : ComputeHistogramCumulativeDeltas(bucketRows, baselineRows, b.StartNano);
    }

    /// <summary>
    /// Histogram counterpart of <see cref="ComputeCumulativeDeltas"/>, under the same
    /// <see cref="ClassifyCumulativeStep"/> rule: bucket counts, count and sum are all turned into
    /// the increase since the previous observation (sum included — it is cumulative too, so
    /// passing it through raw would re-add the running total once per bucket). Min/Max stay as
    /// reported: a cumulative point's min/max cover the whole stream lifetime and can't be
    /// differenced.
    /// </summary>
    private static Dictionary<string, StreamAgg> ComputeHistogramCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = baselineRows.ToDictionary(r => $"{r.MetricId}\u0001{r.AttributesJson}");

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            var ordered = group.OrderBy(r => r.Bucket).ToList();
            var bounds = DeserializeDoubleArray(ordered.Select(r => r.ExplicitBounds).FirstOrDefault(x => x != null));

            long[]? prevCounts = null;
            double? prevSum = null, prevMin = null, prevMax = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevCounts = DeserializeLongArray(baseline.BucketCounts);
                prevSum = baseline.SumValue;
                prevMin = baseline.MinValue;
                prevMax = baseline.MaxValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in ordered)
            {
                var curCounts = DeserializeLongArray(row.BucketCounts) ?? Array.Empty<long>();
                var decreased = prevCounts != null && prevCounts.Length == curCounts.Length && curCounts.Sum() < prevCounts.Sum();
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, decreased, windowStartNano);

                if (step != CumulativeStep.Unknown)
                {
                    // A changed bucket layout can't be differenced element-wise; count it whole.
                    var whole = step == CumulativeStep.Whole || prevCounts == null || prevCounts.Length != curCounts.Length;
                    long[] delta;
                    if (whole)
                    {
                        delta = curCounts;
                    }
                    else
                    {
                        delta = new long[curCounts.Length];
                        for (var i = 0; i < curCounts.Length; i++)
                            delta[i] = Math.Max(0, curCounts[i] - prevCounts![i]);
                    }

                    // See MetricBucketPoint.MinMaxApproximate: exact when `whole` (the reported
                    // min/max cover exactly this bucket's own lifetime) or when the lifetime extreme
                    // moved (it necessarily moved within this interval); otherwise a bound estimate,
                    // tightened to the edge of the highest/lowest bucket that got new observations.
                    var (estMax, maxApprox) = EstimateMax(row.MaxValue, prevMax, whole, HighestNonEmptyUpperBound(delta, bounds));
                    var (estMin, minApprox) = EstimateMin(row.MinValue, prevMin, whole, LowestNonEmptyLowerBound(delta, bounds));

                    var stream = GetOrAddStream(streams, row.MetricId, row.AttributesJson);
                    stream.Bounds ??= bounds;
                    var count = delta.Sum();
                    stream.Buckets[row.Bucket] = new BucketAgg
                    {
                        Count = count,
                        Sum = whole ? row.SumValue : SumIncrease(row.SumValue, prevSum),
                        Min = estMin,
                        Max = estMax,
                        MinMaxApproximate = minApprox || maxApprox,
                        Counts = delta,
                        Rate = PerSecond(count, seconds)
                    };
                }

                prevCounts = curCounts;
                prevSum = row.SumValue;
                prevMin = row.MinValue;
                prevMax = row.MaxValue;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    /// <summary>A cumulative sum's increase over the previous observation, clamped at 0 (float noise / non-monotonic sums).</summary>
    private static double? SumIncrease(double? current, double? previous) =>
        current is { } c && previous is { } p ? Math.Max(0, c - p) : current;

    /// <summary>
    /// Upper edge (explicit bound) of the highest-indexed bucket that received a new (delta &gt; 0)
    /// observation this interval, or null when that bucket is the unbounded overflow bucket (no
    /// finite edge) or nothing in <paramref name="delta"/> is non-empty. Used to tighten a cumulative
    /// histogram bucket's approximate Max estimate (see <see cref="MetricBucketPoint.MinMaxApproximate"/>).
    /// </summary>
    private static double? HighestNonEmptyUpperBound(long[] delta, double[]? bounds)
    {
        for (var i = delta.Length - 1; i >= 0; i--)
        {
            if (delta[i] <= 0) continue;
            return bounds != null && i < bounds.Length ? bounds[i] : null;
        }
        return null;
    }

    /// <summary>Lower-edge counterpart of <see cref="HighestNonEmptyUpperBound"/>, for the approximate Min estimate. The first bucket's lower edge is 0 (explicit-bounds histograms assume non-negative values).</summary>
    private static double? LowestNonEmptyLowerBound(long[] delta, double[]? bounds)
    {
        for (var i = 0; i < delta.Length; i++)
        {
            if (delta[i] <= 0) continue;
            return i == 0 ? 0.0 : bounds != null && i - 1 < bounds.Length ? bounds[i - 1] : null;
        }
        return null;
    }

    /// <summary>
    /// Estimates one bucket's Max from a cumulative point's lifetime <paramref name="curMax"/>:
    /// exact when the lifetime max grew since <paramref name="prevMax"/> (the growth necessarily
    /// happened in this bucket's interval) or when <paramref name="exactWhole"/> (the counter
    /// started/reset here, so the whole lifetime value belongs to this bucket) — otherwise an
    /// upper-bound estimate, tightened against <paramref name="upperEdge"/> when known.
    /// </summary>
    private static (double? Value, bool Approximate) EstimateMax(double? curMax, double? prevMax, bool exactWhole, double? upperEdge)
    {
        if (curMax is not { } cm) return (null, false);
        if (exactWhole || prevMax is null || cm > prevMax.Value) return (cm, false);
        return upperEdge is { } u ? (Math.Min(u, cm), true) : (cm, true);
    }

    /// <summary>Min counterpart of <see cref="EstimateMax"/>.</summary>
    private static (double? Value, bool Approximate) EstimateMin(double? curMin, double? prevMin, bool exactWhole, double? lowerEdge)
    {
        if (curMin is not { } cm) return (null, false);
        if (exactWhole || prevMin is null || cm < prevMin.Value) return (cm, false);
        return lowerEdge is { } l ? (Math.Max(l, cm), true) : (cm, true);
    }

    /// <summary>Exponential-histogram counterpart of <see cref="ComputeHistogramCumulativeDeltas"/>.</summary>
    private static Dictionary<string, StreamAgg> ComputeExpHistogramCumulativeDeltas(
        List<MetricPointRow> bucketRows, List<MetricPointRow> baselineRows, long windowStartNano)
    {
        var streams = new Dictionary<string, StreamAgg>();
        var baselineByStream = baselineRows.ToDictionary(r => $"{r.MetricId}\u0001{r.AttributesJson}");
        var globalTargetScale = ComputeGlobalMinScale(bucketRows, baselineRows);

        foreach (var group in bucketRows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            Dictionary<long, long>? prevMap = null;
            long? prevZero = null;
            double? prevSum = null, prevMin = null, prevMax = null;
            long? prevStart = null, prevTime = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevMap = DownscaleRow(baseline, globalTargetScale);
                prevZero = baseline.ZeroCount;
                prevSum = baseline.SumValue;
                prevMin = baseline.MinValue;
                prevMax = baseline.MaxValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var row in group.OrderBy(r => r.Bucket))
            {
                var curMap = DownscaleRow(row, globalTargetScale);
                var curZero = row.ZeroCount ?? 0;
                var curTotal = curMap.Values.Sum() + curZero;
                var prevTotal = (prevMap?.Values.Sum() ?? 0) + (prevZero ?? 0);
                var (step, seconds) = ClassifyCumulativeStep(row, prevTime, prevStart, prevMap != null && curTotal < prevTotal, windowStartNano);

                if (step != CumulativeStep.Unknown)
                {
                    var whole = step == CumulativeStep.Whole || prevMap == null;
                    var delta = new Dictionary<long, long>();
                    foreach (var kv in curMap)
                        delta[kv.Key] = whole ? kv.Value : Math.Max(0, kv.Value - prevMap!.GetValueOrDefault(kv.Key));
                    var deltaZero = whole || prevZero == null ? curZero : Math.Max(0, curZero - prevZero.Value);
                    var count = delta.Values.Sum() + deltaZero;

                    // Same exactness rule as the explicit-bounds histogram (see
                    // MetricBucketPoint.MinMaxApproximate), but with no tightened edge: recovering a
                    // real bucket boundary from an (index, scale) pair isn't implemented anywhere in
                    // this read path yet (see FinalizeExpHistogramBounds's doc comment on that same
                    // gap), so an inexact point here is flagged approximate at the lifetime value
                    // rather than given a falsely-precise tightened number.
                    var (estMax, maxApprox) = EstimateMax(row.MaxValue, prevMax, whole, null);
                    var (estMin, minApprox) = EstimateMin(row.MinValue, prevMin, whole, null);

                    GetOrAddStream(streams, row.MetricId, row.AttributesJson).Buckets[row.Bucket] = new BucketAgg
                    {
                        Count = count,
                        Sum = whole ? row.SumValue : SumIncrease(row.SumValue, prevSum),
                        Min = estMin,
                        Max = estMax,
                        MinMaxApproximate = minApprox || maxApprox,
                        SparseCounts = delta,
                        ZeroCountValue = deltaZero,
                        Rate = PerSecond(count, seconds)
                    };
                }

                prevMap = curMap;
                prevZero = curZero;
                prevSum = row.SumValue;
                prevMin = row.MinValue;
                prevMax = row.MaxValue;
                prevStart = row.StartTimeUnixNano;
                prevTime = row.TimeUnixNano;
            }
        }
        return streams;
    }

    private static int ComputeGlobalMinScale(params IEnumerable<MetricPointRow>[] rowSets)
    {
        var min = int.MaxValue;
        foreach (var set in rowSets)
            foreach (var r in set)
                if (r.Scale is { } s && s < min) min = s;
        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>
    /// Downscales one exponential-histogram row's positive buckets to <paramref name="targetScale"/>
    /// (the coarsest/smallest scale observed anywhere in this query's result set), keyed by
    /// absolute exponent index — ported from the client's <c>normalizeExpHistogramSeries</c>
    /// (<c>chart.utils.ts</c>), which this phase retires from the client in favor of this
    /// server-side equivalent. Negative buckets are not represented in the read path today,
    /// matching the ported original.
    /// </summary>
    private static Dictionary<long, long> DownscaleRow(MetricPointRow row, int targetScale)
    {
        var map = new Dictionary<long, long>();
        var counts = DeserializeLongArray(row.PositiveBucketCounts);
        if (counts == null || row.Scale is not { } scale) return map;

        var factor = Math.Pow(2, scale - targetScale); // >= 1
        var offset = row.PositiveOffset ?? 0;
        for (var k = 0; k < counts.Length; k++)
        {
            if (counts[k] == 0) continue;
            var jPrime = (long)Math.Floor((offset + k) / factor);
            map[jPrime] = map.GetValueOrDefault(jPrime) + counts[k];
        }
        return map;
    }

    private async Task<Dictionary<string, StreamAgg>> LoadDistributionDeltaAsync(DbConnection conn, string table, bool isExponential,
        IList<long> metricIds, Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var windowParams = Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp);

        // 1) Cheap aggregate: count/sum/min/max per (stream, bucket) — a handful of scalar rows.
        // `max_value`, not `MaxValue`: MySQL reserves the bare identifier MAXVALUE (its partitioning
        // syntax) — see LoadGaugeAsync's identical fix/doc comment, found via the same test failure.
        var aggRows = await conn.QueryAsync<MetricPointRow>(new CommandDefinition($"""
            SELECT dp.metric_id AS MetricId, dp.attributes_json AS AttributesJson, {bucketExpr} AS Bucket,
                   SUM(dp.count) AS Count, SUM(dp.sum_value) AS SumValue, MIN(dp.min_value) AS MinValue, MAX(dp.max_value) AS max_value,
                   SUM({CoveredNanosExpr}) AS CoveredNanos
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metricIds)}) AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end{labelClause}
            GROUP BY dp.metric_id, dp.attributes_json, {bucketExpr}
            """, windowParams, commandTimeout: timeoutSeconds, cancellationToken: ct));

        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in aggRows)
        {
            var stream = GetOrAddStream(streams, r.MetricId, r.AttributesJson);
            var count = r.Count ?? 0;
            stream.Buckets[r.Bucket] = new BucketAgg
            {
                Count = count, Sum = r.SumValue, Min = r.MinValue, Max = r.MaxValue,
                Rate = DeltaRate(count, r.CoveredNanos, b)
            };
        }

        // 2) Bucket-count arrays can't be summed in SQL (JSON text columns), so merge them in C#
        // over an UNBUFFERED read (decision "Delta temporality" under Phase 4): the database still
        // sends every matching row, but only the per-(stream,bucket) accumulator — bounded by
        // streams × buckets — is held in memory at once.
        var rawCols = isExponential
            ? "dp.scale AS Scale, dp.zero_count AS ZeroCount, dp.positive_offset AS PositiveOffset, dp.positive_bucket_counts AS PositiveBucketCounts"
            : "dp.bucket_counts AS BucketCounts, dp.explicit_bounds AS ExplicitBounds";

        int globalTargetScale = 0;
        if (isExponential)
        {
            globalTargetScale = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"""
                SELECT MIN(dp.scale) FROM {table} dp
                WHERE dp.metric_id IN ({IdInList(metricIds)}) AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end{labelClause}
                """, windowParams, commandTimeout: timeoutSeconds, cancellationToken: ct)) ?? 0;
        }

        var rawSql = $"""
            SELECT dp.metric_id AS MetricId, dp.attributes_json AS AttributesJson, {bucketExpr} AS Bucket, {rawCols}
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metricIds)}) AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end{labelClause}
            """;

        using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(
            rawSql, windowParams, commandTimeout: timeoutSeconds, cancellationToken: ct));
        var rawRows = reader.Parse<MetricPointRow>();
        foreach (var row in rawRows)
        {
            var stream = GetOrAddStream(streams, row.MetricId, row.AttributesJson);
            if (!stream.Buckets.TryGetValue(row.Bucket, out var bucket))
            {
                bucket = new BucketAgg { Count = 0 };
                stream.Buckets[row.Bucket] = bucket;
            }

            if (isExponential)
            {
                bucket.SparseCounts ??= new Dictionary<long, long>();
                var m = DownscaleRow(row, globalTargetScale);
                foreach (var kv in m) bucket.SparseCounts[kv.Key] = bucket.SparseCounts.GetValueOrDefault(kv.Key) + kv.Value;
                bucket.ZeroCountValue = (bucket.ZeroCountValue ?? 0) + (row.ZeroCount ?? 0);
            }
            else
            {
                var bounds = DeserializeDoubleArray(row.ExplicitBounds);
                var counts = DeserializeLongArray(row.BucketCounts);
                stream.Bounds ??= bounds;
                if (counts != null && stream.Bounds != null && counts.Length == stream.Bounds.Length + 1)
                {
                    bucket.Counts ??= new long[counts.Length];
                    if (bucket.Counts.Length == counts.Length)
                        for (var i = 0; i < counts.Length; i++) bucket.Counts[i] += counts[i];
                }
            }
        }

        return streams;
    }

    private static readonly string[] SummaryCols = { "count", "sum_value", "quantile_values", "start_time_unix_nano" };

    /// <summary>
    /// Summaries are cumulative by definition: each bucket keeps its last point's quantile snapshot
    /// as-is, while count/sum become the increase since the previous observation under the same
    /// <see cref="ClassifyCumulativeStep"/> rule as the other cumulative types (null when unknowable).
    /// </summary>
    private async Task<Dictionary<string, StreamAgg>> LoadSummaryAsync(DbConnection conn, IList<long> metricIds,
        Bucketing b, Dictionary<string, string>? labelFilters, int timeoutSeconds, CancellationToken ct)
    {
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var bucketExpr = ClampedBucketExpr();
        var idList = IdInList(metricIds);
        var sql = BuildLastPerStreamBucketSql("summary_data_points", idList,
            " AND dp.time_unix_nano >= @start AND dp.time_unix_nano < @end", labelClause, bucketExpr, SummaryCols);

        var rows = await conn.QueryAsync<MetricPointRow>(new CommandDefinition(sql,
            Merge(new { start = b.StartNano, end = b.EndNano, bucketNanos = b.BucketNanos, pointsMinus1 = b.Points - 1 }, lp),
            commandTimeout: timeoutSeconds, cancellationToken: ct));

        var baselineSql = BuildLastPerStreamSql("summary_data_points", idList, " AND dp.time_unix_nano < @start", labelClause, SummaryCols);
        var baselineByStream = (await conn.QueryAsync<MetricPointRow>(new CommandDefinition(baselineSql,
                Merge(new { start = b.StartNano }, lp), commandTimeout: timeoutSeconds, cancellationToken: ct)))
            .ToDictionary(r => $"{r.MetricId}\u0001{r.AttributesJson}");

        var streams = new Dictionary<string, StreamAgg>();
        foreach (var group in rows.GroupBy(r => $"{r.MetricId}\u0001{r.AttributesJson}"))
        {
            long? prevCount = null, prevStart = null, prevTime = null;
            double? prevSum = null;
            if (baselineByStream.TryGetValue(group.Key, out var baseline))
            {
                prevCount = baseline.Count;
                prevSum = baseline.SumValue;
                prevStart = baseline.StartTimeUnixNano;
                prevTime = baseline.TimeUnixNano;
            }

            foreach (var r in group.OrderBy(r => r.Bucket))
            {
                var (step, seconds) = ClassifyCumulativeStep(r, prevTime, prevStart, r.Count < prevCount, b.StartNano);
                long? count = step switch
                {
                    CumulativeStep.Diff => r.Count - prevCount,
                    CumulativeStep.Whole => r.Count,
                    _ => null
                };
                double? sum = step switch
                {
                    CumulativeStep.Diff => SumIncrease(r.SumValue, prevSum),
                    CumulativeStep.Whole => r.SumValue,
                    _ => null
                };

                var quantiles = DeserializeArray<QuantileValueModel>(r.QuantileValues);
                GetOrAddStream(streams, r.MetricId, r.AttributesJson).Buckets[r.Bucket] = new BucketAgg
                {
                    Count = count,
                    Sum = sum,
                    Rate = count is { } c ? PerSecond(c, seconds) : null,
                    Quantiles = quantiles?.Select(q => q.Quantile).ToList(),
                    QuantileValues = quantiles?.Select(q => q.Value).ToList()
                };

                prevCount = r.Count;
                prevSum = r.SumValue;
                prevStart = r.StartTimeUnixNano;
                prevTime = r.TimeUnixNano;
            }
        }
        return streams;
    }

    // -------------------------------------------------------------------
    // Stream -> display series merge, top-N ranking and "other" folding (decisions 22-23, 42).
    // -------------------------------------------------------------------

    /// <summary>
    /// Histogram/exponential-histogram/summary streams merge into one display series per service,
    /// ignoring labels — mirroring the pre-Phase-4 client, which always computed one windowed
    /// aggregate across every series for these three types (<c>aggregateHistogramWindows</c>/
    /// <c>aggregateSummaryWindows</c> over the whole <c>multi.series</c> list, never per label set).
    /// Gauge/Sum keep the label-inclusive <see cref="SeriesKey"/> (one line per pod/label-set), the
    /// client's existing per-series behavior for those types. Without this, decision 42's
    /// mismatched-bucket-layout grouping could only ever trigger for two streams sharing the exact
    /// same label set — an edge case (a service that changed its histogram config without changing
    /// labels) far narrower than its actual purpose: multiple pods of one service, each fine on its
    /// own, disagreeing on bucket layout. Found via the Phase 4 integration tests (the mismatched-
    /// layout test seeds two pods of the same service with different <c>k8s.pod.name</c> values).
    /// </summary>
    private static bool IsDistributionType(MetricType type) =>
        type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM or MetricType.SUMMARY;

    private (List<DisplayMetricSeries> Series, OtherMetricSeries? Other) BuildDisplaySeriesAndOther(
        MetricType type, List<StreamAgg> streams, Bucketing b, int top)
    {
        var isDistribution = IsDistributionType(type);
        var byDisplayKey = streams.GroupBy(s => isDistribution
                ? s.ServiceName
                : SeriesKey(s.ServiceName, ToLabelDictionary(s.Attributes)))
            .Select(g => g.ToList())
            .ToList();

        var built = new List<(DisplayMetricSeries Series, double Rank)>();
        foreach (var group in byDisplayKey)
        {
            // Distributions fold every contributing pod's labels away (see IsDistributionType's
            // doc comment); Gauge/Sum keep the single stream's own label set.
            var labels = isDistribution ? new Dictionary<string, string>() : ToLabelDictionary(group[0].Attributes);
            var serviceName = group[0].ServiceName;
            var (points, excluded) = MergeBucketsForDisplaySeries(type, group, b);
            var series = new DisplayMetricSeries
            {
                SeriesName = BuildSeriesDisplayName(serviceName, labels),
                ServiceName = serviceName,
                Labels = labels,
                Points = points,
                ExcludedStreams = excluded > 0 ? excluded : null
            };
            built.Add((series, RankingMeasure(type, points)));
        }

        var ranked = built.OrderByDescending(x => x.Rank).ToList();
        var kept = ranked.Take(top).Select(x => x.Series).ToList();
        var rest = ranked.Skip(top).Select(x => x.Series).ToList();

        OtherMetricSeries? other = null;
        if (rest.Count > 0)
        {
            var (points, excluded) = MergeDisplaySeriesIntoOther(type, rest, b);
            other = new OtherMetricSeries { SeriesCount = rest.Count, Points = points, ExcludedStreams = excluded > 0 ? excluded : null };
        }

        return (kept, other);
    }

    /// <summary>The measure display series are ranked by (decision 23): total observed magnitude across the window — mirrors the client's former <c>groupValue</c> (window increase for counters, Σ for deltas/histograms/summaries).</summary>
    private static double RankingMeasure(MetricType type, List<MetricBucketPoint> points) => type switch
    {
        MetricType.GAUGE => points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).DefaultIfEmpty(0).Average(),
        MetricType.SUM => points.Sum(p => p.Value ?? 0),
        MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM or MetricType.SUMMARY => points.Sum(p => p.Count ?? 0),
        _ => 0
    };

    private (List<MetricBucketPoint> Points, int Excluded) MergeBucketsForDisplaySeries(MetricType type, List<StreamAgg> streams, Bucketing b)
    {
        var excluded = 0;
        if (type == MetricType.HISTOGRAM)
        {
            var byBounds = streams.GroupBy(s => BoundsSignature(s.Bounds)).ToList();
            if (byBounds.Count > 1)
            {
                var winner = byBounds.OrderByDescending(g => g.Sum(s => s.Buckets.Values.Sum(bk => bk.Count ?? 0))).First();
                excluded = streams.Count - winner.Count();
                streams = winner.ToList();
            }
        }

        var points = new List<MetricBucketPoint>(b.Points);
        for (var i = 0; i < b.Points; i++)
            points.Add(MergeBucketAcrossStreams(type, streams, i, TimeConversion.UnixNanoToDateTime(b.StartNano + i * b.BucketNanos)));

        if (type == MetricType.EXPONENTIAL_HISTOGRAM)
            FinalizeExpHistogramBounds(points, streams);

        return (points, excluded);
    }

    private static string BoundsSignature(double[]? bounds)
        => bounds == null ? "" : string.Join(",", bounds.Select(v => v.ToString("R")));

    private MetricBucketPoint MergeBucketAcrossStreams(MetricType type, List<StreamAgg> streams, int bucketIdx, DateTime ts)
    {
        var contributing = streams.Select(s => s.Buckets.GetValueOrDefault(bucketIdx)).Where(x => x != null).Select(x => x!).ToList();
        var point = new MetricBucketPoint { Timestamp = ts };
        if (contributing.Count == 0) return point;

        switch (type)
        {
            case MetricType.GAUGE:
                var avgs = contributing.Where(c => c.Avg.HasValue).Select(c => c.Avg!.Value).ToList();
                if (avgs.Count > 0) point.Value = avgs.Average();
                var mins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (mins.Count > 0) point.Min = mins.Min();
                var maxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (maxs.Count > 0) point.Max = maxs.Max();
                break;

            case MetricType.SUM:
                point.Value = contributing.Sum(c => c.Value ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                break;

            case MetricType.HISTOGRAM:
                point.Count = contributing.Sum(c => c.Count ?? 0);
                point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                var hMins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (hMins.Count > 0) point.Min = hMins.Min();
                var hMaxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (hMaxs.Count > 0) point.Max = hMaxs.Max();
                point.MinMaxApproximate = contributing.Any(c => c.MinMaxApproximate);
                var len = contributing.FirstOrDefault(c => c.Counts != null)?.Counts?.Length;
                if (len is { } l)
                {
                    var merged = new long[l];
                    foreach (var c in contributing)
                        if (c.Counts != null && c.Counts.Length == l)
                            for (var i = 0; i < l; i++) merged[i] += c.Counts[i];
                    point.BucketCounts = merged.ToList();
                    point.BucketBounds = streams[0].Bounds?.ToList();
                }
                break;

            case MetricType.EXPONENTIAL_HISTOGRAM:
                point.Count = contributing.Sum(c => c.Count ?? 0);
                point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                var eMins = contributing.Where(c => c.Min.HasValue).Select(c => c.Min!.Value).ToList();
                if (eMins.Count > 0) point.Min = eMins.Min();
                var eMaxs = contributing.Where(c => c.Max.HasValue).Select(c => c.Max!.Value).ToList();
                if (eMaxs.Count > 0) point.Max = eMaxs.Max();
                point.MinMaxApproximate = contributing.Any(c => c.MinMaxApproximate);
                // BucketCounts/BucketBounds are filled in by FinalizeExpHistogramBounds once every
                // bucket's sparse map is known (needs the series-wide index range).
                break;

            case MetricType.SUMMARY:
                // Count/Sum are null for a stream whose increase is unknowable (see LoadSummaryAsync) —
                // left null rather than 0 when no stream knows it, so no false dip is charted.
                if (contributing.Any(c => c.Count.HasValue)) point.Count = contributing.Sum(c => c.Count ?? 0);
                if (contributing.Any(c => c.Sum.HasValue)) point.Sum = contributing.Sum(c => c.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(c => c.Rate));
                point.IsApproximate = contributing.Count > 1;
                var withQuantiles = contributing.Where(c => c.Quantiles is { Count: > 0 }).ToList();
                if (withQuantiles.Count > 0)
                {
                    var qLen = withQuantiles[0].Quantiles!.Count;
                    if (withQuantiles.All(c => c.QuantileValues!.Count == qLen))
                    {
                        point.Quantiles = withQuantiles[0].Quantiles;
                        point.QuantileValues = Enumerable.Range(0, qLen)
                            .Select(i => withQuantiles.Average(c => c.QuantileValues![i]))
                            .ToList();
                    }
                }
                break;
        }

        return point;
    }

    /// <summary>
    /// After every bucket's sparse exponential-histogram map is merged (decision 22/42's exp-histogram
    /// note), computes the display series' own index range once and materializes each bucket's
    /// dense <c>BucketCounts</c>/<c>BucketBounds</c> against it (zero-filling buckets with no
    /// observation at a given index) — the per-display-series equivalent of the ported
    /// <c>normalizeExpHistogramSeries</c>.
    /// </summary>
    private void FinalizeExpHistogramBounds(List<MetricBucketPoint> points, List<StreamAgg> streams)
    {
        var sparseByBucket = new Dictionary<int, (Dictionary<long, long> Map, long Zero)>();
        for (var i = 0; i < points.Count; i++)
        {
            var contributing = streams.Select(s => s.Buckets.GetValueOrDefault(i)).Where(x => x?.SparseCounts != null).Select(x => x!).ToList();
            if (contributing.Count == 0) continue;
            var map = new Dictionary<long, long>();
            long zero = 0;
            foreach (var c in contributing)
            {
                foreach (var kv in c.SparseCounts!) map[kv.Key] = map.GetValueOrDefault(kv.Key) + kv.Value;
                zero += c.ZeroCountValue ?? 0;
            }
            sparseByBucket[i] = (map, zero);
        }
        if (sparseByBucket.Count == 0) return;

        var minIdx = sparseByBucket.Values.SelectMany(v => v.Map.Keys).DefaultIfEmpty(0).Min();
        var maxIdx = sparseByBucket.Values.SelectMany(v => v.Map.Keys).DefaultIfEmpty(0).Max();
        var n = sparseByBucket.Values.Any(v => v.Map.Count > 0) ? (int)(maxIdx - minIdx + 1) : 0;

        // Target scale isn't tracked per display series (it was resolved once, globally, at load
        // time — see LoadDistributionDeltaAsync/ComputeExpHistogramCumulativeDeltas) so the bound
        // values here use log2Base = 1 (i.e. bucket index only); callers that need the true bound
        // *value* recompute it from BucketCounts.Length/positive index — recorded as a known gap,
        // see the deviations note in the plan.
        var bounds = new List<double>();
        for (var m = 0; m < n; m++) bounds.Add(minIdx + m);

        foreach (var (bucketIdx, (map, zero)) in sparseByBucket)
        {
            var counts = new long[n + 1];
            counts[0] = zero;
            foreach (var kv in map) counts[kv.Key - minIdx + 1] = kv.Value;
            points[bucketIdx].BucketCounts = counts.ToList();
            points[bucketIdx].BucketBounds = bounds;
        }
    }

    private (List<MetricBucketPoint> Points, int Excluded) MergeDisplaySeriesIntoOther(MetricType type, List<DisplayMetricSeries> excludedSeries, Bucketing b)
    {
        var totalExcludedStreams = excludedSeries.Sum(s => s.ExcludedStreams ?? 0);
        var points = new List<MetricBucketPoint>(b.Points);

        if (type == MetricType.HISTOGRAM)
        {
            var byLen = excludedSeries.GroupBy(s => s.Points.FirstOrDefault(p => p.BucketCounts != null)?.BucketCounts?.Count ?? -1).ToList();
            if (byLen.Count > 1)
            {
                var winner = byLen.OrderByDescending(g => g.Sum(s => s.Points.Sum(p => p.Count ?? 0))).First();
                totalExcludedStreams += excludedSeries.Count - winner.Count();
                excludedSeries = winner.ToList();
            }
        }

        for (var i = 0; i < b.Points; i++)
        {
            var ts = TimeConversion.UnixNanoToDateTime(b.StartNano + i * b.BucketNanos);
            var contributing = excludedSeries.Select(s => i < s.Points.Count ? s.Points[i] : null).Where(p => p != null).Select(p => p!).ToList();
            points.Add(MergePointsOfType(type, contributing, ts));
        }

        return (points, totalExcludedStreams);
    }

    /// <summary>Σ of the known values, or null when none is known (a rate nobody could measure isn't 0/s).</summary>
    private static double? SumOrNull(IEnumerable<double?> values)
    {
        double? total = null;
        foreach (var v in values)
            if (v.HasValue) total = (total ?? 0) + v.Value;
        return total;
    }

    private static MetricBucketPoint MergePointsOfType(MetricType type, List<MetricBucketPoint> contributing, DateTime ts)
    {
        var point = new MetricBucketPoint { Timestamp = ts };
        var withValue = contributing.Where(p => p.Value.HasValue || p.Count.HasValue || p.Quantiles != null).ToList();
        if (withValue.Count == 0) return point;

        switch (type)
        {
            case MetricType.GAUGE:
                var avgs = contributing.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).ToList();
                if (avgs.Count > 0) point.Value = avgs.Average();
                var mins = contributing.Where(p => p.Min.HasValue).Select(p => p.Min!.Value).ToList();
                if (mins.Count > 0) point.Min = mins.Min();
                var maxs = contributing.Where(p => p.Max.HasValue).Select(p => p.Max!.Value).ToList();
                if (maxs.Count > 0) point.Max = maxs.Max();
                break;
            case MetricType.SUM:
                point.Value = contributing.Sum(p => p.Value ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                break;
            case MetricType.HISTOGRAM:
            case MetricType.EXPONENTIAL_HISTOGRAM:
                point.Count = contributing.Sum(p => p.Count ?? 0);
                point.Sum = contributing.Sum(p => p.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                var hoMins = contributing.Where(p => p.Min.HasValue).Select(p => p.Min!.Value).ToList();
                if (hoMins.Count > 0) point.Min = hoMins.Min();
                var hoMaxs = contributing.Where(p => p.Max.HasValue).Select(p => p.Max!.Value).ToList();
                if (hoMaxs.Count > 0) point.Max = hoMaxs.Max();
                point.MinMaxApproximate = contributing.Any(p => p.MinMaxApproximate);
                var len = contributing.FirstOrDefault(p => p.BucketCounts != null)?.BucketCounts?.Count;
                if (len is { } l)
                {
                    var merged = new long[l];
                    foreach (var p in contributing)
                        if (p.BucketCounts != null && p.BucketCounts.Count == l)
                            for (var i = 0; i < l; i++) merged[i] += p.BucketCounts[i];
                    point.BucketCounts = merged.ToList();
                    point.BucketBounds = contributing.FirstOrDefault(p => p.BucketBounds != null)?.BucketBounds;
                }
                break;
            case MetricType.SUMMARY:
                if (contributing.Any(p => p.Count.HasValue)) point.Count = contributing.Sum(p => p.Count ?? 0);
                if (contributing.Any(p => p.Sum.HasValue)) point.Sum = contributing.Sum(p => p.Sum ?? 0);
                point.Rate = SumOrNull(contributing.Select(p => p.Rate));
                point.IsApproximate = true;
                var withQuantiles = contributing.Where(p => p.Quantiles is { Count: > 0 }).ToList();
                if (withQuantiles.Count > 0)
                {
                    var qLen = withQuantiles[0].Quantiles!.Count;
                    if (withQuantiles.All(p => p.QuantileValues!.Count == qLen))
                    {
                        point.Quantiles = withQuantiles[0].Quantiles;
                        point.QuantileValues = Enumerable.Range(0, qLen).Select(i => withQuantiles.Average(p => p.QuantileValues![i])).ToList();
                    }
                }
                break;
        }
        return point;
    }

    // =========================================================================
    // "LAST PER STREAM(-BUCKET)" SQL DIALECT HOOKS (Phase 4)
    // =========================================================================

    /// <summary>
    /// One row per <c>(metric_id, attributes_json, bucket)</c>, holding <paramref name="valueColumns"/>
    /// from whichever row has the greatest <c>time_unix_nano</c> in that group — the "last point in
    /// this stream-bucket" SQL cumulative sums/histograms need (decision 22). Default implementation
    /// (PostgreSQL, Timescale, SqlServer, MySQL 8+) uses <c>ROW_NUMBER() OVER (PARTITION BY ...)</c>;
    /// ClickHouse overrides with <c>argMax</c> per column instead (its window-function support is
    /// there but <c>argMax</c> is the idiomatic, cheaper form for this exact shape — see the plan's
    /// Phase 4 text). <paramref name="idInList"/>/<paramref name="timeClause"/>/<paramref name="labelClause"/>
    /// are pre-built fragments (the caller already bound their parameters); <paramref name="bucketExpr"/>
    /// is <see cref="ClampedBucketExpr"/>'s SQL text.
    /// </summary>
    protected virtual string BuildLastPerStreamBucketSql(string table, string idInList, string timeClause,
        string labelClause, string bucketExpr, IReadOnlyList<string> valueColumns)
    {
        var dpCols = string.Join(", ", valueColumns.Select(c => $"dp.{c}"));
        return $"""
            SELECT metric_id, attributes_json, bucket, time_unix_nano, {string.Join(", ", valueColumns)}
            FROM (
                SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json, {bucketExpr} AS bucket,
                       dp.time_unix_nano AS time_unix_nano, {dpCols},
                       ROW_NUMBER() OVER (PARTITION BY dp.metric_id, dp.attributes_json, {bucketExpr} ORDER BY dp.time_unix_nano DESC) AS rn
                FROM {table} dp
                WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            ) ranked
            WHERE rn = 1
            """;
    }

    /// <summary>Same as <see cref="BuildLastPerStreamBucketSql"/> but with no bucket partition — the pre-window baseline row per stream (last point before <c>@start</c>).</summary>
    protected virtual string BuildLastPerStreamSql(string table, string idInList, string timeClause,
        string labelClause, IReadOnlyList<string> valueColumns)
    {
        var dpCols = string.Join(", ", valueColumns.Select(c => $"dp.{c}"));
        return $"""
            SELECT metric_id, attributes_json, time_unix_nano, {string.Join(", ", valueColumns)}
            FROM (
                SELECT dp.metric_id AS metric_id, dp.attributes_json AS attributes_json,
                       dp.time_unix_nano AS time_unix_nano, {dpCols},
                       ROW_NUMBER() OVER (PARTITION BY dp.metric_id, dp.attributes_json ORDER BY dp.time_unix_nano DESC) AS rn
                FROM {table} dp
                WHERE dp.metric_id IN ({idInList}){timeClause}{labelClause}
            ) ranked
            WHERE rn = 1
            """;
    }

    // =========================================================================
    // EXEMPLARS (Phase 4, decision 26)
    // =========================================================================

    /// <summary>
    /// Analytics-tier default (decision 26): real keyset paging, newest first, over the data
    /// points that carry at least one exemplar. <c>SqlServerMetricReadRepository</c>/
    /// <c>MySqlMetricReadRepository</c> override this back down to
    /// <see cref="GetMetricExemplarsCappedAsync"/> for the standard tier's newest-500 behavior.
    ///
    /// <b>Deviation from the plan's literal text</b>: the plan describes unnesting individual
    /// exemplars in SQL (<c>jsonb_array_elements … WITH ORDINALITY</c> on PostgreSQL/Timescale,
    /// <c>arrayJoin</c>/<c>arrayEnumerate</c> on ClickHouse) and keyset-paging on
    /// <c>(time, data point id, ordinal)</c>. This implementation instead keysets one level up, on
    /// <c>(data point time_unix_nano, data point id)</c>, and returns every exemplar of each data
    /// point that lands on the page. A page therefore never splits one data point's exemplars
    /// across two pages (still no row ever skipped or duplicated across pages), but its exemplar
    /// count can run a little over or under the requested <see cref="MetricExemplarQuery.Size"/>
    /// when a data point carries an unusually large exemplar batch — and, for the same reason,
    /// <c>nav=last</c> is not trimmed to the exact <c>total mod size</c> remainder the way logs/
    /// traces pages are (decision 2): it simply fetches the oldest <c>size</c> data points. Chosen
    /// over the full per-exemplar SQL unnest for a much smaller, lower-risk, provider-uniform
    /// implementation (one query shape shared by every provider through existing hooks, instead of
    /// two bespoke per-dialect array-unnest queries) while still replacing the old hard 500-row cap
    /// with genuine, unbounded keyset paging. Recorded here per the task's "note material
    /// deviations" instruction; a follow-up can implement the literal per-exemplar unnest if the
    /// coarser granularity proves visible in practice.
    /// </summary>
    public virtual async Task<MetricExemplarPage?> GetMetricExemplarsAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var sql = MetricSelect + " AND m.name = @name";
        if (query.MetricId.HasValue) sql += " AND m.id = @metricId";
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            sql, new { tenantId = TenantId, name = query.MetricName, metricId = query.MetricId }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0) return null;

        var type = Enum.Parse<MetricType>(metrics[0].Type);
        var result = new MetricExemplarPage { Name = query.MetricName, Type = type };
        if (type == MetricType.SUMMARY) return result; // no exemplars_json column

        var table = TableFor(type);
        var isCountTable = type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM;
        var metricIds = metrics.Select(m => m.Id).ToList();
        var serviceNameByMetricId = ToDictionaryFirst(metrics, m => m.Id, m => Svc(m.ServiceName) ?? "unknown");

        var (labelClause, lp) = LabelFilterClause(query.LabelFilters);
        var (timeClause, tp) = TimeRange(query.Start, query.End);
        var baseWhere = $"dp.metric_id IN ({IdInList(metricIds)}) AND dp.exemplars_json IS NOT NULL{timeClause}{labelClause}";
        var baseParams = Merge(tp, lp);

        var filterHashText = $"{query.MetricName}|{query.MetricId}|{query.Start:O}|{query.End:O}|" +
            string.Join(",", (query.LabelFilters ?? new Dictionary<string, string>()).OrderBy(k => k.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        var size = Math.Clamp(query.Size, 1, 1000);

        DecodedCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = KeysetCursor.Decode(query.Cursor);
            if (cursor == null || !KeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        var (total, totalTimedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await CountExemplarsAsync(conn, table, baseWhere, baseParams, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);

        List<ExemplarPointRow> rows;
        bool forward;
        var requestedSize = size;

        switch (nav)
        {
            case "next":
                forward = true;
                rows = await FetchExemplarPointsAsync(conn, table, isCountTable, baseWhere, baseParams, size, descending: true,
                    KeysetCursor.Predicate("dp.time_unix_nano", "dp.id", "cursorK", "cursorId", descending: true),
                    new { cursorK = cursor!.K, cursorId = cursor.Id }, cancellationToken);
                break;
            case "prev":
                forward = false;
                rows = await FetchExemplarPointsAsync(conn, table, isCountTable, baseWhere, baseParams, size, descending: false,
                    KeysetCursor.Predicate("dp.time_unix_nano", "dp.id", "cursorK", "cursorId", descending: false),
                    new { cursorK = cursor!.K, cursorId = cursor.Id }, cancellationToken);
                break;
            case "last":
                forward = false;
                rows = await FetchExemplarPointsAsync(conn, table, isCountTable, baseWhere, baseParams, size, descending: false, null, null, cancellationToken);
                break;
            default: // "first"
                forward = true;
                rows = await FetchExemplarPointsAsync(conn, table, isCountTable, baseWhere, baseParams, size, descending: true, null, null, cancellationToken);
                break;
        }

        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<ExemplarPointRow> displayRows;
        string? nextCursor, prevCursor;
        if (forward)
        {
            displayRows = rows;
            nextCursor = hasExtra ? Encode(displayRows[^1], filterHash) : null;
            prevCursor = nav == "first" ? null : (displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null);
        }
        else
        {
            displayRows = new List<ExemplarPointRow>(rows);
            displayRows.Reverse();
            if (nav == "last")
            {
                nextCursor = null;
                prevCursor = (!hasExtra || displayRows.Count == 0) ? null : Encode(displayRows[0], filterHash);
            }
            else
            {
                nextCursor = displayRows.Count > 0 ? Encode(displayRows[^1], filterHash) : null;
                prevCursor = hasExtra && displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null;
            }
        }

        var flattened = new List<MetricExemplar>();
        foreach (var row in displayRows)
        {
            var serviceName = serviceNameByMetricId.GetValueOrDefault(row.MetricId, "unknown");
            var labels = ToLabelDictionary(DeserializeAttributes(row.AttributesJson));
            var seriesName = BuildSeriesDisplayName(serviceName, labels);
            var exemplars = DeserializeExemplars(row.ExemplarsJson) ?? new List<ExemplarModel>();
            foreach (var ex in exemplars.OrderByDescending(e => e.TimeUnixNano))
            {
                flattened.Add(new MetricExemplar
                {
                    Exemplar = ex,
                    SeriesName = seriesName,
                    ServiceName = serviceName,
                    Labels = labels,
                    PointTimestamp = TimeConversion.UnixNanoToDateTime(row.TimeUnixNano),
                    PointCount = isCountTable ? row.Count : null,
                    PointDoubleValue = isCountTable ? null : row.ValueDouble,
                    PointIntValue = isCountTable ? null : row.ValueInt
                });
            }
        }

        result.Exemplars = flattened;
        result.NextCursor = nextCursor;
        result.PrevCursor = prevCursor;
        result.Total = total;
        result.TotalIsLowerBound = totalTimedOut;
        return result;
    }

    private static string Encode(ExemplarPointRow row, string filterHash) => KeysetCursor.Encode(row.TimeUnixNano, row.Id, filterHash);

    /// <summary>
    /// <paramref name="isCountTable"/> selects the right "value" columns for the table actually
    /// being queried: histogram/exp-histogram data points carry <c>count</c> (their "value" for
    /// exemplar display is the observation count) but no <c>value_double</c>/<c>value_int</c>;
    /// gauge/sum data points are the reverse. Selecting a column absent from the table is a real bug
    /// found via the Phase 4 integration tests, and on PostgreSQL specifically it doesn't fail with
    /// the expected "column does not exist": since <c>count</c> IS also a valid aggregate function
    /// name, Postgres re-parses the bare <c>dp.count</c> as the documented func-call sugar for
    /// composite-type field access, <c>count(dp)</c> — silently turning the whole query into an
    /// aggregate one and failing instead with "column dp.metric_id must appear in the GROUP BY
    /// clause", which is what surfaced this.
    /// </summary>
    private async Task<List<ExemplarPointRow>> FetchExemplarPointsAsync(DbConnection conn, string table, bool isCountTable, string baseWhere, object baseParams,
        int size, bool descending, string? cursorPredicate, object? cursorParams, CancellationToken ct)
    {
        var where = baseWhere + (cursorPredicate != null ? $" AND {cursorPredicate}" : "");
        var order = descending ? "DESC" : "ASC";
        var parameters = Merge(Merge(baseParams, new { limit = size + 1, offset = 0 }), cursorParams ?? new { });
        var countCol = isCountTable ? "dp.count AS Count" : "NULL AS Count";
        var valueCols = isCountTable ? "NULL AS ValueDouble, NULL AS ValueInt" : "dp.value_double AS ValueDouble, dp.value_int AS ValueInt";
        var sql = $"""
            SELECT dp.metric_id AS MetricId, dp.id AS Id, dp.time_unix_nano AS TimeUnixNano,
                   dp.attributes_json AS AttributesJson, {valueCols},
                   {countCol}, dp.exemplars_json AS ExemplarsJson
            FROM {table} dp
            WHERE {where}
            ORDER BY dp.time_unix_nano {order}, dp.id {order}
            {PagingClause}
            """;
        var rows = await conn.QueryAsync<ExemplarPointRow>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Exact exemplar count under the shared summary timeout (decision 26/31); an unbuffered scan so memory stays bounded regardless of how many rows match.</summary>
    private async Task<long> CountExemplarsAsync(DbConnection conn, string table, string baseWhere, object baseParams, int timeoutSeconds, CancellationToken ct)
    {
        using var reader = await conn.ExecuteReaderAsync(new CommandDefinition(
            $"SELECT dp.exemplars_json AS ExemplarsJson FROM {table} dp WHERE {baseWhere}",
            baseParams, commandTimeout: timeoutSeconds, cancellationToken: ct));

        long total = 0;
        foreach (var json in reader.Parse<string?>())
        {
            if (string.IsNullOrEmpty(json)) continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                total += doc.RootElement.GetArrayLength();
            }
            catch (JsonException) { /* malformed row; skip rather than fail the whole count */ }
        }
        return total;
    }

    private sealed class ExemplarPointRow
    {
        public long MetricId { get; set; }
        public long Id { get; set; }
        public long TimeUnixNano { get; set; }
        public string? AttributesJson { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public long? Count { get; set; }
        public string? ExemplarsJson { get; set; }
    }

    /// <summary>
    /// Standard-tier behavior (decision 26): the newest 500 exemplars (label-filtered in SQL — the
    /// Phase 1 fix), no cursor, <see cref="MetricExemplarPage.Capped"/> flagged when more exist.
    /// Called from <c>SqlServerMetricReadRepository</c>/<c>MySqlMetricReadRepository</c>'s override
    /// of <see cref="GetMetricExemplarsAsync"/>.
    /// </summary>
    protected async Task<MetricExemplarPage?> GetMetricExemplarsCappedAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var sql = MetricSelect + " AND m.name = @name";
        if (query.MetricId.HasValue) sql += " AND m.id = @metricId";
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            sql, new { tenantId = TenantId, name = query.MetricName, metricId = query.MetricId }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0) return null;

        var type = Enum.Parse<MetricType>(metrics[0].Type);
        var result = new MetricExemplarPage { Name = query.MetricName, Type = type };
        if (type == MetricType.SUMMARY) return result; // no exemplars_json column

        const int cap = 500;
        var flattened = new List<MetricExemplar>();
        var anyTableHitCap = false;

        foreach (var group in metrics.GroupBy(m => Enum.Parse<MetricType>(m.Type)))
        {
            if (group.Key == MetricType.SUMMARY) continue;
            var serviceNameByMetricId = ToDictionaryFirst(group, m => m.Id, m => Svc(m.ServiceName) ?? "unknown");
            var ids = group.Select(m => m.Id).ToList();

            var (rows, rowsReturned) = await ScanExemplarRowsAsync(conn, group.Key, ids, query.Start, query.End, cap, query.LabelFilters, cancellationToken);
            if (rowsReturned == cap) anyTableHitCap = true;

            foreach (var row in rows)
            {
                var serviceName = serviceNameByMetricId[row.MetricId];
                var labels = ToLabelDictionary(row.Attributes);
                var seriesName = BuildSeriesDisplayName(serviceName, labels);
                foreach (var exemplar in row.Exemplars)
                {
                    flattened.Add(new MetricExemplar
                    {
                        Exemplar = exemplar,
                        SeriesName = seriesName,
                        ServiceName = serviceName,
                        Labels = labels,
                        PointTimestamp = row.Timestamp,
                        PointCount = row.Count,
                        PointDoubleValue = row.ValueDouble,
                        PointIntValue = row.ValueInt
                    });
                }
            }
        }

        flattened.Sort((a, b) => b.Exemplar.TimeUnixNano.CompareTo(a.Exemplar.TimeUnixNano));
        result.Capped = anyTableHitCap || flattened.Count > cap;
        result.Exemplars = flattened.Take(cap).ToList();
        return result;
    }

    /// <summary>Dispatches to the per-type exemplar scan; SUMMARY is filtered out by the caller.</summary>
    private Task<(List<ExemplarScanRow> Rows, int RowsReturned)> ScanExemplarRowsAsync(DbConnection conn,
        MetricType type, IList<long> metricIds, DateTime? startTime, DateTime? endTime, int limit,
        Dictionary<string, string>? labelFilters, CancellationToken ct) => type switch
    {
        MetricType.GAUGE => ScanScalarExemplarsAsync(conn, "gauge_data_points", metricIds, startTime, endTime, limit, labelFilters, ct),
        MetricType.SUM => ScanScalarExemplarsAsync(conn, "sum_data_points", metricIds, startTime, endTime, limit, labelFilters, ct),
        MetricType.HISTOGRAM => ScanCountExemplarsAsync(conn, "histogram_data_points", metricIds, startTime, endTime, limit, labelFilters, ct),
        MetricType.EXPONENTIAL_HISTOGRAM => ScanCountExemplarsAsync(conn, "exponential_histogram_data_points", metricIds, startTime, endTime, limit, labelFilters, ct),
        _ => Task.FromResult((new List<ExemplarScanRow>(), 0))
    };

    /// <summary>Exemplar scan for gauge/sum: the point's own value is a double or int.</summary>
    private async Task<(List<ExemplarScanRow>, int)> ScanScalarExemplarsAsync(DbConnection conn, string table,
        IList<long> metricIds, DateTime? startTime, DateTime? endTime, int limit,
        Dictionary<string, string>? labelFilters, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var rows = (await conn.QueryAsync<ExemplarScalarRow>(new CommandDefinition($"""
            SELECT dp.metric_id AS MetricId, dp.time_unix_nano AS TimeUnixNano,
                   dp.value_double AS ValueDouble, dp.value_int AS ValueInt,
                   dp.attributes_json AS AttributesJson, dp.exemplars_json AS ExemplarsJson
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metricIds)})
              AND dp.exemplars_json IS NOT NULL
              {timeClause}{labelClause}
            ORDER BY dp.time_unix_nano DESC
            {PagingClause}
            """, Merge(Merge(new { limit, offset = 0 }, tp), lp), cancellationToken: ct))).ToList();

        var scanRows = rows.Select(r => new ExemplarScanRow
        {
            MetricId = r.MetricId,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            ValueDouble = r.ValueDouble,
            ValueInt = r.ValueInt,
            Count = null,
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = DeserializeExemplars(r.ExemplarsJson) ?? new List<ExemplarModel>()
        }).ToList();

        return (scanRows, rows.Count);
    }

    /// <summary>Exemplar scan for histogram/exp-histogram: the point's own "value" is its observation count.</summary>
    private async Task<(List<ExemplarScanRow>, int)> ScanCountExemplarsAsync(DbConnection conn, string table,
        IList<long> metricIds, DateTime? startTime, DateTime? endTime, int limit,
        Dictionary<string, string>? labelFilters, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var (labelClause, lp) = LabelFilterClause(labelFilters);
        var rows = (await conn.QueryAsync<ExemplarCountRow>(new CommandDefinition($"""
            SELECT dp.metric_id AS MetricId, dp.time_unix_nano AS TimeUnixNano, dp.count AS Cnt,
                   dp.attributes_json AS AttributesJson, dp.exemplars_json AS ExemplarsJson
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metricIds)})
              AND dp.exemplars_json IS NOT NULL
              {timeClause}{labelClause}
            ORDER BY dp.time_unix_nano DESC
            {PagingClause}
            """, Merge(Merge(new { limit, offset = 0 }, tp), lp), cancellationToken: ct))).ToList();

        var scanRows = rows.Select(r => new ExemplarScanRow
        {
            MetricId = r.MetricId,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            ValueDouble = null,
            ValueInt = null,
            Count = r.Cnt,
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = DeserializeExemplars(r.ExemplarsJson) ?? new List<ExemplarModel>()
        }).ToList();

        return (scanRows, rows.Count);
    }

    // =========================================================================
    // AGGREGATION / ANALYSIS READS (unchanged by Phase 4)
    // =========================================================================

    public async Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var result = new Dictionary<string, double>();

        foreach (var table in new[] { "gauge_data_points", "sum_data_points" })
        {
            var rows = await conn.QueryAsync<LatestValueRow>(new CommandDefinition($"""
                SELECT m.name AS MetricName, dp.time_unix_nano AS TimeUnixNano,
                       COALESCE(dp.value_double, dp.value_int, 0) AS Value
                FROM {table} dp
                JOIN metrics m   ON dp.metric_id = m.id
                WHERE m.tenant_id = @tenantId AND m.service_name = @service
                """, new { tenantId = TenantId, service = serviceName }, cancellationToken: cancellationToken));

            var values = rows
                .GroupBy(x => x.MetricName)
                .Select(g => new { MetricName = g.Key, LatestValue = g.OrderByDescending(x => x.TimeUnixNano).First().Value });

            foreach (var v in values)
                result[v.MetricName] = v.LatestValue;
        }

        return result;
    }

    public async Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await QueryMetricRowsAsync(conn, serviceName, cancellationToken);

        return rows
            .GroupBy(m => Enum.Parse<MetricType>(m.Type))
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
    }

    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await QueryMetricRowsAsync(conn, serviceName, cancellationToken);

        return rows.Select(m => m.Name).Distinct().OrderBy(name => name).ToList();
    }

    /// <summary>The tenant's metrics catalog rows, narrowed to <paramref name="serviceName"/> in SQL when given.</summary>
    private async Task<List<MetricRow>> QueryMetricRowsAsync(DbConnection conn, string? serviceName, CancellationToken cancellationToken)
    {
        var sql = string.IsNullOrEmpty(serviceName) ? MetricSelect : MetricSelect + " AND m.service_name = @service";
        return (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            sql, new { tenantId = TenantId, service = serviceName }, cancellationToken: cancellationToken))).ToList();
    }

    /// <summary>Row cap for the label picker's distinct-attribute-set scan (decision 25).</summary>
    private const int MaxLabelSampleRows = 1_000;

    public async Task<MetricLabelsResult> GetMetricLabelsAsync(string metricName, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        // Time-bounded to the current window, defaulting to the last 24 hours when the caller
        // passes neither bound (decision 25) — unlike every other read here, this had NO time
        // bound at all before Phase 1, scanning every attributes_json ever written for the metric.
        var effectiveStart = startTime ?? DateTime.UtcNow.AddHours(-24);
        var effectiveEnd = endTime ?? DateTime.UtcNow;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // A metric name is not unique: the same name is emitted by multiple services/resources,
        // so this returns many rows. Aggregate labels across all of them (they share a type).
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            MetricSelect + " AND m.name = @name", new { tenantId = TenantId, name = metricName }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0)
            return new MetricLabelsResult();

        var table = TableFor(Enum.Parse<MetricType>(metrics[0].Type));
        var (timeClause, tp) = TimeRange(effectiveStart, effectiveEnd);
        // DISTINCT reads distinct label SETS, not the newest N points: a busy series with one
        // dominant label combination can't crowd out a rare one within the row cap this way,
        // which sampling the newest N points would risk (decision 25).
        var jsonValues = (await conn.QueryAsync<string?>(new CommandDefinition(
            $"""
            SELECT DISTINCT dp.attributes_json
            FROM {table} dp
            WHERE dp.metric_id IN ({IdInList(metrics.Select(m => m.Id))}) AND dp.attributes_json IS NOT NULL{timeClause}
            ORDER BY dp.attributes_json
            {PagingClause}
            """, Merge(new { limit = MaxLabelSampleRows, offset = 0 }, tp), cancellationToken: cancellationToken))).ToList();

        var labelDictionary = new Dictionary<string, HashSet<string>>();
        foreach (var attributes in ParseAttributesJson(jsonValues))
        {
            foreach (var kvp in attributes)
            {
                var value = ConvertAttributeValueToString(kvp.Value);
                if (!labelDictionary.TryGetValue(kvp.Key, out var set))
                {
                    set = new HashSet<string>();
                    labelDictionary[kvp.Key] = set;
                }
                set.Add(value);
            }
        }

        return new MetricLabelsResult
        {
            Labels = labelDictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OrderBy(v => v).ToList()),
            Partial = jsonValues.Count == MaxLabelSampleRows
        };
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static (string clause, object extraParams) TimeRange(DateTime? startTime, DateTime? endTime)
    {
        var clause = "";
        if (startTime.HasValue) clause += " AND dp.time_unix_nano >= @start";
        if (endTime.HasValue) clause += " AND dp.time_unix_nano <= @end";
        return (clause, new
        {
            start = startTime.HasValue ? TimeConversion.DateTimeToUnixNano(startTime.Value) : (long?)null,
            end = endTime.HasValue ? TimeConversion.DateTimeToUnixNano(endTime.Value) : (long?)null
        });
    }

    /// <summary>
    /// Compiles <paramref name="labelFilters"/> into <c>AttributePredicate</c> clauses against
    /// <c>dp.attributes_json</c>, ANDed onto the query's WHERE — always non-negated, since a plain
    /// <see cref="Dictionary{TKey,TValue}"/> has no way to express "key is not value" (unlike the
    /// AST-based search filters phases 2/3 add). Called BEFORE <c>GROUP BY</c>/<c>ORDER BY … LIMIT</c>
    /// in every loader/exemplar scan (list-pages-server-side plan, Phase 1, decision 24).
    /// </summary>
    private (string clause, DynamicParameters parameters) LabelFilterClause(Dictionary<string, string>? labelFilters)
    {
        var parameters = new DynamicParameters();
        if (labelFilters == null || labelFilters.Count == 0)
            return ("", parameters);

        var clauses = new List<string>();
        var i = 0;
        foreach (var filter in labelFilters)
        {
            var keyParam = $"lblKey{i}";
            var valueParam = $"lblVal{i}";
            parameters.Add(keyParam, AttributeKeyParamValue(filter.Key));
            parameters.Add(valueParam, filter.Value);
            clauses.Add(AttributePredicate("dp.attributes_json", $"@{keyParam}", $"@{valueParam}", negated: false));
            i++;
        }
        return (" AND " + string.Join(" AND ", clauses), parameters);
    }

    /// <summary>
    /// Builds a comma-separated id list for an IN(...) clause. Used instead of a Dapper list
    /// parameter because Dapper's list expansion is not applied under Npgsql (the list is sent
    /// as a single array param, producing "IN $1" -> 42601 syntax error). Callers guarantee a
    /// non-empty list and the ids are DB-generated bigints, so inlining is injection-safe.
    /// </summary>
    private static string IdInList(IEnumerable<long> ids) => string.Join(",", ids);

    private static string TableFor(MetricType type) => type switch
    {
        MetricType.GAUGE => "gauge_data_points",
        MetricType.SUM => "sum_data_points",
        MetricType.HISTOGRAM => "histogram_data_points",
        MetricType.EXPONENTIAL_HISTOGRAM => "exponential_histogram_data_points",
        MetricType.SUMMARY => "summary_data_points",
        _ => "gauge_data_points"
    };

    /// <summary>
    /// A data point's exemplars, stored as a JSON array on the row itself (schema 2.9.0) rather
    /// than joined from a separate table.
    /// </summary>
    private static List<ExemplarModel>? DeserializeExemplars(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<ExemplarModel>>(json);

    private static long[]? DeserializeLongArray(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<long[]>(json);

    private static double[]? DeserializeDoubleArray(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<double[]>(json);

    private static T[]? DeserializeArray<T>(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T[]>(json);

    /// <summary>Flattens a data point's raw attributes into a string-keyed label set for series identity.</summary>
    private static Dictionary<string, string> ToLabelDictionary(Dictionary<string, object>? attributes)
    {
        if (attributes == null || attributes.Count == 0)
            return new Dictionary<string, string>();
        return attributes.ToDictionary(kvp => kvp.Key, kvp => ConvertAttributeValueToString(kvp.Value));
    }

    /// <summary>
    /// Stable identity key for a series: service name plus the label set with keys sorted, so
    /// identical attribute sets across scrapes collapse into one series regardless of ordering.
    /// </summary>
    private static string SeriesKey(string serviceName, Dictionary<string, string> labels)
    {
        var labelPart = string.Join(",", labels.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{serviceName}{labelPart}";
    }

    private static string BuildSeriesDisplayName(string serviceName, Dictionary<string, string> labels)
    {
        if (labels.Count == 0) return serviceName;
        var labelPart = string.Join(", ", labels.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}={kvp.Value}"));
        return $"{serviceName} | {labelPart}";
    }

    private static IEnumerable<Dictionary<string, object>> ParseAttributesJson(IEnumerable<string?> attributeJsonValues)
    {
        foreach (var attributesJson in attributeJsonValues)
        {
            if (string.IsNullOrWhiteSpace(attributesJson)) continue;
            Dictionary<string, object>? parsed;
            try { parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(attributesJson); }
            catch (JsonException) { continue; }
            if (parsed != null) yield return parsed;
        }
    }

    // ClickHouse stores a missing service name as '' (non-Nullable column); treat it as absent everywhere.
    private static string? Svc(string? serviceName) => string.IsNullOrEmpty(serviceName) ? null : serviceName;

    private static MetricInfo ToMetricInfo(MetricRow m, string? serviceName, MetricStat? stat = null) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Description = m.Description,
        Unit = m.Unit,
        Type = Enum.Parse<MetricType>(m.Type),
        ServiceName = serviceName,
        // Real data-point window/count when available; otherwise fall back to registration time / 0.
        FirstSeen = stat?.First ?? m.CreatedAt,
        LastSeen = stat?.Last ?? m.CreatedAt,
        DataPointCount = stat?.Count ?? 0
    };

    /// <summary>
    /// Computes the all-time first/last data-point timestamp and total point count for each metric,
    /// keyed by metric id. Each metric's points live in exactly one table (per <see cref="TableFor"/>),
    /// so rows are grouped by table and one aggregate query is issued per table involved. Not time
    /// bounded: these represent the metric's full lifetime, independent of any chart time range.
    /// </summary>
    private async Task<Dictionary<long, MetricStat>> GetMetricStatsAsync(DbConnection conn, IReadOnlyList<MetricRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<long, MetricStat>();
        if (rows.Count == 0) return result;

        foreach (var group in rows.GroupBy(r => TableFor(Enum.Parse<MetricType>(r.Type))))
        {
            var table = group.Key;
            var ids = group.Select(r => r.Id).ToList();
            var statRows = await conn.QueryAsync<StatRow>(new CommandDefinition($"""
                SELECT metric_id AS MetricId, MIN(time_unix_nano) AS MinNano,
                       MAX(time_unix_nano) AS MaxNano, COUNT(*) AS Cnt
                FROM {table}
                WHERE metric_id IN ({IdInList(ids)})
                GROUP BY metric_id
                """, cancellationToken: ct));

            foreach (var s in statRows)
            {
                result[s.MetricId] = new MetricStat(
                    TimeConversion.UnixNanoToDateTime(s.MinNano),
                    TimeConversion.UnixNanoToDateTime(s.MaxNano),
                    (int)Math.Min(s.Cnt, int.MaxValue));
            }
        }

        return result;
    }

    private static object Merge(object a, object b)
    {
        var dp = new DynamicParameters();
        dp.AddDynamicParams(a);
        dp.AddDynamicParams(b);
        return dp;
    }

    // =========================================================================
    // ROW / AGGREGATE DTOs
    // =========================================================================

    /// <summary>All-time data-point window and count for a single metric.</summary>
    private sealed record MetricStat(DateTime First, DateTime Last, int Count);

    private sealed class StatRow
    {
        public long MetricId { get; set; }
        public long MinNano { get; set; }
        public long MaxNano { get; set; }
        public long Cnt { get; set; }
    }

    private sealed class MetricRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public string? Unit { get; set; }
        public string Type { get; set; } = null!;
        public DateTime CreatedAt { get; set; }
        public string? ServiceName { get; set; }
    }

    private sealed class LatestValueRow
    {
        public string MetricName { get; set; } = null!;
        public long TimeUnixNano { get; set; }
        public double Value { get; set; }
    }

    /// <summary>Wide row DTO shared by every Phase 4 bucketed-query projection; a given SQL text only selects the subset of columns it needs, and Dapper leaves the rest at their default.</summary>
    private sealed class MetricPointRow
    {
        public long MetricId { get; set; }
        public string AttributesJson { get; set; } = "";
        public int Bucket { get; set; }
        public long TimeUnixNano { get; set; }
        public long? StartTimeUnixNano { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public double? AvgValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? SumOfValue { get; set; }
        /// <summary>Delta loaders: Σ(time − start) over the bucket's points that carry a usable start time, in nanos.</summary>
        public double? CoveredNanos { get; set; }
        public long? Count { get; set; }
        public double? SumValue { get; set; }
        public string? BucketCounts { get; set; }
        public string? ExplicitBounds { get; set; }
        public int? Scale { get; set; }
        public long? ZeroCount { get; set; }
        public int? PositiveOffset { get; set; }
        public string? PositiveBucketCounts { get; set; }
        /// <summary>Maps the <c>quantile_values</c> column (Dapper's underscore-insensitive default name matching).</summary>
        public string? QuantileValues { get; set; }
    }

    /// <summary>Per-stream (metric_id + attributes_json) working state during aggregation.</summary>
    private sealed class StreamAgg
    {
        public long MetricId { get; set; }
        public string AttributesJson { get; set; } = "";
        public Dictionary<string, object>? Attributes { get; set; }
        public string ServiceName { get; set; } = "unknown";
        /// <summary>Histogram only: this stream's explicit bucket bounds (assumed stable across the window; see decision 42).</summary>
        public double[]? Bounds { get; set; }
        public Dictionary<int, BucketAgg> Buckets { get; } = new();
    }

    /// <summary>One stream's aggregate for one time bucket. Which fields are set depends on the metric type — see the loader that populated it.</summary>
    private sealed class BucketAgg
    {
        public double? Avg { get; set; }
        public double? Value { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public long? Count { get; set; }
        public double? Sum { get; set; }
        /// <summary>See <see cref="MetricBucketPoint.Rate"/>.</summary>
        public double? Rate { get; set; }
        /// <summary>See <see cref="MetricBucketPoint.MinMaxApproximate"/>.</summary>
        public bool MinMaxApproximate { get; set; }
        ///<summary>Histogram (explicit bounds) merged/deltaed bucket counts, dense, aligned to the stream's <see cref="StreamAgg.Bounds"/>.</summary>
        public long[]? Counts { get; set; }
        /// <summary>Exponential histogram merged/deltaed bucket counts, sparse, keyed by absolute exponent index at the query's global target scale.</summary>
        public Dictionary<long, long>? SparseCounts { get; set; }
        public long? ZeroCountValue { get; set; }
        public List<double>? Quantiles { get; set; }
        public List<double>? QuantileValues { get; set; }
    }

    /// <summary>Type-normalized result of an exemplar-bearing row scan, before flattening its exemplars.</summary>
    private sealed class ExemplarScanRow
    {
        public long MetricId { get; set; }
        public DateTime Timestamp { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public long? Count { get; set; }
        public Dictionary<string, object>? Attributes { get; set; }
        public List<ExemplarModel> Exemplars { get; set; } = new();
    }

    private sealed class ExemplarScalarRow
    {
        public long MetricId { get; set; }
        public long TimeUnixNano { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public string? AttributesJson { get; set; }
        public string? ExemplarsJson { get; set; }
    }

    private sealed class ExemplarCountRow
    {
        public long MetricId { get; set; }
        public long TimeUnixNano { get; set; }
        public long Cnt { get; set; }
        public string? AttributesJson { get; set; }
        public string? ExemplarsJson { get; set; }
    }
}
