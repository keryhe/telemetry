using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Core.Data.Read.MetricSeriesPipeline;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// <see cref="IMetricReadRepository"/> over the ClickHouse row model (plans/clickhouse-redesign phase 5, README R4).
///
/// <b>Catalog.</b> <c>metric_catalog</c> holds one row per (tenant, service, metric name, type), so a metric's id is a hash of that key
/// (README R6, <see cref="ClickHouseIds.MetricId"/>) and a catalog "instance" is a service, not a resource. "Seen in range" is the
/// catalog's <c>first_seen</c>/<c>last_seen</c> (accurate to <c>CatalogRefreshSeconds</c>) for a window ending within the last hour,
/// and an existence check on the type's points table for an older window.
///
/// <b>Series.</b> One points table, grouped by <c>series_id</c> per time bucket in SQL; the per-stream math, merging, top-N and "other"
/// fold are the shared <see cref="MetricSeriesPipeline"/> the relational providers run. A stream's labels come from <c>metric_series</c>.
/// </summary>
public sealed class ClickHouseMetricReadRepository : DapperReadRepository, IMetricReadRepository
{
    private const int MaxLabelSampleRows = 1_000;

    private readonly string _connectionString;
    private readonly int _summaryTimeoutSeconds;

    public ClickHouseMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext) : base(tenantContext)
    {
        _connectionString = configuration.GetConnectionString("Api")!;
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured) ? configured : 5;
    }

    protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => ClickHouseConnectionFactory.OpenReadAsync(_connectionString, cancellationToken);

    private static string At(string parameter) => $"fromUnixTimestamp64Nano(CAST(@{parameter} AS Int64), 'UTC')";

    private static string TableFor(MetricType type) => type switch
    {
        MetricType.GAUGE => "gauge_points",
        MetricType.SUM => "sum_points",
        MetricType.HISTOGRAM => "histogram_points",
        MetricType.EXPONENTIAL_HISTOGRAM => "exp_histogram_points",
        _ => "summary_points"
    };

    // =========================================================================
    // CATALOG ROWS
    // =========================================================================

    private sealed class CatalogRow
    {
        public string ServiceName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "GAUGE";
        public string? Unit { get; set; }
        public string? Description { get; set; }
        public long FirstNano { get; set; }
        public long LastNano { get; set; }

        public MetricType MetricType => Enum.Parse<MetricType>(Type);
        public long Id(long tenant) => ClickHouseIds.MetricId(tenant, ServiceName, Name, MetricType);
    }

    private const string CatalogSelect = """
        SELECT service_name AS ServiceName, metric_name AS Name, toString(metric_type) AS Type,
               anyLast(unit) AS Unit, anyLast(description) AS Description,
               toUnixTimestamp64Nano(min(first_seen)) AS FirstNano, toUnixTimestamp64Nano(max(last_seen)) AS LastNano
        FROM metric_catalog
        """;

    private async Task<List<CatalogRow>> ReadCatalogAsync(
        DbConnection conn, string where, DynamicParameters parameters, string having, string order, int? limit, CancellationToken ct)
    {
        parameters.Add("tenantId", (ulong)TenantId);
        var sql = $"""
            {CatalogSelect}
            WHERE tenant_id = @tenantId{where}
            GROUP BY service_name, metric_name, metric_type
            {(having.Length > 0 ? "HAVING " + having : "")}
            ORDER BY {order}
            {(limit is { } n ? "LIMIT " + n : "")}
            """;
        return (await conn.QueryAsync<CatalogRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
    }

    private const string NewestFirst = "FirstNano DESC, service_name DESC, metric_name DESC, metric_type DESC";

    private MetricInfo ToInfo(CatalogRow r, int count = 0) => new()
    {
        Id = r.Id(TenantId), Name = r.Name, Description = NullIfEmpty(r.Description), Unit = NullIfEmpty(r.Unit), Type = r.MetricType,
        ServiceName = NullIfEmpty(r.ServiceName), FirstSeen = TimeConversion.UnixNanoToDateTime(r.FirstNano),
        LastSeen = TimeConversion.UnixNanoToDateTime(r.LastNano), DataPointCount = count
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // =========================================================================
    // METADATA READS
    // =========================================================================

    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await ReadCatalogAsync(conn, "", new DynamicParameters(), "", NewestFirst, null, cancellationToken);
        // Parity with the relational providers: a found metric maps to an (intentionally empty) MetricModel.
        return rows.Any(r => r.Id(TenantId) == id) ? new MetricModel() : null;
    }

    public async Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("name", name);
        var rows = await ReadCatalogAsync(conn, " AND metric_name = @name", p, "", NewestFirst, null, cancellationToken);
        var counts = await PointCountsAsync(conn, rows, cancellationToken);
        return rows.Select(r => ToInfo(r, counts.GetValueOrDefault((r.ServiceName, r.Name, r.Type)))).ToList();
    }

    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("type", type.ToString());
        var rows = await ReadCatalogAsync(conn, " AND metric_type = @type", p, "", NewestFirst, null, cancellationToken);
        var counts = await PointCountsAsync(conn, rows, cancellationToken);
        return rows.Select(r => ToInfo(r, counts.GetValueOrDefault((r.ServiceName, r.Name, r.Type)))).ToList();
    }

    /// <summary>All-time point counts per (service, name, type), one grouped read per points table involved.</summary>
    private async Task<Dictionary<(string, string, string), int>> PointCountsAsync(DbConnection conn, List<CatalogRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), int>();
        foreach (var group in rows.GroupBy(r => r.MetricType))
        {
            var p = new DynamicParameters();
            p.Add("tenantId", (ulong)TenantId);
            p.Add("names", group.Select(r => r.Name).Distinct().ToArray());
            var counts = await conn.QueryAsync<CountRow>(new CommandDefinition($"""
                SELECT service_name AS ServiceName, metric_name AS Name, count() AS Cnt
                FROM {TableFor(group.Key)}
                WHERE tenant_id = @tenantId AND metric_name IN @names
                GROUP BY service_name, metric_name
                """, p, cancellationToken: ct));
            foreach (var c in counts)
                result[(c.ServiceName, c.Name, group.Key.ToString())] = (int)Math.Min(c.Cnt, int.MaxValue);
        }
        return result;
    }

    public async Task<MetricsSummary> GetMetricsSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var p = new DynamicParameters();
        var (where, having) = startTime.HasValue && endTime.HasValue ? SeenInRange(startTime.Value, endTime.Value, p) : ("", "");
        var rows = await ReadCatalogAsync(conn, where, p, having, NewestFirst, null, cancellationToken);

        // A name could in principle appear with more than one type (rare); the first one seen wins.
        var byName = new Dictionary<string, MetricType>();
        foreach (var r in rows) byName.TryAdd(r.Name, r.MetricType);
        return new MetricsSummary
        {
            UniqueMetricCount = byName.Count,
            CountsByType = byName.Values.GroupBy(t => t).Select(g => new MetricTypeCount { Type = g.Key, Count = g.Count() }).ToList()
        };
    }

    public async Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await ServiceCatalogAsync(conn, serviceName, cancellationToken);
        return rows.GroupBy(r => r.MetricType).ToDictionary(g => g.Key.ToString(), g => g.Count());
    }

    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await ServiceCatalogAsync(conn, serviceName, cancellationToken);
        return rows.Select(r => r.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    private Task<List<CatalogRow>> ServiceCatalogAsync(DbConnection conn, string? serviceName, CancellationToken ct)
    {
        var p = new DynamicParameters();
        var where = "";
        if (!string.IsNullOrEmpty(serviceName)) { where = " AND service_name = @service"; p.Add("service", serviceName); }
        return ReadCatalogAsync(conn, where, p, "", NewestFirst, null, ct);
    }

    public async Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var result = new Dictionary<string, double>();
        foreach (var table in new[] { "gauge_points", "sum_points" })
        {
            var rows = await conn.QueryAsync<LatestRow>(new CommandDefinition($"""
                SELECT metric_name AS MetricName, argMax(value, time) AS Value
                FROM {table}
                WHERE tenant_id = @tenantId AND service_name = @service
                GROUP BY metric_name
                """, new { tenantId = (ulong)TenantId, service = serviceName }, cancellationToken: cancellationToken));
            foreach (var r in rows) result[r.MetricName] = r.Value;
        }
        return result;
    }

    // =========================================================================
    // CATALOG PAGE
    // =========================================================================

    /// <summary>
    /// "Seen in range": for a window that ended within the last hour, the catalog row's own <c>first_seen</c>/<c>last_seen</c> overlap
    /// the window (<c>HAVING</c>, since a row may be several partials); for an older window, the (service, name) has a point in it
    /// (an existence check on the type's points table, <c>WHERE</c>).
    /// </summary>
    private (string Where, string Having) SeenInRange(DateTime start, DateTime end, DynamicParameters parameters)
    {
        parameters.Add("seenStart", TimeConversion.DateTimeToUnixNano(start));
        parameters.Add("seenEnd", TimeConversion.DateTimeToUnixNano(end));
        if (DateTime.UtcNow - end <= TimeSpan.FromHours(1))
            return ("", $"max(last_seen) >= {At("seenStart")} AND min(first_seen) <= {At("seenEnd")}");

        var exists = string.Join(" OR ", Enum.GetValues<MetricType>().Select(t =>
            $"(metric_type = '{t}' AND (service_name, metric_name) IN (SELECT service_name, metric_name FROM {TableFor(t)} WHERE tenant_id = @tenantId AND time >= {At("seenStart")} AND time <= {At("seenEnd")} AND toStartOfHour(time) >= toStartOfHour({At("seenStart")}) AND toStartOfHour(time) <= toStartOfHour({At("seenEnd")})))"));
        return ($" AND ({exists})", "");
    }

    public async Task<MetricCatalogPage> GetMetricCatalogPageAsync(MetricCatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Max(1, query.Limit);
        var p = new DynamicParameters();
        var (seenWhere, having) = SeenInRange(query.Start, query.End, p);
        var where = new StringBuilder(seenWhere);
        if (!string.IsNullOrWhiteSpace(query.Q)) { where.Append(" AND positionCaseInsensitiveUTF8(metric_name, @nameSearch) > 0"); p.Add("nameSearch", query.Q); }
        if (!string.IsNullOrWhiteSpace(query.Service)) { where.Append(" AND service_name = @service"); p.Add("service", query.Service); }
        if (query.Type.HasValue) { where.Append(" AND metric_type = @metricType"); p.Add("metricType", query.Type.Value.ToString()); }

        await using var conn = await OpenConnectionAsync(cancellationToken);
        if (!string.Equals(query.GroupBy, "name", StringComparison.OrdinalIgnoreCase))
        {
            var rows = await ReadCatalogAsync(conn, where.ToString(), p, having, NewestFirst, limit + 1, cancellationToken);
            var truncated = rows.Count > limit;
            if (truncated) rows.RemoveAt(rows.Count - 1);
            return new MetricCatalogPage { Items = rows.Select(r => ToInfo(r)).ToList(), Truncated = truncated };
        }

        // groupBy=name: the catalog is one row per (service, metric, type), small enough to group here.
        var all = await ReadCatalogAsync(conn, where.ToString(), p, having, NewestFirst, null, cancellationToken);
        var names = all.GroupBy(r => r.Name)
            .Select(g => (Name: g.Key, Newest: g.Max(r => r.FirstNano), Rows: g.OrderBy(r => r.FirstNano).ToList()))
            .OrderByDescending(g => g.Newest).ThenByDescending(g => g.Name, StringComparer.Ordinal)
            .Take(limit + 1).ToList();
        var nameTruncated = names.Count > limit;
        if (nameTruncated) names.RemoveAt(names.Count - 1);

        return new MetricCatalogPage
        {
            Truncated = nameTruncated,
            Names = names.Select(g => new UniqueMetricSummary
            {
                Name = g.Name,
                Type = g.Rows[0].MetricType,
                Unit = NullIfEmpty(g.Rows[0].Unit),
                Description = NullIfEmpty(g.Rows[0].Description),
                InstanceCount = g.Rows.Count,
                Services = g.Rows.Select(r => r.ServiceName).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList(),
                LastSeen = TimeConversion.UnixNanoToDateTime(g.Rows.Max(r => r.LastNano))
            }).ToList()
        };
    }

    // =========================================================================
    // SERIES
    // =========================================================================

    public async Task<MetricSeriesResult?> GetMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("name", query.MetricName);
        var rows = await ReadCatalogAsync(conn, " AND metric_name = @name", p, "", NewestFirst, null, cancellationToken);
        if (query.MetricId.HasValue) rows = rows.Where(r => r.Id(TenantId) == query.MetricId.Value).ToList();
        if (rows.Count == 0) return null;

        var type = rows[0].MetricType;
        rows = rows.Where(r => r.MetricType == type).ToList();
        var unit = NullIfEmpty(rows[0].Unit);
        var services = rows.Select(r => r.ServiceName).Distinct().ToArray();

        var points = Math.Clamp(query.Points, 1, 1000);
        var top = Math.Max(1, query.Top);

        // Full-resolution attempt, then one retry at a quarter of the points on a fresh connection (the timed-out one is never
        // reused), then a "window too large" (timed out) result rather than a 500.
        var (result, timedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => RunSeriesAsync(conn, type, services, query, points, top, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);
        if (!timedOut) return result;

        await using var retryConn = await OpenConnectionAsync(cancellationToken);
        var (retry, retryTimedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => RunSeriesAsync(retryConn, type, services, query, Math.Max(1, points / 4), top, timeoutSeconds, ct),
            _summaryTimeoutSeconds, cancellationToken);
        if (retryTimedOut)
            return new MetricSeriesResult { Name = query.MetricName, Type = type, Unit = unit, TimedOut = true };
        retry!.TimedOut = false;
        return retry;
    }

    public async IAsyncEnumerable<MetricExportRow> ExportMetricSeriesAsync(
        MetricExportQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await GetMetricSeriesAsync(new MetricSeriesQuery
        {
            MetricName = query.MetricName, MetricId = query.MetricId, Start = query.Start, End = query.End,
            LabelFilters = query.LabelFilters, Points = query.Points, Top = int.MaxValue
        }, cancellationToken);
        if (result == null) yield break;

        foreach (var series in result.Series.OrderBy(s => s.SeriesName, StringComparer.Ordinal))
            foreach (var point in series.Points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MetricExportRow
                {
                    MetricName = result.Name, SeriesName = series.SeriesName, ServiceName = series.ServiceName, Labels = series.Labels,
                    BucketStart = point.Timestamp, Value = point.Value, Min = point.Min, Max = point.Max, Count = point.Count, Sum = point.Sum,
                    BucketCounts = point.BucketCounts, BucketBounds = point.BucketBounds, Quantiles = point.Quantiles,
                    QuantileValues = point.QuantileValues, Rate = point.Rate
                };
            }
    }

    private async Task<MetricSeriesResult> RunSeriesAsync(
        DbConnection conn, MetricType type, string[] services, MetricSeriesQuery query, int points, int top, int timeoutSeconds, CancellationToken ct)
    {
        var b = ComputeBucketing(query.Start, query.End, points);
        var scope = new Scope(this, services, query.MetricName, query.LabelFilters, b, timeoutSeconds);

        var streams = type switch
        {
            MetricType.GAUGE => await LoadGaugeAsync(conn, scope, ct),
            MetricType.SUM => await LoadSumAsync(conn, scope, ct),
            MetricType.HISTOGRAM => await LoadDistributionAsync(conn, scope, exponential: false, ct),
            MetricType.EXPONENTIAL_HISTOGRAM => await LoadDistributionAsync(conn, scope, exponential: true, ct),
            _ => await LoadSummaryAsync(conn, scope, ct)
        };

        // labels and service per stream
        var info = await SeriesInfoAsync(conn, services, query.MetricName, streams.Values.Select(s => (ulong)s.MetricId).ToArray(), ct);
        foreach (var stream in streams.Values)
        {
            if (info.TryGetValue((ulong)stream.MetricId, out var s))
            {
                stream.ServiceName = string.IsNullOrEmpty(s.Service) ? scope.ServiceOf(stream.MetricId) : s.Service;
                stream.Attributes = s.Attributes.Count == 0 ? null : s.Attributes.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            }
            else stream.ServiceName = scope.ServiceOf(stream.MetricId);
            if (string.IsNullOrEmpty(stream.ServiceName)) stream.ServiceName = "unknown";
        }

        var (series, other) = BuildDisplaySeriesAndOther(type, streams.Values.ToList(), b, top);
        return new MetricSeriesResult
        {
            Name = query.MetricName, Type = type, BucketWidthMs = b.BucketNanos / 1_000_000, Series = series, Other = other
        };
    }

    /// <summary>Labels and service of the returned series, from <c>metric_series</c> (one row per series; the newest partial wins).</summary>
    private async Task<Dictionary<ulong, (string Service, Dictionary<string, string> Attributes)>> SeriesInfoAsync(
        DbConnection conn, string[] services, string metricName, ulong[] ids, CancellationToken ct)
    {
        var result = new Dictionary<ulong, (string, Dictionary<string, string>)>();
        if (ids.Length == 0) return result;
        var rows = await conn.QueryAsync<SeriesInfoRow>(new CommandDefinition("""
            SELECT series_id AS SeriesId, any(service_name) AS ServiceName, argMax(attributes, last_seen) AS SeriesAttributes
            FROM metric_series
            WHERE tenant_id = @tenantId AND metric_name = @name AND service_name IN @services AND series_id IN @ids
            GROUP BY series_id
            """, new { tenantId = (ulong)TenantId, name = metricName, services, ids }, cancellationToken: ct));
        foreach (var r in rows) result[r.SeriesId] = (r.ServiceName, r.SeriesAttributes ?? new Dictionary<string, string>());
        return result;
    }

    /// <summary>One series request's shared SQL fragments and parameters.</summary>
    private sealed class Scope
    {
        public readonly string Where;
        public readonly string Bucket;
        public readonly DynamicParameters Parameters = new();
        public readonly Bucketing B;
        public readonly int TimeoutSeconds;
        public readonly string[] Services;
        private readonly Dictionary<long, string> _serviceBySeries = new();

        public Scope(ClickHouseMetricReadRepository repo, string[] services, string name, Dictionary<string, string>? labelFilters, Bucketing b, int timeoutSeconds)
        {
            B = b; TimeoutSeconds = timeoutSeconds; Services = services;
            Parameters.Add("tenantId", (ulong)repo.TenantId);
            Parameters.Add("name", name);
            Parameters.Add("services", services);
            Parameters.Add("start", b.StartNano);
            Parameters.Add("end", b.EndNano);
            Parameters.Add("bucketNanos", b.BucketNanos);
            Parameters.Add("pointsMinus1", b.Points - 1);

            var where = new StringBuilder("tenant_id = @tenantId AND metric_name = @name AND service_name IN @services");
            var i = 0;
            foreach (var (k, v) in labelFilters ?? [])
            {
                Parameters.Add($"lblKey{i}", k);
                Parameters.Add($"lblVal{i}", v.ToLowerInvariant());
                where.Append($" AND mapContains(attributes, @lblKey{i}) = 1 AND lowerUTF8(attributes[@lblKey{i}]) = @lblVal{i}");
                i++;
            }
            Where = where.ToString();
            Bucket = "least(toInt32(@pointsMinus1), greatest(toInt32(0), toInt32(intDiv(toUnixTimestamp64Nano(time) - @start, @bucketNanos))))";
        }

        // The sort key is (..., toStartOfHour(time), series_id, time): a bound on `time` alone does not prune it, so every time range
        // also bounds the hour (the same rule as the logs' five-minute bucket).
        public string Window => $" AND time >= {At("start")} AND time < {At("end")} AND toStartOfHour(time) >= toStartOfHour({At("start")}) AND toStartOfHour(time) <= toStartOfHour({At("end")})";
        public string Before => $" AND time < {At("start")} AND toStartOfHour(time) <= toStartOfHour({At("start")})";
        public CommandDefinition Cmd(string sql, CancellationToken ct, DynamicParameters? p = null)
            => new(sql, p ?? Parameters, commandTimeout: TimeoutSeconds > 0 ? TimeoutSeconds : null, cancellationToken: ct);

        public void Remember(long seriesId, string service) => _serviceBySeries[seriesId] = service;
        public string ServiceOf(long seriesId) => _serviceBySeries.GetValueOrDefault(seriesId, "");
    }

    private static StreamAgg Stream(Dictionary<string, StreamAgg> streams, Scope scope, SeriesRow r)
    {
        scope.Remember((long)r.SeriesId, r.ServiceName ?? "");
        return GetOrAddStream(streams, (long)r.SeriesId, "");
    }

    private static MetricPointRow ToPoint(SeriesRow r) => new()
    {
        MetricId = (long)r.SeriesId, AttributesJson = "", Bucket = r.Bucket, TimeUnixNano = r.Tn, StartTimeUnixNano = r.StartNano,
        ValueDouble = r.Value, Count = r.Cnt is { } c ? (long)c : null, SumValue = r.SumValue, MinValue = r.MinValue, MaxValue = r.MaxValue,
        BucketCounts = r.BucketCounts is null ? null : JsonSerializer.Serialize(r.BucketCounts),
        ExplicitBounds = r.ExplicitBounds is null ? null : JsonSerializer.Serialize(r.ExplicitBounds),
        Scale = r.Scale, ZeroCount = r.ZeroCount is { } z ? (long)z : null, PositiveOffset = r.PositiveOffset,
        PositiveBucketCounts = r.PositiveBucketCounts is null ? null : JsonSerializer.Serialize(r.PositiveBucketCounts),
        QuantileValues = r.QuantileQ is null ? null : JsonSerializer.Serialize(
            r.QuantileQ.Select((q, i) => new QuantileValueModel { Quantile = q, Value = r.QuantileV![i] }).ToList())
    };

    private async Task<AggregationTemporality> PeekTemporalityAsync(DbConnection conn, Scope scope, string table, CancellationToken ct)
    {
        var value = await conn.QuerySingleOrDefaultAsync<string?>(scope.Cmd(
            $"SELECT toString(temporality) FROM {table} WHERE {scope.Where} ORDER BY time DESC LIMIT 1", ct));
        return value != null && Enum.TryParse<AggregationTemporality>(value, out var t) ? t : AggregationTemporality.CUMULATIVE;
    }

    // ---- gauge ----

    private async Task<Dictionary<string, StreamAgg>> LoadGaugeAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                   avg(value) AS AvgValue, min(value) AS MinValue, max(value) AS MaxValue
            FROM gauge_points
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, Bucket
            """, ct));
        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in rows)
            Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg { Avg = r.AvgValue, Min = r.MinValue, Max = r.MaxValue };
        return streams;
    }

    // ---- sum ----

    private const string CoveredNanos =
        "sum(if(toUnixTimestamp64Nano(start_time) > 0 AND toUnixTimestamp64Nano(start_time) < toUnixTimestamp64Nano(time), toUnixTimestamp64Nano(time) - toUnixTimestamp64Nano(start_time), 0))";

    private async Task<Dictionary<string, StreamAgg>> LoadSumAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var temporality = await PeekTemporalityAsync(conn, scope, "sum_points", ct);
        if (temporality == AggregationTemporality.DELTA)
        {
            var rows = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
                SELECT series_id AS SeriesId, any(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                       sum(value) AS SumOfValue, toFloat64({CoveredNanos}) AS CoveredNanos
                FROM sum_points
                WHERE {scope.Where}{scope.Window}
                GROUP BY series_id, Bucket
                """, ct));
            var streams = new Dictionary<string, StreamAgg>();
            foreach (var r in rows)
            {
                var value = r.SumOfValue ?? 0;
                Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg { Value = value, Rate = DeltaRate(value, r.CoveredNanos, scope.B) };
            }
            return streams;
        }

        var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, "sum_points", "argMax(value, time) AS Value", ct);
        return ComputeCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano);
    }

    /// <summary>
    /// The cumulative types' two reads: the last point per (series, bucket) in the window, and each series' last point before it
    /// (the baseline the first bucket's increase is measured against).
    /// </summary>
    private async Task<(List<MetricPointRow> Buckets, List<MetricPointRow> Baseline)> LastPerBucketAndBaselineAsync(
        DbConnection conn, Scope scope, string table, string valueColumns, CancellationToken ct)
    {
        var last = $"toUnixTimestamp64Nano(max(time)) AS Tn, toUnixTimestamp64Nano(argMax(start_time, time)) AS StartNano, {valueColumns}";
        var buckets = (await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any(service_name) AS ServiceName, {scope.Bucket} AS Bucket, {last}
            FROM {table}
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, Bucket
            """, ct))).ToList();
        foreach (var r in buckets) scope.Remember((long)r.SeriesId, r.ServiceName ?? "");

        var baseline = (await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any(service_name) AS ServiceName, 0 AS Bucket, {last}
            FROM {table}
            WHERE {scope.Where}{scope.Before}
            GROUP BY series_id
            """, ct))).ToList();
        return (buckets.Select(ToPoint).ToList(), baseline.Select(ToPoint).ToList());
    }

    // ---- histogram / exponential histogram ----

    private async Task<Dictionary<string, StreamAgg>> LoadDistributionAsync(DbConnection conn, Scope scope, bool exponential, CancellationToken ct)
    {
        var table = exponential ? "exp_histogram_points" : "histogram_points";
        var temporality = await PeekTemporalityAsync(conn, scope, table, ct);

        if (temporality != AggregationTemporality.DELTA)
        {
            var columns = exponential
                ? "argMax(count, time) AS Cnt, argMax(sum, time) AS SumValue, argMax(min, time) AS MinValue, argMax(max, time) AS MaxValue, argMax(scale, time) AS Scale, argMax(zero_count, time) AS ZeroCount, argMax(positive_offset, time) AS PositiveOffset, argMax(positive_bucket_counts, time) AS PositiveBucketCounts"
                : "argMax(count, time) AS Cnt, argMax(sum, time) AS SumValue, argMax(min, time) AS MinValue, argMax(max, time) AS MaxValue, argMax(bucket_counts, time) AS BucketCounts, argMax(explicit_bounds, time) AS ExplicitBounds";
            var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, table, columns, ct);
            return exponential
                ? ComputeExpHistogramCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano)
                : ComputeHistogramCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano);
        }

        // delta: count, sum, min and max per (series, bucket) ...
        var aggregates = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                   sum(count) AS Cnt, sum(sum) AS SumValue, min(min) AS MinValue, max(max) AS MaxValue, toFloat64({CoveredNanos}) AS CoveredNanos
            FROM {table}
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, Bucket
            """, ct));
        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in aggregates)
        {
            var count = r.Cnt is { } c ? (long)c : 0;
            Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg
            {
                Count = count, Sum = r.SumValue, Min = r.MinValue, Max = r.MaxValue, Rate = DeltaRate(count, r.CoveredNanos, scope.B)
            };
        }

        // ... and the bucket-count arrays, summed element-wise by the database
        if (!exponential)
        {
            var arrays = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
                SELECT series_id AS SeriesId, {scope.Bucket} AS Bucket, sumForEach(bucket_counts) AS BucketCounts, any(explicit_bounds) AS ExplicitBounds
                FROM {table}
                WHERE {scope.Where}{scope.Window}
                GROUP BY series_id, Bucket
                """, ct));
            foreach (var r in arrays)
            {
                var stream = GetOrAddStream(streams, (long)r.SeriesId, "");
                stream.Bounds ??= r.ExplicitBounds;
                if (r.BucketCounts is { } counts && stream.Bounds != null && counts.Length == stream.Bounds.Length + 1
                    && stream.Buckets.TryGetValue(r.Bucket, out var bucket))
                    bucket.Counts = counts.Select(x => (long)x).ToArray();
            }
            return streams;
        }

        var targetScale = await conn.ExecuteScalarAsync<int?>(scope.Cmd(
            $"SELECT min(scale) FROM {table} WHERE {scope.Where}{scope.Window}", ct)) ?? 0;
        var sparse = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, {scope.Bucket} AS Bucket, scale AS Scale, positive_offset AS PositiveOffset,
                   sumForEach(positive_bucket_counts) AS PositiveBucketCounts, sum(zero_count) AS ZeroCount
            FROM {table}
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, Bucket, scale, positive_offset
            """, ct));
        foreach (var r in sparse)
        {
            var stream = GetOrAddStream(streams, (long)r.SeriesId, "");
            if (!stream.Buckets.TryGetValue(r.Bucket, out var bucket)) stream.Buckets[r.Bucket] = bucket = new BucketAgg { Count = 0 };
            bucket.SparseCounts ??= new Dictionary<long, long>();
            foreach (var kv in DownscaleRow(ToPoint(r), targetScale))
                bucket.SparseCounts[kv.Key] = bucket.SparseCounts.GetValueOrDefault(kv.Key) + kv.Value;
            bucket.ZeroCountValue = (bucket.ZeroCountValue ?? 0) + (r.ZeroCount is { } z ? (long)z : 0);
        }
        return streams;
    }

    // ---- summary ----

    private async Task<Dictionary<string, StreamAgg>> LoadSummaryAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var columns = "argMax(count, time) AS Cnt, argMax(sum, time) AS SumValue, argMax(quantiles.quantile, time) AS QuantileQ, argMax(quantiles.value, time) AS QuantileV";
        var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, "summary_points", columns, ct);
        return ComputeSummaryDeltas(bucketRows, ToDictionaryNewest(baselineRows, r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano), scope.B.StartNano);
    }

    // =========================================================================
    // EXEMPLARS
    // =========================================================================

    public async Task<MetricExemplarPage?> GetMetricExemplarsAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("name", query.MetricName);
        var rows = await ReadCatalogAsync(conn, " AND metric_name = @name", p, "", NewestFirst, null, cancellationToken);
        if (query.MetricId.HasValue) rows = rows.Where(r => r.Id(TenantId) == query.MetricId.Value).ToList();
        if (rows.Count == 0) return null;

        var type = rows[0].MetricType;
        var page = new MetricExemplarPage { Name = query.MetricName, Type = type };
        if (type == MetricType.SUMMARY) return page; // no exemplars

        var cap = Math.Max(1, query.Limit);
        var services = rows.Where(r => r.MetricType == type).Select(r => r.ServiceName).Distinct().ToArray();
        var table = TableFor(type);
        var valueColumns = type is MetricType.GAUGE or MetricType.SUM ? "value AS PointValue, 0 AS PointCount" : "0 AS PointValue, count AS PointCount";

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", (ulong)TenantId);
        parameters.Add("name", query.MetricName);
        parameters.Add("services", services);
        var where = new StringBuilder("tenant_id = @tenantId AND metric_name = @name AND service_name IN @services AND notEmpty(exemplars.time)");
        where.Append($" AND time >= {At("start")} AND time <= {At("end")} AND toStartOfHour(time) >= toStartOfHour({At("start")}) AND toStartOfHour(time) <= toStartOfHour({At("end")})");
        parameters.Add("start", TimeConversion.DateTimeToUnixNano(query.Start));
        parameters.Add("end", TimeConversion.DateTimeToUnixNano(query.End));
        var i = 0;
        foreach (var (k, v) in query.LabelFilters ?? [])
        {
            parameters.Add($"lblKey{i}", k);
            parameters.Add($"lblVal{i}", v.ToLowerInvariant());
            where.Append($" AND mapContains(attributes, @lblKey{i}) = 1 AND lowerUTF8(attributes[@lblKey{i}]) = @lblVal{i}");
            i++;
        }

        // newest cap + 1 exemplars: the extra one only says that more exist
        var found = (await conn.QueryAsync<ExemplarRow>(new CommandDefinition($"""
            SELECT service_name AS ServiceName, attributes AS PointAttributes, toUnixTimestamp64Nano(time) AS PointNano, {valueColumns},
                   toUnixTimestamp64Nano(ex_time) AS ExNano, ex_value AS ExValue, ex_trace AS ExTrace, ex_span AS ExSpan, ex_filtered AS ExFiltered
            FROM {table}
            ARRAY JOIN exemplars.time AS ex_time, exemplars.value AS ex_value, exemplars.trace_id AS ex_trace, exemplars.span_id AS ex_span,
                       exemplars.filtered_attributes AS ex_filtered
            WHERE {where}
            ORDER BY ex_time DESC
            LIMIT {cap + 1}
            """, parameters, cancellationToken: cancellationToken))).ToList();

        page.Truncated = found.Count > cap;
        page.Exemplars = found.Take(cap).Select(r =>
        {
            var labels = r.PointAttributes ?? new Dictionary<string, string>();
            return new MetricExemplar
            {
                Exemplar = new ExemplarModel
                {
                    TimeUnixNano = r.ExNano, ValueDouble = r.ExValue,
                    TraceIdHex = r.ExTrace == Guid.Empty ? null : ClickHouseIds.GuidToTraceIdHex(r.ExTrace),
                    SpanIdHex = r.ExSpan == 0 ? null : ClickHouseIds.UInt64ToSpanIdHex(r.ExSpan),
                    FilteredAttributes = r.ExFiltered is { Count: > 0 } f ? f.ToDictionary(kv => kv.Key, kv => (object)kv.Value) : null
                },
                SeriesName = BuildSeriesDisplayName(r.ServiceName, labels),
                ServiceName = string.IsNullOrEmpty(r.ServiceName) ? "unknown" : r.ServiceName,
                Labels = new Dictionary<string, string>(labels),
                PointTimestamp = TimeConversion.UnixNanoToDateTime(r.PointNano),
                PointCount = type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM ? (long)r.PointCount : null,
                PointDoubleValue = type is MetricType.GAUGE or MetricType.SUM ? r.PointValue : null
            };
        }).ToList();
        return page;
    }

    // =========================================================================
    // LABELS
    // =========================================================================

    public async Task<MetricLabelsResult> GetMetricLabelsAsync(string metricName, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        var start = startTime ?? DateTime.UtcNow.AddHours(-24);
        var end = endTime ?? DateTime.UtcNow;
        var p = new DynamicParameters();
        p.Add("tenantId", (ulong)TenantId);
        p.Add("name", metricName);
        p.Add("start", TimeConversion.DateTimeToUnixNano(start));
        p.Add("end", TimeConversion.DateTimeToUnixNano(end));

        // One row per series that reported in the window (a series row is refreshed every CatalogRefreshSeconds while it reports).
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var sets = (await conn.QueryAsync<AttributesRow>(new CommandDefinition($"""
            SELECT argMax(attributes, last_seen) AS SeriesAttributes
            FROM metric_series
            WHERE tenant_id = @tenantId AND metric_name = @name AND last_seen >= {At("start")} AND first_seen <= {At("end")}
            GROUP BY service_name, series_id
            LIMIT {MaxLabelSampleRows}
            """, p, cancellationToken: cancellationToken))).ToList();

        var labels = new Dictionary<string, HashSet<string>>();
        foreach (var set in sets)
            foreach (var (key, value) in set.SeriesAttributes ?? [])
            {
                if (!labels.TryGetValue(key, out var values)) labels[key] = values = new HashSet<string>();
                values.Add(value);
            }
        return new MetricLabelsResult
        {
            Labels = labels.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(v => v, StringComparer.Ordinal).ToList()),
            Partial = sets.Count == MaxLabelSampleRows
        };
    }

    // =========================================================================
    // ROW DTOs
    // =========================================================================

    private sealed class CountRow
    {
        public string ServiceName { get; set; } = "";
        public string Name { get; set; } = "";
        public ulong Cnt { get; set; }
    }

    private sealed class LatestRow
    {
        public string MetricName { get; set; } = "";
        public double Value { get; set; }
    }

    private sealed class AttributesRow
    {
        public Dictionary<string, string>? SeriesAttributes { get; set; }
    }

    private sealed class SeriesInfoRow
    {
        public ulong SeriesId { get; set; }
        public string ServiceName { get; set; } = "";
        public Dictionary<string, string>? SeriesAttributes { get; set; }
    }

    private sealed class ExemplarRow
    {
        public string ServiceName { get; set; } = "";
        public Dictionary<string, string>? PointAttributes { get; set; }
        public long PointNano { get; set; }
        public double PointValue { get; set; }
        public ulong PointCount { get; set; }
        public long ExNano { get; set; }
        public double ExValue { get; set; }
        public Guid ExTrace { get; set; }
        public ulong ExSpan { get; set; }
        public Dictionary<string, string>? ExFiltered { get; set; }
    }

    /// <summary>One wide row for every series projection; a SQL text selects the columns it needs, the rest stay default.</summary>
    private sealed class SeriesRow
    {
        public ulong SeriesId { get; set; }
        public string? ServiceName { get; set; }
        public int Bucket { get; set; }
        public long Tn { get; set; }
        public long? StartNano { get; set; }
        public double? Value { get; set; }
        public double? AvgValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public double? SumOfValue { get; set; }
        public double? CoveredNanos { get; set; }
        public ulong? Cnt { get; set; }
        public double? SumValue { get; set; }
        public ulong[]? BucketCounts { get; set; }
        public double[]? ExplicitBounds { get; set; }
        public int? Scale { get; set; }
        public ulong? ZeroCount { get; set; }
        public int? PositiveOffset { get; set; }
        public ulong[]? PositiveBucketCounts { get; set; }
        public double[]? QuantileQ { get; set; }
        public double[]? QuantileV { get; set; }
    }
}
