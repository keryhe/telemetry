using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

using static Keryhe.Telemetry.Core.Data.Read.MetricSeriesPipeline;

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
            LEFT JOIN metric_last_seen mls ON mls.metric_id = m.id
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
    /// <see cref="SeenInRangeClause"/>)? A correlated <c>EXISTS</c>, index-served on each table's
    /// <c>(metric_id, time_unix_nano)</c>.
    /// </summary>
    protected string ExactSeenInRangePredicate(string metricsAlias)
    {
        var existsClauses = TelemetryIngestionHelpers.TimePrunedMetricTables.Select(t =>
            $"EXISTS (SELECT 1 FROM {t} dp WHERE dp.metric_id = {metricsAlias}.id AND dp.time_unix_nano >= @seenStartNano AND dp.time_unix_nano <= @seenEndNano)");
        return "(" + string.Join(" OR ", existsClauses) + ")";
    }

    public async Task<MetricCatalogPage> GetMetricCatalogPageAsync(MetricCatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var limit = Math.Max(1, query.Limit);
        var groupByName = string.Equals(query.GroupBy, "name", StringComparison.OrdinalIgnoreCase);

        return groupByName
            ? await GetMetricCatalogByNameAsync(conn, query, limit, cancellationToken)
            : await GetMetricCatalogByInstanceAsync(conn, query, limit, cancellationToken);
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

    private async Task<MetricCatalogPage> GetMetricCatalogByInstanceAsync(DbConnection conn, MetricCatalogQuery query, int limit, CancellationToken cancellationToken)
    {
        var (filterClause, filterParams) = BuildCatalogFilterClauses(query);
        var parameters = new DynamicParameters();
        parameters.AddDynamicParams(filterParams);
        var seenClause = SeenInRangeClause(query.Start, query.End, parameters);
        parameters.Add("tenantId", TenantId);

        var baseWhere = $"m.tenant_id = @tenantId AND {seenClause}{filterClause}";

        // limit + 1 rows: the extra one only says that more matched.
        var rows = await FetchCatalogInstancesAsync(conn, baseWhere, parameters, limit + 1, cancellationToken);
        var truncated = rows.Count > limit;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        return new MetricCatalogPage
        {
            Items = rows.Select(m => ToMetricInfo(m, Svc(m.ServiceName))).ToList(),
            Truncated = truncated
        };
    }

    private async Task<List<MetricRow>> FetchCatalogInstancesAsync(
        DbConnection conn, string where, object parameters, int rowCount, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT m.id AS Id, m.name AS Name, m.description AS Description, m.unit AS Unit,
                   m.type AS Type, m.created_at AS CreatedAt, m.service_name AS ServiceName
            FROM metrics m
            LEFT JOIN metric_last_seen mls ON mls.metric_id = m.id
            WHERE {where}
            ORDER BY m.created_at DESC, m.id DESC
            {LiteralPagingClause(rowCount, 0)}
            """;
        var rows = await conn.QueryAsync<MetricRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// <c>groupBy=name</c> view (decision 28): one row per metric name, most recently created first. The GROUP BY/LIMIT query
    /// itself stays portable across all five providers (plain COUNT/MAX); a second, narrow follow-up query fetches the
    /// returned names' own rows to compute the per-name type and distinct service list in C#, instead of a per-provider
    /// ARRAY_AGG/GROUP_CONCAT/groupUniqArray dialect for what is at most <c>limit</c> names.
    /// </summary>
    private async Task<MetricCatalogPage> GetMetricCatalogByNameAsync(DbConnection conn, MetricCatalogQuery query, int limit, CancellationToken cancellationToken)
    {
        var (filterClause, filterParams) = BuildCatalogFilterClauses(query);
        var parameters = new DynamicParameters();
        parameters.AddDynamicParams(filterParams);
        var seenClause = SeenInRangeClause(query.Start, query.End, parameters);
        parameters.Add("tenantId", TenantId);

        var baseWhere = $"m.tenant_id = @tenantId AND {seenClause}{filterClause}";

        // limit + 1 names: the extra one only says that more matched.
        var displayRows = await FetchCatalogNamesAsync(conn, baseWhere, parameters, limit + 1, cancellationToken);
        var truncated = displayRows.Count > limit;
        if (truncated) displayRows.RemoveAt(displayRows.Count - 1);

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
                LEFT JOIN metric_last_seen mls ON mls.metric_id = m.id
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

        return new MetricCatalogPage
        {
            Names = items,
            Truncated = truncated
        };
    }

    private async Task<List<NameGroupRow>> FetchCatalogNamesAsync(
        DbConnection conn, string baseWhere, object parameters, int rowCount, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT m.name AS Name, MAX(m.created_at) AS NewestCreatedAt, COUNT(*) AS InstanceCount
            FROM metrics m
            LEFT JOIN metric_last_seen mls ON mls.metric_id = m.id
            WHERE {baseWhere}
            GROUP BY m.name
            ORDER BY MAX(m.created_at) DESC, m.name DESC
            {LiteralPagingClause(rowCount, 0)}
            """;
        var rows = await conn.QueryAsync<NameGroupRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// <c>LIMIT n OFFSET m</c> with the values inlined as literals rather than bound parameters —
    /// safe because both are C#-computed integers, never user input. Used only by the
    /// two catalog fetches above, in place of the shared <see cref="PagingClause"/> (which
    /// stays parameterized for every other query in this file and the rest of the codebase).
    /// </summary>
    protected virtual string LiteralPagingClause(int limit, int offset) => $"LIMIT {limit} OFFSET {offset}";

    private sealed class NameGroupRow
    {
        public string Name { get; set; } = null!;
        public DateTime NewestCreatedAt { get; set; }
        public int InstanceCount { get; set; }
    }

    // =========================================================================
    // TIME SERIES READS (Phase 4: database-side bucketed aggregation)
    // =========================================================================

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
            // The retry runs on a FRESH connection, not the one the timed-out attempt was using. That attempt was
            // aborted mid-statement (TimedQuery cancels the token, and the command timeout can fire at the same
            // moment), which leaves the connection in a state the driver owns and we cannot inspect: a provider may
            // break the connector outright (Npgsql wraps its read timeout in an NpgsqlException and the connector is
            // then unusable) or hand back a session whose previous result set was not fully drained. Reusing it makes
            // the retry's own result sets suspect, which is worse than a slow query — the per-stream baseline rows it
            // reads are a lookup keyed on something no index enforces, so a stray row is not a crash but a wrong
            // delta. See ToDictionaryNewest for the read side of the same concern.
            await using var retryConn = await OpenConnectionAsync(cancellationToken);
            var (retryResult, retryTimedOut) = await TimedQuery.RunAsync(
                async (timeoutSeconds, ct) => await RunSeriesQueryAsync(retryConn, type, metricIds, serviceNameByMetricId, query, retryPoints, top, timeoutSeconds, ct),
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
    // (BucketIndexExpr, AttributePredicate/LabelFilterClause) and BuildLastPerStreamBucketSql/BuildLastPerStreamSql.
    // -------------------------------------------------------------------

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
        var valueExpr = "COALESCE(dp.value_double, dp.value_int)";
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
                   SUM(COALESCE(dp.value_double, dp.value_int)) AS SumOfValue, SUM({CoveredNanosExpr}) AS CoveredNanos
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
        var baselineByStream = ToDictionaryNewest(
            await conn.QueryAsync<MetricPointRow>(new CommandDefinition(baselineSql,
                Merge(new { start = b.StartNano }, lp), commandTimeout: timeoutSeconds, cancellationToken: ct)),
            r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano);

        return ComputeSummaryDeltas(rows, baselineByStream, b.StartNano);
    }

    // -------------------------------------------------------------------
    // Stream -> display series merge, top-N ranking and "other" folding (decisions 22-23, 42).
    // -------------------------------------------------------------------

    // =========================================================================
    // "LAST PER STREAM(-BUCKET)" SQL
    // =========================================================================

    /// <summary>
    /// One row per <c>(metric_id, attributes_json, bucket)</c>, holding <paramref name="valueColumns"/>
    /// from whichever row has the greatest <c>time_unix_nano</c> in that group — the "last point in
    /// this stream-bucket" SQL cumulative sums/histograms need (decision 22), with
    /// <c>ROW_NUMBER() OVER (PARTITION BY ...)</c> (PostgreSQL, SqlServer, MySQL 8+). <paramref name="idInList"/>/<paramref name="timeClause"/>/<paramref name="labelClause"/>
    /// are pre-built fragments (the caller already bound their parameters); <paramref name="bucketExpr"/>
    /// is <see cref="ClampedBucketExpr"/>'s SQL text.
    /// </summary>
    protected string BuildLastPerStreamBucketSql(string table, string idInList, string timeClause,
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
    protected string BuildLastPerStreamSql(string table, string idInList, string timeClause,
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
    // EXEMPLARS
    // =========================================================================

    /// <summary>
    /// The newest <see cref="MetricExemplarQuery.Limit"/> exemplars, label-filtered in SQL, with
    /// <see cref="MetricExemplarPage.Truncated"/> set when more exist. The same query on every provider.
    /// </summary>
    public async Task<MetricExemplarPage?> GetMetricExemplarsAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default)
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

        var cap = Math.Max(1, query.Limit);
        var flattened = new List<MetricExemplar>();
        var anyTableHitCap = false;

        foreach (var group in metrics.GroupBy(m => Enum.Parse<MetricType>(m.Type)))
        {
            if (group.Key == MetricType.SUMMARY) continue;
            var serviceNameByMetricId = ToDictionaryFirst(group, m => m.Id, m => Svc(m.ServiceName) ?? "unknown");
            var ids = group.Select(m => m.Id).ToList();

            // cap + 1 data points: the extra one only says that more exist.
            var (rows, rowsReturned) = await ScanExemplarRowsAsync(conn, group.Key, ids, query.Start, query.End, cap + 1, query.LabelFilters, cancellationToken);
            if (rowsReturned > cap) anyTableHitCap = true;

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
        result.Truncated = anyTableHitCap || flattened.Count > cap;
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

    // An empty service name is absent everywhere.
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
