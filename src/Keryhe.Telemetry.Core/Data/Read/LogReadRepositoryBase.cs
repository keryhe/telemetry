using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="ILogReadRepository"/>. Joins to
/// <c>resources</c>/<c>instrumentation_scopes</c>, applies the tenant + severity/time
/// filters in SQL, and shapes rows into <see cref="LogRecordModel"/> exactly as the
/// former EF repository did.
/// </summary>
public abstract class LogReadRepositoryBase : DapperReadRepository, ILogReadRepository
{
    /// <summary>
    /// Defaults to <see cref="QueryOptions.SummaryTimeoutSeconds"/>'s own default (5) so every
    /// provider's read repository — none of which currently passes <see cref="IConfiguration"/> to
    /// this base — gets a sane timeout rather than 0, which would cancel every summary/page query
    /// immediately (<see cref="TimedQuery.RunAsync{T}"/> treats a 0-second budget as "expire now").
    /// The two-argument constructor below overrides it from configuration when a provider does
    /// pass one.
    /// </summary>
    private readonly int _summaryTimeoutSeconds = 5;

    /// <summary>Sample size for <see cref="GetLogFacetsAsync"/> (decision 15): the newest N matching rows, not the whole filtered set.</summary>
    private const int FacetSampleSize = 10_000;

    private const long NanosPerMinute = 60_000_000_000L;
    private const long NanosPerHour = 3_600_000_000_000L;

    protected LogReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    protected LogReadRepositoryBase(ITenantContext tenantContext, IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
    }

    private const string BaseSelect = """
        SELECT
            lr.time_unix_nano            AS TimeUnixNano,
            lr.observed_time_unix_nano   AS ObservedTimeUnixNano,
            lr.severity_number           AS SeverityNumber,
            lr.severity_text             AS SeverityText,
            lr.event_name                AS EventName,
            lr.body_type                 AS BodyType,
            lr.body_value                AS BodyValue,
            lr.dropped_attributes_count  AS DroppedAttributesCount,
            lr.flags                     AS Flags,
            lr.trace_id                  AS TraceId,
            lr.span_id                   AS SpanId,
            lr.attributes_json           AS AttributesJson,
            r.schema_url                 AS ResourceSchemaUrl,
            r.attributes_json            AS ResourceAttributesJson,
            sc.name                      AS ScopeName,
            sc.version                   AS ScopeVersion,
            sc.schema_url                AS ScopeSchemaUrl,
            sc.attributes_json           AS ScopeAttributesJson
        FROM log_records lr
        JOIN resources r               ON lr.resource_id = r.id
        JOIN instrumentation_scopes sc ON lr.scope_id = sc.id
        WHERE r.tenant_id = @tenantId
        """;

    public async Task<LogRecordModel?> GetLogRecordByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var row = await conn.QuerySingleOrDefaultAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.id = @id",
            new { tenantId = TenantId, id }, cancellationToken: cancellationToken));
        return row == null ? null : Map(row);
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTraceIdAsync(string traceIdHex, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(traceIdHex))
            throw new ArgumentException("Trace ID cannot be null or empty", nameof(traceIdHex));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.trace_id = @traceId ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, traceId = traceIdHex }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetLogRecordsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        var startNano = TimeConversion.DateTimeToUnixNano(startTime);
        var endNano = TimeConversion.DateTimeToUnixNano(endTime);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<LogRow>(new CommandDefinition(
            BaseSelect + " AND lr.time_unix_nano >= @start AND lr.time_unix_nano <= @end ORDER BY lr.time_unix_nano DESC",
            new { tenantId = TenantId, start = startNano, end = endNano }, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task<IEnumerable<LogRecordModel>> GetSurroundingLogRecordsAsync(
        long anchorTimeUnixNano, string? service, int before, int after, CancellationToken cancellationToken = default)
    {
        before = Math.Clamp(before, 0, 500);
        after = Math.Clamp(after, 0, 500);

        var serviceClause = string.IsNullOrEmpty(service) ? "" : $" AND {ResourceServiceNameExpr()} = @service";

        await using var conn = await OpenConnectionAsync(cancellationToken);

        // Records strictly before the anchor, newest-first, capped at `before`.
        var older = (await conn.QueryAsync<LogRow>(new CommandDefinition(
            $"{BaseSelect}{serviceClause} AND lr.time_unix_nano < @anchor ORDER BY lr.time_unix_nano DESC {PagingClause}",
            new { tenantId = TenantId, service, anchor = anchorTimeUnixNano, limit = before, offset = 0 },
            cancellationToken: cancellationToken))).ToList();

        // The anchor row(s) plus records after it, oldest-first (+1 to include the anchor itself).
        var newer = (await conn.QueryAsync<LogRow>(new CommandDefinition(
            $"{BaseSelect}{serviceClause} AND lr.time_unix_nano >= @anchor ORDER BY lr.time_unix_nano ASC {PagingClause}",
            new { tenantId = TenantId, service, anchor = anchorTimeUnixNano, limit = after + 1, offset = 0 },
            cancellationToken: cancellationToken))).ToList();

        older.Reverse(); // DESC -> ASC so the whole window reads oldest -> newest.
        return older.Concat(newer).Select(Map).ToList();
    }

    // =========================================================================
    // SUMMARY (list-pages-server-side plan, Phase 2)
    // =========================================================================

    public async Task<LogSummaryResult> GetLogSummaryAsync(LogSummaryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await ExecuteWithRetryAsync(() => GetLogSummaryCoreAsync(query, cancellationToken));
    }

    private async Task<LogSummaryResult> GetLogSummaryCoreAsync(LogSummaryQuery query, CancellationToken cancellationToken)
    {
        var parsed = SearchQueryParser.Parse(query.Search);
        var hasRawSearchFilter = parsed.IsTraceIdSearch || parsed.Terms.Count > 0;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);

        // Rollup eligibility (decision 37): time/service filters only. MinSeverity is deliberately
        // excluded even though decision 37 nominally allows it — the pre-aggregated severity-group
        // columns can't safely reconstruct an arbitrary row-level `severity_number >= X` cutoff
        // (the Info group bundles NULL severities with 9-12, and a cutoff that doesn't land exactly
        // on a group boundary would need sub-group counts the rollup tables don't keep), so a
        // MinSeverity filter always falls back to the raw path. Documented as a deliberate,
        // correctness-preserving scope narrowing rather than an oversight.
        var rollupEligible = !hasRawSearchFilter && query.MinSeverity is null;

        if (rollupEligible)
        {
            var rollupResult = await TryGetRollupSummaryAsync(conn, query, startNano, endNano, cancellationToken);
            if (rollupResult != null)
            {
                var newSince = await CountNewSinceAsOfAsync(conn, query.Start, query.End, query.Service, query.MinSeverity, parsed, asOf, cancellationToken);
                return new LogSummaryResult
                {
                    Source = rollupResult.Source,
                    Buckets = rollupResult.Buckets,
                    Total = rollupResult.Total,
                    TotalIsLowerBound = rollupResult.TotalIsLowerBound,
                    NewSinceAsOf = newSince,
                    AsOf = asOf
                };
            }
        }

        return await GetRawSummaryAsync(conn, query, parsed, startNano, endNano, asOf, cancellationToken);
    }

    /// <summary>
    /// Attempts the rollup-table source (decisions 37-38): minute rows for a window under 24h,
    /// hour rows otherwise, plus a raw tail for the not-yet-rolled portion of the window. Returns
    /// null when <c>rollup_state</c> reports no coverage of this window at all, so the caller falls
    /// back to the raw path — this is NOT the same as "no data": a covered-but-empty window still
    /// returns a (zeroed) result here.
    /// </summary>
    private async Task<LogSummaryResult?> TryGetRollupSummaryAsync(
        System.Data.Common.DbConnection conn, LogSummaryQuery query, long startNano, long endNano, CancellationToken cancellationToken)
    {
        var granularity = (endNano - startNano) > 24 * NanosPerHour ? "hour" : "minute";
        var bucketSizeNano = granularity == "minute" ? NanosPerMinute : NanosPerHour;
        var table = granularity == "minute" ? "log_rollup_minute" : "log_rollup_hour";

        var state = await conn.QuerySingleOrDefaultAsync<RollupStateRow>(new CommandDefinition(
            "SELECT coverage_start_unix_nano AS CoverageStartUnixNano, rolled_until_unix_nano AS RolledUntilUnixNano " +
            "FROM rollup_state WHERE signal_name = 'logs' AND granularity = @granularity",
            new { granularity }, cancellationToken: cancellationToken));

        if (state?.CoverageStartUnixNano is not { } coverageStart || startNano < coverageStart)
            return null;

        var rolledUntil = state.RolledUntilUnixNano;
        var rollupEnd = Math.Min(endNano, rolledUntil);
        if (rollupEnd < startNano) rollupEnd = startNano;

        var serviceClause = string.IsNullOrEmpty(query.Service) ? "" : $" AND {ResourceServiceNameExpr()} = @service";
        var rollupSql = $"""
            SELECT lrm.bucket_unix_nano AS BucketUnixNano,
                   SUM(lrm.trace_count) AS Trace,
                   SUM(lrm.debug_count) AS Debug,
                   SUM(lrm.info_count)  AS Info,
                   SUM(lrm.warn_count)  AS Warn,
                   SUM(lrm.error_count) AS Error,
                   SUM(lrm.fatal_count) AS Fatal
            FROM {table} AS lrm{RollupFinalHint}
            JOIN resources r ON lrm.resource_id = r.id
            WHERE r.tenant_id = @tenantId
              AND lrm.bucket_unix_nano >= @start AND lrm.bucket_unix_nano < @rollupEnd
              {serviceClause}
            GROUP BY lrm.bucket_unix_nano
            """;

        var rollupRows = (await conn.QueryAsync<SummaryBucketRow>(new CommandDefinition(rollupSql, new
        {
            tenantId = TenantId,
            start = startNano,
            rollupEnd,
            service = query.Service
        }, cancellationToken: cancellationToken))).ToList();

        var buckets = rollupRows.ToDictionary(r => r.BucketUnixNano);

        // Raw tail: [rollupEnd, endNano), the not-yet-rolled portion of the window (typically the
        // last SettleSeconds). Aligned to the same bucket size so it merges cleanly.
        if (rollupEnd < endNano)
        {
            var tailClauses = new List<string> { "lr.time_unix_nano >= @rollupEnd", "lr.time_unix_nano < @end" };
            if (!string.IsNullOrEmpty(query.Service)) tailClauses.Add($"{ResourceServiceNameExpr()} = @service");
            var tailWhere = string.Join(" AND ", tailClauses);
            var bucketIndexExpr = BucketIndexExpr("lr.time_unix_nano", "@bucketSize");
            var tailSql = $"""
                SELECT ({bucketIndexExpr}) AS BucketIndex, {LogSeverityGroupSql.SumCaseColumns("lr.severity_number")}
                FROM log_records lr
                JOIN resources r ON lr.resource_id = r.id
                WHERE r.tenant_id = @tenantId AND {tailWhere}
                GROUP BY ({bucketIndexExpr})
                """;
            var tailRows = await conn.QueryAsync<TailBucketRow>(new CommandDefinition(tailSql, new
            {
                tenantId = TenantId,
                rollupEnd,
                end = endNano,
                service = query.Service,
                bucketSize = bucketSizeNano
            }, cancellationToken: cancellationToken));

            foreach (var row in tailRows)
            {
                var bucketStart = row.BucketIndex * bucketSizeNano;
                if (buckets.TryGetValue(bucketStart, out var existing))
                {
                    buckets[bucketStart] = new SummaryBucketRow
                    {
                        BucketUnixNano = bucketStart,
                        Trace = existing.Trace + row.Trace,
                        Debug = existing.Debug + row.Debug,
                        Info = existing.Info + row.Info,
                        Warn = existing.Warn + row.Warn,
                        Error = existing.Error + row.Error,
                        Fatal = existing.Fatal + row.Fatal
                    };
                }
                else
                {
                    buckets[bucketStart] = new SummaryBucketRow
                    {
                        BucketUnixNano = bucketStart,
                        Trace = row.Trace,
                        Debug = row.Debug,
                        Info = row.Info,
                        Warn = row.Warn,
                        Error = row.Error,
                        Fatal = row.Fatal
                    };
                }
            }
        }

        var orderedBuckets = buckets.Values
            .OrderBy(b => b.BucketUnixNano)
            .Select(b => new LogSummaryBucket
            {
                Timestamp = TimeConversion.UnixNanoToDateTime(b.BucketUnixNano),
                Trace = b.Trace,
                Debug = b.Debug,
                Info = b.Info,
                Warn = b.Warn,
                Error = b.Error,
                Fatal = b.Fatal
            })
            .ToList();

        var total = orderedBuckets.Sum(b => b.Trace + b.Debug + b.Info + b.Warn + b.Error + b.Fatal);

        return new LogSummaryResult
        {
            Source = "rollup",
            Buckets = orderedBuckets,
            Total = total,
            TotalIsLowerBound = false
        };
    }

    private async Task<LogSummaryResult> GetRawSummaryAsync(
        System.Data.Common.DbConnection conn, LogSummaryQuery query, ParsedSearchQuery parsed,
        long startNano, long endNano, DateTime asOf, CancellationToken cancellationToken)
    {
        var bucketCount = Math.Clamp(query.BucketCount, 1, 500);
        var rangeNano = Math.Max(1, endNano - startNano);

        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);

        var where = string.Join(" AND ", clauses);
        var rawIndexExpr = BucketIndexExpr("(lr.time_unix_nano - @start) * @bucketCount", "@rangeNano");
        var clampedIndexExpr = $"CASE WHEN {rawIndexExpr} < 0 THEN 0 WHEN {rawIndexExpr} > @bucketCountMinus1 THEN @bucketCountMinus1 ELSE {rawIndexExpr} END";
        var sql = $"""
            SELECT {clampedIndexExpr} AS BucketIndex, {LogSeverityGroupSql.SumCaseColumns("lr.severity_number")}
            FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            GROUP BY {clampedIndexExpr}
            """;

        parameters.Add("bucketCount", bucketCount);
        parameters.Add("bucketCountMinus1", bucketCount - 1);
        parameters.Add("rangeNano", rangeNano);

        var (rows, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => (await conn.QueryAsync<LogBucketRow>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct))).ToList(),
            _summaryTimeoutSeconds, cancellationToken);

        if (timedOut)
        {
            var cappedTotal = await GetCappedTotalAsync(conn, query.Service, query.MinSeverity, parsed, startNano, endNano, cancellationToken);
            var newSinceCapped = await CountNewSinceAsOfAsync(conn, query.Start, query.End, query.Service, query.MinSeverity, parsed, asOf, cancellationToken);
            return new LogSummaryResult
            {
                Source = "raw",
                Buckets = [],
                Total = cappedTotal,
                TotalIsLowerBound = true,
                NewSinceAsOf = newSinceCapped,
                AsOf = asOf
            };
        }

        var byIndex = (rows ?? []).ToDictionary(r => r.BucketIndex);
        var startTicks = query.Start.Ticks;
        var rangeTicks = Math.Max(1, query.End.Ticks - startTicks);

        var buckets = new List<LogSummaryBucket>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            byIndex.TryGetValue(i, out var r);
            buckets.Add(new LogSummaryBucket
            {
                Timestamp = new DateTime(startTicks + (long)i * rangeTicks / bucketCount, query.Start.Kind),
                Trace = r?.Trace ?? 0,
                Debug = r?.Debug ?? 0,
                Info = r?.Info ?? 0,
                Warn = r?.Warn ?? 0,
                Error = r?.Error ?? 0,
                Fatal = r?.Fatal ?? 0
            });
        }

        var total = buckets.Sum(b => b.Trace + b.Debug + b.Info + b.Warn + b.Error + b.Fatal);
        var newSince = await CountNewSinceAsOfAsync(conn, query.Start, query.End, query.Service, query.MinSeverity, parsed, asOf, cancellationToken);

        return new LogSummaryResult
        {
            Source = "raw",
            Buckets = buckets,
            Total = total,
            TotalIsLowerBound = false,
            NewSinceAsOf = newSince,
            AsOf = asOf
        };
    }

    /// <summary>Capped-count fallback (ports the retired <c>QueryLogRecordsAsync</c>'s capped-subquery-count shape) for when the raw summary times out.</summary>
    private async Task<long> GetCappedTotalAsync(
        System.Data.Common.DbConnection conn, string? service, int? minSeverity, ParsedSearchQuery parsed,
        long startNano, long endNano, CancellationToken cancellationToken)
    {
        const int cap = 10_000;
        var (clauses, parameters) = BuildFilterClauses(service, minSeverity, parsed, startNano, endNano);
        var where = string.Join(" AND ", clauses);
        var sql = $"""
            SELECT COUNT(*) FROM (
                SELECT 1 AS x
                FROM log_records lr
                JOIN resources r ON lr.resource_id = r.id
                WHERE r.tenant_id = @tenantId AND {where}
                ORDER BY lr.time_unix_nano DESC
                {PagingClause}
            ) capped
            """;
        parameters.Add("limit", cap + 1);
        parameters.Add("offset", 0);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<long> CountNewSinceAsOfAsync(
        System.Data.Common.DbConnection conn, DateTime start, DateTime end, string? service, int? minSeverity,
        ParsedSearchQuery parsed, DateTime asOf, CancellationToken cancellationToken)
    {
        var startNano = TimeConversion.DateTimeToUnixNano(start);
        var endNano = TimeConversion.DateTimeToUnixNano(end);
        var (clauses, parameters) = BuildFilterClauses(service, minSeverity, parsed, startNano, endNano);
        clauses.Add("lr.created_at > @asOf");
        parameters.Add("asOf", asOf);
        var where = string.Join(" AND ", clauses);
        var sql = $"""
            SELECT COUNT(*) FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            """;
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    // =========================================================================
    // PAGE (list-pages-server-side plan, Phase 2, keyset paging)
    // =========================================================================

    public async Task<LogPageResult> GetLogPageAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        return await ExecuteWithRetryAsync(() => GetLogPageCoreAsync(query, cancellationToken));
    }

    private async Task<LogPageResult> GetLogPageCoreAsync(LogQuery query, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(query.Size, 1, 500);
        var parsed = SearchQueryParser.Parse(query.Search);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var asOf = await ResolveAsOfAsync(conn, query.AsOf, cancellationToken);

        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);
        clauses.Add("lr.created_at <= @asOf");
        parameters.Add("asOf", asOf);

        var filterHashText = $"{query.Start:O}|{query.End:O}|{asOf:O}|{query.Service}|{query.Search}|{query.MinSeverity}";
        var filterHash = KeysetCursor.ComputeFilterHash(filterHashText);

        var nav = (query.Nav ?? "first").ToLowerInvariant();
        DecodedCursor? cursor = null;
        if (nav is "next" or "prev")
        {
            cursor = KeysetCursor.Decode(query.Cursor);
            if (cursor == null || !KeysetCursor.MatchesFilterHash(cursor, filterHashText))
                throw new ArgumentException("Invalid or stale cursor.");
        }

        List<SlimLogRow> rows;
        bool forward;
        int requestedSize;

        switch (nav)
        {
            case "next":
                forward = true;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("lr.time_unix_nano", "lr.id", "cursorK", "cursorId", descending: true));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;

            case "prev":
                forward = false;
                requestedSize = size;
                clauses.Add(KeysetCursor.Predicate("lr.time_unix_nano", "lr.id", "cursorK", "cursorId", descending: false));
                parameters.Add("cursorK", cursor!.K);
                parameters.Add("cursorId", cursor.Id);
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            case "last":
                forward = false;
                var lastCount = await TryGetExactCountAsync(conn, clauses, parameters, cancellationToken);
                requestedSize = lastCount.HasValue ? KeysetCursor.LastPageRowCount(lastCount.Value, size) : size;
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: false, cancellationToken);
                break;

            default: // "first"
                forward = true;
                requestedSize = size;
                rows = await FetchPageAsync(conn, clauses, parameters, requestedSize, descending: true, cancellationToken);
                break;
        }

        // FetchPageAsync always fetches requestedSize+1 rows in its own query order, so a
        // (requestedSize+1)th row (whichever end it lands on doesn't matter — it's always the last
        // element returned) means there is more data beyond this page in that direction.
        var hasExtra = rows.Count > requestedSize;
        if (hasExtra) rows.RemoveAt(rows.Count - 1);

        List<SlimLogRow> displayRows;
        string? nextCursor;
        string? prevCursor;

        if (forward)
        {
            displayRows = rows;
            nextCursor = hasExtra ? Encode(displayRows[^1], filterHash) : null;
            prevCursor = nav == "first" ? null : (displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null);
        }
        else
        {
            displayRows = [.. rows];
            displayRows.Reverse();
            if (nav == "last")
            {
                nextCursor = null;
                var reachedStart = !hasExtra;
                prevCursor = reachedStart || displayRows.Count == 0 ? null : Encode(displayRows[0], filterHash);
            }
            else // prev
            {
                nextCursor = displayRows.Count > 0 ? Encode(displayRows[^1], filterHash) : null;
                prevCursor = hasExtra && displayRows.Count > 0 ? Encode(displayRows[0], filterHash) : null;
            }
        }

        var resourceById = await LoadResourcesAsync(conn, cancellationToken);
        var scopeById = await LoadScopesAsync(conn, cancellationToken);

        return new LogPageResult
        {
            Items = displayRows.Select(r => MapSlim(r, resourceById, scopeById)).ToList(),
            NextCursor = nextCursor,
            PrevCursor = prevCursor,
            AsOf = asOf
        };
    }

    private static string Encode(SlimLogRow row, string filterHash) => KeysetCursor.Encode(row.TimeUnixNano ?? 0, row.Id, filterHash);

    private async Task<List<SlimLogRow>> FetchPageAsync(
        System.Data.Common.DbConnection conn, List<string> clauses, DynamicParameters parameters,
        int size, bool descending, CancellationToken cancellationToken)
    {
        var where = string.Join(" AND ", clauses);
        var order = descending ? "DESC" : "ASC";
        var sql = $"""
            SELECT
                lr.id                        AS Id,
                lr.time_unix_nano            AS TimeUnixNano,
                lr.observed_time_unix_nano   AS ObservedTimeUnixNano,
                lr.severity_number           AS SeverityNumber,
                lr.severity_text             AS SeverityText,
                lr.event_name                AS EventName,
                lr.body_type                 AS BodyType,
                lr.body_value                AS BodyValue,
                lr.dropped_attributes_count  AS DroppedAttributesCount,
                lr.flags                     AS Flags,
                lr.trace_id                  AS TraceId,
                lr.span_id                   AS SpanId,
                lr.attributes_json           AS AttributesJson,
                lr.resource_id               AS ResourceId,
                lr.scope_id                  AS ScopeId
            FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            ORDER BY lr.time_unix_nano {order}, lr.id {order}
            {PagingClause}
            """;
        var clone = new DynamicParameters(parameters);
        clone.Add("limit", size + 1);
        clone.Add("offset", 0);
        var rows = await conn.QueryAsync<SlimLogRow>(new CommandDefinition(sql, clone, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<long?> TryGetExactCountAsync(
        System.Data.Common.DbConnection conn, List<string> clauses, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        var where = string.Join(" AND ", clauses);
        var sql = $"""
            SELECT COUNT(*) FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            """;
        var (result, timedOut) = await TimedQuery.RunAsync(
            async (timeoutSeconds, ct) => await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                sql, parameters, commandTimeout: timeoutSeconds, cancellationToken: ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return timedOut ? null : result;
    }

    // =========================================================================
    // FACETS (list-pages-server-side plan, Phase 2, decision 15)
    // =========================================================================

    public async Task<LogFacetsResult> GetLogFacetsAsync(LogFacetsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var parsed = SearchQueryParser.Parse(query.Search);
        var startNano = TimeConversion.DateTimeToUnixNano(query.Start);
        var endNano = TimeConversion.DateTimeToUnixNano(query.End);
        var (clauses, parameters) = BuildFilterClauses(query.Service, query.MinSeverity, parsed, startNano, endNano);
        var where = string.Join(" AND ", clauses);

        var sql = $"""
            SELECT lr.attributes_json AS AttributesJson
            FROM log_records lr
            JOIN resources r ON lr.resource_id = r.id
            WHERE r.tenant_id = @tenantId AND {where}
            ORDER BY lr.time_unix_nano DESC
            {PagingClause}
            """;
        parameters.Add("limit", FacetSampleSize);
        parameters.Add("offset", 0);

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var attributeJsonRows = (await conn.QueryAsync<string?>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var valueLimit = Math.Clamp(query.ValueLimit, 1, 100);
        var keyFilter = query.Keys is { Count: > 0 } ? new HashSet<string>(query.Keys) : null;
        var counts = new Dictionary<string, Dictionary<string, int>>();

        foreach (var json in attributeJsonRows)
        {
            var attrs = DeserializeAttributes(json);
            if (attrs == null) continue;
            foreach (var (key, value) in attrs)
            {
                if (keyFilter != null && !keyFilter.Contains(key)) continue;
                var valueText = ConvertAttributeValueToString(value);
                if (!counts.TryGetValue(key, out var valueCounts))
                {
                    valueCounts = new Dictionary<string, int>();
                    counts[key] = valueCounts;
                }
                valueCounts[valueText] = valueCounts.GetValueOrDefault(valueText) + 1;
            }
        }

        var facets = counts.Select(kv => new LogFacet
        {
            Key = kv.Key,
            Values = kv.Value
                .OrderByDescending(v => v.Value)
                .Take(valueLimit)
                .Select(v => new LogFacetValue { Value = v.Key, Count = v.Value })
                .ToList()
        }).OrderBy(f => f.Key).ToList();

        return new LogFacetsResult { SampleSize = attributeJsonRows.Count, Facets = facets };
    }

    // =========================================================================
    // SHARED FILTER COMPILATION
    // =========================================================================

    /// <summary>
    /// Builds the common time/service/minSeverity/search WHERE clauses (plus their parameters)
    /// shared by the summary, page and facets queries. A trace-id-shaped <c>q</c> (decision 10)
    /// filters on <c>lr.trace_id</c> exactly rather than through <see cref="DapperReadRepository.CompileSearch"/>,
    /// whose free-text/attribute compiler has no trace-id concept of its own.
    /// </summary>
    private (List<string> Clauses, DynamicParameters Parameters) BuildFilterClauses(
        string? service, int? minSeverity, ParsedSearchQuery parsed, long startNano, long endNano)
    {
        var clauses = new List<string> { "lr.time_unix_nano >= @start", "lr.time_unix_nano <= @end" };
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", startNano);
        parameters.Add("end", endNano);

        if (!string.IsNullOrEmpty(service))
        {
            clauses.Add($"{ResourceServiceNameExpr()} = @service");
            parameters.Add("service", service);
        }
        if (minSeverity.HasValue)
        {
            clauses.Add("lr.severity_number >= @minSeverity");
            parameters.Add("minSeverity", minSeverity.Value);
        }

        if (parsed.IsTraceIdSearch)
        {
            clauses.Add("lr.trace_id = @traceIdSearch");
            parameters.Add("traceIdSearch", parsed.TraceId);
        }
        else if (parsed.Terms.Count > 0)
        {
            // CompileSearch returns its fragment with a leading " AND " (meant to be appended
            // directly to a WHERE clause); strip exactly that literal prefix rather than trimming
            // characters, which would also eat the leading "N" of a negated term's "NOT (...)".
            var (searchSql, searchParams) = CompileSearch(parsed, "lr.body_value", "lr.attributes_json");
            const string andPrefix = " AND ";
            clauses.Add(searchSql.StartsWith(andPrefix, StringComparison.Ordinal) ? searchSql[andPrefix.Length..] : searchSql);
            parameters.AddDynamicParams(searchParams);
        }

        return (clauses, parameters);
    }

    private async Task<Dictionary<long, ResourceRow>> LoadResourcesAsync(System.Data.Common.DbConnection conn, CancellationToken cancellationToken)
    {
        var resourceRows = await conn.QueryAsync<ResourceRow>(new CommandDefinition(
            "SELECT id AS Id, schema_url AS SchemaUrl, attributes_json AS AttributesJson FROM resources WHERE tenant_id = @tenantId",
            new { tenantId = TenantId }, cancellationToken: cancellationToken));
        return resourceRows.ToDictionary(r => r.Id);
    }

    private async Task<Dictionary<long, ScopeRow>> LoadScopesAsync(System.Data.Common.DbConnection conn, CancellationToken cancellationToken)
    {
        var scopeRows = await conn.QueryAsync<ScopeRow>(new CommandDefinition(
            "SELECT id AS Id, name AS Name, version AS Version, schema_url AS SchemaUrl, attributes_json AS AttributesJson FROM instrumentation_scopes",
            cancellationToken: cancellationToken));
        return scopeRows.ToDictionary(s => s.Id);
    }

    private static LogRecordModel Map(LogRow r) => new()
    {
        Resource = new ResourceModel
        {
            SchemaUrl = r.ResourceSchemaUrl,
            Attributes = DeserializeAttributes(r.ResourceAttributesJson) ?? new Dictionary<string, object>()
        },
        InstrumentationScope = new InstrumentationScopeModel
        {
            Name = r.ScopeName,
            Version = r.ScopeVersion,
            SchemaUrl = r.ScopeSchemaUrl,
            Attributes = DeserializeAttributes(r.ScopeAttributesJson) ?? new Dictionary<string, object>()
        },
        SeverityText = r.SeverityText,
        EventName = r.EventName,
        SeverityNumber = r.SeverityNumber,
        Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>(),
        TraceIdHex = r.TraceId,
        BodyType = r.BodyType == null ? null : Enum.Parse<AttributeType>(r.BodyType),
        BodyValue = r.BodyValue,
        DroppedAttributesCount = r.DroppedAttributesCount,
        Flags = r.Flags,
        ObservedTimeUnixNano = r.ObservedTimeUnixNano,
        SpanIdHex = r.SpanId,
        TimeUnixNano = r.TimeUnixNano
    };

    private static LogRecordModel MapSlim(SlimLogRow r, IReadOnlyDictionary<long, ResourceRow> resourceById, IReadOnlyDictionary<long, ScopeRow> scopeById)
    {
        resourceById.TryGetValue(r.ResourceId, out var resource);
        scopeById.TryGetValue(r.ScopeId, out var scope);
        return new LogRecordModel
        {
            Resource = new ResourceModel
            {
                SchemaUrl = resource?.SchemaUrl,
                Attributes = DeserializeAttributes(resource?.AttributesJson) ?? new Dictionary<string, object>()
            },
            InstrumentationScope = new InstrumentationScopeModel
            {
                Name = scope?.Name ?? "",
                Version = scope?.Version,
                SchemaUrl = scope?.SchemaUrl,
                Attributes = DeserializeAttributes(scope?.AttributesJson) ?? new Dictionary<string, object>()
            },
            SeverityText = r.SeverityText,
            EventName = r.EventName,
            SeverityNumber = r.SeverityNumber,
            Attributes = DeserializeAttributes(r.AttributesJson) ?? new Dictionary<string, object>(),
            TraceIdHex = r.TraceId,
            BodyType = r.BodyType == null ? null : Enum.Parse<AttributeType>(r.BodyType),
            BodyValue = r.BodyValue,
            DroppedAttributesCount = r.DroppedAttributesCount,
            Flags = r.Flags,
            ObservedTimeUnixNano = r.ObservedTimeUnixNano,
            SpanIdHex = r.SpanId,
            TimeUnixNano = r.TimeUnixNano
        };
    }

    private sealed class LogRow
    {
        public long? TimeUnixNano { get; set; }
        public long? ObservedTimeUnixNano { get; set; }
        public int? SeverityNumber { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? BodyType { get; set; }
        public string? BodyValue { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int Flags { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? AttributesJson { get; set; }
        public string? ResourceSchemaUrl { get; set; }
        public string? ResourceAttributesJson { get; set; }
        public string ScopeName { get; set; } = null!;
        public string? ScopeVersion { get; set; }
        public string? ScopeSchemaUrl { get; set; }
        public string? ScopeAttributesJson { get; set; }
    }

    private sealed class SlimLogRow
    {
        public long Id { get; set; }
        public long? TimeUnixNano { get; set; }
        public long? ObservedTimeUnixNano { get; set; }
        public int? SeverityNumber { get; set; }
        public string? SeverityText { get; set; }
        public string? EventName { get; set; }
        public string? BodyType { get; set; }
        public string? BodyValue { get; set; }
        public int DroppedAttributesCount { get; set; }
        public int Flags { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public string? AttributesJson { get; set; }
        public long ResourceId { get; set; }
        public long ScopeId { get; set; }
    }

    private sealed class ResourceRow
    {
        public long Id { get; set; }
        public string? SchemaUrl { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class ScopeRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? Version { get; set; }
        public string? SchemaUrl { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class LogBucketRow
    {
        public int BucketIndex { get; set; }
        public long Trace { get; set; }
        public long Debug { get; set; }
        public long Info { get; set; }
        public long Warn { get; set; }
        public long Error { get; set; }
        public long Fatal { get; set; }
    }

    private sealed class TailBucketRow
    {
        public long BucketIndex { get; set; }
        public long Trace { get; set; }
        public long Debug { get; set; }
        public long Info { get; set; }
        public long Warn { get; set; }
        public long Error { get; set; }
        public long Fatal { get; set; }
    }

    private sealed class SummaryBucketRow
    {
        public long BucketUnixNano { get; set; }
        public long Trace { get; set; }
        public long Debug { get; set; }
        public long Info { get; set; }
        public long Warn { get; set; }
        public long Error { get; set; }
        public long Fatal { get; set; }
    }

    private sealed class RollupStateRow
    {
        public long? CoverageStartUnixNano { get; set; }
        public long RolledUntilUnixNano { get; set; }
    }
}
