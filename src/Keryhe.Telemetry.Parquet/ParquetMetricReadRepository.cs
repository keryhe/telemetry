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
using static Keryhe.Telemetry.Parquet.ParquetRows;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// <see cref="IMetricReadRepository"/> over the Parquet row model (plans/parquet-duckdb phase 5), following
/// <c>ClickHouseMetricReadRepository</c>.
///
/// <b>Catalog.</b> <c>metric_catalog</c> holds, per day, rows per (tenant, service, metric name, type); the read folds every day (min
/// first seen, max last seen). A metric's id is a hash of that key (<see cref="ParquetIds.MetricId"/>), so a catalog "instance" is a
/// service. "Seen in range" is the folded first/last seen overlapping the window (accurate to <c>CatalogRefreshSeconds</c>).
///
/// <b>Series.</b> One points table, grouped by <c>series_id</c> per time bucket in SQL; the per-stream math, merging, top-N and "other"
/// fold are the shared <see cref="MetricSeriesPipeline"/>. A stream's labels come from <c>metric_series</c>. The points view covers the
/// window and a day before it (the baseline a cumulative first bucket is measured against); "latest value" reads cover the last day.
/// </summary>
public sealed class ParquetMetricReadRepository : DapperReadRepository, IMetricReadRepository
{
    private const int MaxLabelSampleRows = 1_000;
    private const long NanosPerHour = 3_600_000_000_000L;
    private const long NanosPerDay = 24 * NanosPerHour;
    private const long BaselineLookbackNanos = NanosPerDay;

    private readonly ParquetQueryEngine _engine;
    private readonly int _summaryTimeoutSeconds;

    public ParquetMetricReadRepository(IConfiguration configuration, ITenantContext tenantContext, ParquetQueryEngine engine) : base(tenantContext)
    {
        _engine = engine;
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured) ? configured : 5;
    }

    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await _engine.OpenAsync([], cancellationToken);

    private ParquetViewScope CatalogScope() => new(ParquetTables.MetricCatalogTable, TenantId, null, null);
    private ParquetViewScope SeriesScope(long from, long to) => new(ParquetTables.MetricSeriesTable, TenantId, from, to);
    private ParquetViewScope PointsScope(MetricType type, long? from, long? to) => new(TableFor(type), TenantId, from, to);

    private static ParquetTable TableFor(MetricType type) => type switch
    {
        MetricType.GAUGE => ParquetTables.GaugeTable,
        MetricType.SUM => ParquetTables.SumTable,
        MetricType.HISTOGRAM => ParquetTables.HistogramTable,
        MetricType.EXPONENTIAL_HISTOGRAM => ParquetTables.ExpHistogramTable,
        _ => ParquetTables.SummaryTable
    };

    private Task<NamedParameterConnection> OpenCatalogAsync(CancellationToken ct) => _engine.OpenAsync([CatalogScope()], ct);

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
        public long Id(long tenant) => ParquetIds.MetricId(tenant, ServiceName, Name, MetricType);
    }

    private const string CatalogSelect = """
        SELECT service_name AS ServiceName, metric_name AS Name, metric_type AS Type,
               any_value(unit) AS Unit, any_value(description) AS Description,
               min(first_seen_unix_nano) AS FirstNano, max(last_seen_unix_nano) AS LastNano
        FROM metric_catalog
        """;

    private async Task<List<CatalogRow>> ReadCatalogAsync(
        DbConnection conn, string where, DynamicParameters parameters, string having, string order, int? limit, CancellationToken ct)
    {
        parameters.Add("tenantId", TenantId);
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

    // =========================================================================
    // METADATA READS
    // =========================================================================

    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenCatalogAsync(cancellationToken);
        var rows = await ReadCatalogAsync(conn, "", new DynamicParameters(), "", NewestFirst, null, cancellationToken);
        // Parity with the relational providers: a found metric maps to an (intentionally empty) MetricModel.
        return rows.Any(r => r.Id(TenantId) == id) ? new MetricModel() : null;
    }

    public async Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        await using var conn = await OpenCatalogAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("name", name);
        var rows = await ReadCatalogAsync(conn, " AND metric_name = @name", p, "", NewestFirst, null, cancellationToken);
        var counts = await PointCountsAsync(conn, rows, cancellationToken);
        return rows.Select(r => ToInfo(r, counts.GetValueOrDefault((r.ServiceName, r.Name, r.Type)))).ToList();
    }

    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenCatalogAsync(cancellationToken);
        var p = new DynamicParameters();
        p.Add("type", type.ToString());
        var rows = await ReadCatalogAsync(conn, " AND metric_type = @type", p, "", NewestFirst, null, cancellationToken);
        var counts = await PointCountsAsync(conn, rows, cancellationToken);
        return rows.Select(r => ToInfo(r, counts.GetValueOrDefault((r.ServiceName, r.Name, r.Type)))).ToList();
    }

    /// <summary>All-time point counts per (service, name, type), one grouped read per points table involved.</summary>
    private async Task<Dictionary<(string, string, string), int>> PointCountsAsync(NamedParameterConnection conn, List<CatalogRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), int>();
        foreach (var group in rows.GroupBy(r => r.MetricType))
        {
            var table = TableFor(group.Key);
            await _engine.DefineAsync(conn, PointsScope(group.Key, null, null), ct);
            var p = new DynamicParameters();
            p.Add("tenantId", TenantId);
            p.Add("names", group.Select(r => r.Name).Distinct().ToArray());
            var counts = await conn.QueryAsync<CountRow>(new CommandDefinition($"""
                SELECT service_name AS ServiceName, metric_name AS Name, count(*) AS Cnt
                FROM {table.Name}
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
        await using var conn = await OpenCatalogAsync(cancellationToken);
        var p = new DynamicParameters();
        var having = startTime.HasValue && endTime.HasValue ? SeenInRange(startTime.Value, endTime.Value, p) : "";
        var rows = await ReadCatalogAsync(conn, "", p, having, NewestFirst, null, cancellationToken);

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
        await using var conn = await OpenCatalogAsync(cancellationToken);
        var rows = await ServiceCatalogAsync(conn, serviceName, cancellationToken);
        return rows.GroupBy(r => r.MetricType).ToDictionary(g => g.Key.ToString(), g => g.Count());
    }

    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenCatalogAsync(cancellationToken);
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

        // "Latest" is the newest value within the last day.
        var now = TimeConversion.DateTimeToUnixNano(DateTime.UtcNow);
        await using var conn = await _engine.OpenAsync(
            [PointsScope(MetricType.GAUGE, now - NanosPerDay, now + NanosPerHour), PointsScope(MetricType.SUM, now - NanosPerDay, now + NanosPerHour)], cancellationToken);
        var result = new Dictionary<string, double>();
        foreach (var table in new[] { "gauge_points", "sum_points" })
        {
            var rows = await conn.QueryAsync<LatestRow>(new CommandDefinition($"""
                SELECT metric_name AS MetricName, arg_max(value, time_unix_nano) AS Value
                FROM {table}
                WHERE tenant_id = @tenantId AND service_name = @service
                GROUP BY metric_name
                """, new { tenantId = TenantId, service = serviceName }, cancellationToken: cancellationToken));
            foreach (var r in rows) result[r.MetricName] = r.Value;
        }
        return result;
    }

    // =========================================================================
    // CATALOG PAGE
    // =========================================================================

    /// <summary>"Seen in range": the metric's folded <c>first_seen</c>/<c>last_seen</c> overlap the window.</summary>
    private static string SeenInRange(DateTime start, DateTime end, DynamicParameters parameters)
    {
        parameters.Add("seenStart", TimeConversion.DateTimeToUnixNano(start));
        parameters.Add("seenEnd", TimeConversion.DateTimeToUnixNano(end));
        return "max(last_seen_unix_nano) >= @seenStart AND min(first_seen_unix_nano) <= @seenEnd";
    }

    public async Task<MetricCatalogPage> GetMetricCatalogPageAsync(MetricCatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Start >= query.End)
            throw new ArgumentException("Start time must be before end time");

        var limit = Math.Max(1, query.Limit);
        var p = new DynamicParameters();
        var having = SeenInRange(query.Start, query.End, p);
        var where = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(query.Q)) { where.Append(" AND contains(lower(metric_name), @nameSearch)"); p.Add("nameSearch", query.Q.ToLowerInvariant()); }
        if (!string.IsNullOrWhiteSpace(query.Service)) { where.Append(" AND service_name = @service"); p.Add("service", query.Service); }
        if (query.Type.HasValue) { where.Append(" AND metric_type = @metricType"); p.Add("metricType", query.Type.Value.ToString()); }

        await using var conn = await OpenCatalogAsync(cancellationToken);
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

        await using var conn = await OpenCatalogAsync(cancellationToken);
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

        await using var retryConn = await _engine.OpenAsync([], cancellationToken);
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
        NamedParameterConnection conn, MetricType type, string[] services, MetricSeriesQuery query, int points, int top, int timeoutSeconds, CancellationToken ct)
    {
        var b = ComputeBucketing(query.Start, query.End, points);
        await _engine.DefineAsync(conn, PointsScope(type, b.StartNano - BaselineLookbackNanos, b.EndNano), ct);
        var scope = new Scope(this, type, services, query.MetricName, query.LabelFilters, b, timeoutSeconds);

        var streams = type switch
        {
            MetricType.GAUGE => await LoadGaugeAsync(conn, scope, ct),
            MetricType.SUM => await LoadSumAsync(conn, scope, ct),
            MetricType.HISTOGRAM => await LoadDistributionAsync(conn, scope, exponential: false, ct),
            MetricType.EXPONENTIAL_HISTOGRAM => await LoadDistributionAsync(conn, scope, exponential: true, ct),
            _ => await LoadSummaryAsync(conn, scope, ct)
        };

        // labels and service per stream
        await _engine.DefineAsync(conn, SeriesScope(b.StartNano - NanosPerHour, b.EndNano + NanosPerDay), ct);
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

    /// <summary>Labels and service of the returned series, from <c>metric_series</c> (one row per series after folding; the newest partial wins).</summary>
    private async Task<Dictionary<ulong, (string Service, Dictionary<string, string> Attributes)>> SeriesInfoAsync(
        DbConnection conn, string[] services, string metricName, ulong[] ids, CancellationToken ct)
    {
        var result = new Dictionary<ulong, (string, Dictionary<string, string>)>();
        if (ids.Length == 0) return result;
        var rows = await conn.QueryAsync<SeriesInfoRow>(new CommandDefinition("""
            SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, to_json(arg_max(attributes, last_seen_unix_nano)) AS SeriesAttributes
            FROM metric_series
            WHERE tenant_id = @tenantId AND metric_name = @name AND service_name IN @services AND series_id IN @ids
            GROUP BY series_id
            """, new { tenantId = TenantId, name = metricName, services, ids }, cancellationToken: ct));
        foreach (var r in rows) result[r.SeriesId] = (r.ServiceName, ToStrings(r.SeriesAttributes));
        return result;
    }

    /// <summary>One series request's shared SQL fragments and parameters.</summary>
    private sealed class Scope
    {
        public readonly string Where;
        public readonly string Bucket;
        public readonly string Table;
        public readonly string Window = " AND time_unix_nano >= @start AND time_unix_nano < @end";
        public readonly string Before = " AND time_unix_nano < @start AND time_unix_nano >= @lookbackStart";
        public readonly DynamicParameters Parameters = new();
        public readonly Bucketing B;
        public readonly int TimeoutSeconds;
        public readonly string[] Services;
        private readonly Dictionary<long, string> _serviceBySeries = new();

        public Scope(ParquetMetricReadRepository repo, MetricType type, string[] services, string name, Dictionary<string, string>? labelFilters, Bucketing b, int timeoutSeconds)
        {
            B = b; TimeoutSeconds = timeoutSeconds; Services = services; Table = TableFor(type).Name;
            Parameters.Add("tenantId", repo.TenantId);
            Parameters.Add("name", name);
            Parameters.Add("services", services);
            Parameters.Add("start", b.StartNano);
            Parameters.Add("end", b.EndNano);
            Parameters.Add("lookbackStart", b.StartNano - BaselineLookbackNanos);
            Parameters.Add("bucketNanos", b.BucketNanos);
            Parameters.Add("pointsMinus1", b.Points - 1);

            var where = new StringBuilder("tenant_id = @tenantId AND metric_name = @name AND service_name IN @services");
            var i = 0;
            foreach (var (k, v) in labelFilters ?? [])
            {
                Parameters.Add($"lblKey{i}", k);
                Parameters.Add($"lblVal{i}", v.ToLowerInvariant());
                where.Append($" AND lower(element_at(attributes, @lblKey{i})[1]) = @lblVal{i}");
                i++;
            }
            Where = where.ToString();
            Bucket = "least(CAST(@pointsMinus1 AS INTEGER), greatest(0, CAST(((time_unix_nano - @start) // @bucketNanos) AS INTEGER)))";
        }

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
        ValueDouble = r.Value, Count = r.Cnt, SumValue = r.SumValue, MinValue = r.MinValue, MaxValue = r.MaxValue,
        BucketCounts = r.BucketCountsJson, ExplicitBounds = r.ExplicitBoundsJson,
        Scale = r.Scale, ZeroCount = r.ZeroCount, PositiveOffset = r.PositiveOffset, PositiveBucketCounts = r.PositiveCountsJson,
        QuantileValues = QuantileValuesJson(r.QuantilesJson)
    };

    private static string? QuantileValuesJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        return JsonSerializer.Serialize(doc.RootElement.EnumerateArray()
            .Select(e => new QuantileValueModel { Quantile = e.GetProperty("quantile").GetDouble(), Value = e.GetProperty("value").GetDouble() }).ToList());
    }

    private async Task<AggregationTemporality> PeekTemporalityAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var value = await conn.QuerySingleOrDefaultAsync<string?>(scope.Cmd(
            $"SELECT temporality FROM {scope.Table} WHERE {scope.Where}{scope.Window} ORDER BY time_unix_nano DESC LIMIT 1", ct));
        return value != null && Enum.TryParse<AggregationTemporality>(value, out var t) ? t : AggregationTemporality.CUMULATIVE;
    }

    // ---- gauge ----

    private async Task<Dictionary<string, StreamAgg>> LoadGaugeAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                   avg(value) AS AvgValue, min(value) AS MinValue, max(value) AS MaxValue
            FROM gauge_points
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, {scope.Bucket}
            """, ct));
        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in rows)
            Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg { Avg = r.AvgValue, Min = r.MinValue, Max = r.MaxValue };
        return streams;
    }

    // ---- sum ----

    private const string CoveredNanos =
        "CAST(sum(CASE WHEN start_time_unix_nano > 0 AND start_time_unix_nano < time_unix_nano THEN time_unix_nano - start_time_unix_nano ELSE 0 END) AS DOUBLE)";

    private async Task<Dictionary<string, StreamAgg>> LoadSumAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var temporality = await PeekTemporalityAsync(conn, scope, ct);
        if (temporality == AggregationTemporality.DELTA)
        {
            var rows = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
                SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                       sum(value) AS SumOfValue, {CoveredNanos} AS CoveredNanos
                FROM sum_points
                WHERE {scope.Where}{scope.Window}
                GROUP BY series_id, {scope.Bucket}
                """, ct));
            var streams = new Dictionary<string, StreamAgg>();
            foreach (var r in rows)
            {
                var value = r.SumOfValue ?? 0;
                Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg { Value = value, Rate = DeltaRate(value, r.CoveredNanos, scope.B) };
            }
            return streams;
        }

        var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, "arg_max(value, time_unix_nano) AS Value", ct);
        return ComputeCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano);
    }

    /// <summary>
    /// The cumulative types' two reads: the last point per (series, bucket) in the window, and each series' last point before it
    /// (the baseline the first bucket's increase is measured against, looked for in the day before the window).
    /// </summary>
    private async Task<(List<MetricPointRow> Buckets, List<MetricPointRow> Baseline)> LastPerBucketAndBaselineAsync(
        DbConnection conn, Scope scope, string valueColumns, CancellationToken ct)
    {
        var last = $"max(time_unix_nano) AS Tn, arg_max(start_time_unix_nano, time_unix_nano) AS StartNano, {valueColumns}";
        var buckets = (await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, {scope.Bucket} AS Bucket, {last}
            FROM {scope.Table}
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, {scope.Bucket}
            """, ct))).ToList();
        foreach (var r in buckets) scope.Remember((long)r.SeriesId, r.ServiceName ?? "");

        var baseline = (await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, 0 AS Bucket, {last}
            FROM {scope.Table}
            WHERE {scope.Where}{scope.Before}
            GROUP BY series_id
            """, ct))).ToList();
        return (buckets.Select(ToPoint).ToList(), baseline.Select(ToPoint).ToList());
    }

    // ---- histogram / exponential histogram ----

    private async Task<Dictionary<string, StreamAgg>> LoadDistributionAsync(DbConnection conn, Scope scope, bool exponential, CancellationToken ct)
    {
        var table = scope.Table;
        var temporality = await PeekTemporalityAsync(conn, scope, ct);

        if (temporality != AggregationTemporality.DELTA)
        {
            var common = "CAST(arg_max(\"count\", time_unix_nano) AS BIGINT) AS Cnt, arg_max(\"sum\", time_unix_nano) AS SumValue, arg_max(\"min\", time_unix_nano) AS MinValue, arg_max(\"max\", time_unix_nano) AS MaxValue";
            var columns = exponential
                ? $"{common}, arg_max(scale, time_unix_nano) AS Scale, CAST(arg_max(zero_count, time_unix_nano) AS BIGINT) AS ZeroCount, arg_max(positive_offset, time_unix_nano) AS PositiveOffset, to_json(arg_max(positive_bucket_counts, time_unix_nano)) AS PositiveCountsJson"
                : $"{common}, to_json(arg_max(bucket_counts, time_unix_nano)) AS BucketCountsJson, to_json(arg_max(explicit_bounds, time_unix_nano)) AS ExplicitBoundsJson";
            var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, columns, ct);
            return exponential
                ? ComputeExpHistogramCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano)
                : ComputeHistogramCumulativeDeltas(bucketRows, baselineRows, scope.B.StartNano);
        }

        // delta: count, sum, min and max per (series, bucket) ...
        var aggregates = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, any_value(service_name) AS ServiceName, {scope.Bucket} AS Bucket,
                   CAST(sum("count") AS BIGINT) AS Cnt, sum("sum") AS SumValue, min("min") AS MinValue, max("max") AS MaxValue, {CoveredNanos} AS CoveredNanos
            FROM {table}
            WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, {scope.Bucket}
            """, ct));
        var streams = new Dictionary<string, StreamAgg>();
        foreach (var r in aggregates)
        {
            var count = r.Cnt ?? 0;
            Stream(streams, scope, r).Buckets[r.Bucket] = new BucketAgg
            {
                Count = count, Sum = r.SumValue, Min = r.MinValue, Max = r.MaxValue, Rate = DeltaRate(count, r.CoveredNanos, scope.B)
            };
        }

        // ... and the bucket-count arrays, summed element by element by the database
        if (!exponential)
        {
            var bounds = await conn.QueryAsync<SeriesRow>(scope.Cmd($"""
                SELECT series_id AS SeriesId, to_json(any_value(explicit_bounds)) AS ExplicitBoundsJson
                FROM {table} WHERE {scope.Where}{scope.Window} GROUP BY series_id
                """, ct));
            foreach (var r in bounds) GetOrAddStream(streams, (long)r.SeriesId, "").Bounds ??= ToDoubles(r.ExplicitBoundsJson);

            var elements = await conn.QueryAsync<ElementRow>(scope.Cmd($"""
                SELECT series_id AS SeriesId, {scope.Bucket} AS Bucket, idx AS Idx, CAST(sum(c) AS BIGINT) AS C
                FROM (SELECT series_id, time_unix_nano, unnest(bucket_counts) AS c, unnest(range(len(bucket_counts))) AS idx
                      FROM {table} WHERE {scope.Where}{scope.Window})
                GROUP BY series_id, {scope.Bucket}, idx
                """, ct));
            foreach (var group in elements.GroupBy(e => (e.SeriesId, e.Bucket)))
            {
                var stream = GetOrAddStream(streams, (long)group.Key.SeriesId, "");
                if (stream.Bounds == null || !stream.Buckets.TryGetValue(group.Key.Bucket, out var bucket)) continue;
                var counts = new long[group.Max(e => e.Idx) + 1];
                foreach (var e in group) counts[e.Idx] = e.C;
                if (counts.Length == stream.Bounds.Length + 1) bucket.Counts = counts;
            }
            return streams;
        }

        var targetScale = await conn.ExecuteScalarAsync<int?>(scope.Cmd(
            $"SELECT min(scale) FROM {table} WHERE {scope.Where}{scope.Window}", ct)) ?? 0;

        var zeroes = (await conn.QueryAsync<SparseKeyRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, {scope.Bucket} AS Bucket, scale AS Scale, positive_offset AS PositiveOffset, CAST(sum(zero_count) AS BIGINT) AS ZeroCount
            FROM {table} WHERE {scope.Where}{scope.Window}
            GROUP BY series_id, {scope.Bucket}, scale, positive_offset
            """, ct))).ToList();
        var sparseElements = (await conn.QueryAsync<SparseElementRow>(scope.Cmd($"""
            SELECT series_id AS SeriesId, {scope.Bucket} AS Bucket, scale AS Scale, positive_offset AS PositiveOffset, idx AS Idx, CAST(sum(c) AS BIGINT) AS C
            FROM (SELECT series_id, time_unix_nano, scale, positive_offset, unnest(positive_bucket_counts) AS c, unnest(range(len(positive_bucket_counts))) AS idx
                  FROM {table} WHERE {scope.Where}{scope.Window})
            GROUP BY series_id, {scope.Bucket}, scale, positive_offset, idx
            """, ct))).ToList();

        var countsByKey = sparseElements.GroupBy(e => (e.SeriesId, e.Bucket, e.Scale, e.PositiveOffset)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var z in zeroes)
        {
            var stream = GetOrAddStream(streams, (long)z.SeriesId, "");
            if (!stream.Buckets.TryGetValue(z.Bucket, out var bucket)) stream.Buckets[z.Bucket] = bucket = new BucketAgg { Count = 0 };
            bucket.SparseCounts ??= new Dictionary<long, long>();

            var positive = new long[0];
            if (countsByKey.TryGetValue((z.SeriesId, z.Bucket, z.Scale, z.PositiveOffset), out var elements))
            {
                positive = new long[elements.Max(e => e.Idx) + 1];
                foreach (var e in elements) positive[e.Idx] = e.C;
            }
            var row = new MetricPointRow
            {
                MetricId = (long)z.SeriesId, Bucket = z.Bucket, Scale = z.Scale, PositiveOffset = z.PositiveOffset,
                PositiveBucketCounts = JsonSerializer.Serialize(positive), ZeroCount = z.ZeroCount
            };
            foreach (var kv in DownscaleRow(row, targetScale))
                bucket.SparseCounts[kv.Key] = bucket.SparseCounts.GetValueOrDefault(kv.Key) + kv.Value;
            bucket.ZeroCountValue = (bucket.ZeroCountValue ?? 0) + z.ZeroCount;
        }
        return streams;
    }

    // ---- summary ----

    private async Task<Dictionary<string, StreamAgg>> LoadSummaryAsync(DbConnection conn, Scope scope, CancellationToken ct)
    {
        var columns = "CAST(arg_max(\"count\", time_unix_nano) AS BIGINT) AS Cnt, arg_max(\"sum\", time_unix_nano) AS SumValue, to_json(arg_max(quantiles, time_unix_nano)) AS QuantilesJson";
        var (bucketRows, baselineRows) = await LastPerBucketAndBaselineAsync(conn, scope, columns, ct);
        return ComputeSummaryDeltas(bucketRows, ToDictionaryNewest(baselineRows, r => $"{r.MetricId}\u0001{r.AttributesJson}", r => r.TimeUnixNano), scope.B.StartNano);
    }

    // =========================================================================
    // EXEMPLARS
    // =========================================================================

    public async Task<MetricExemplarPage?> GetMetricExemplarsAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(query.MetricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(query));

        await using var conn = await OpenCatalogAsync(cancellationToken);
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
        var table = TableFor(type).Name;
        var valueColumns = type is MetricType.GAUGE or MetricType.SUM ? "value AS PointValue, 0 AS PointCount" : "0 AS PointValue, CAST(\"count\" AS BIGINT) AS PointCount";

        var start = TimeConversion.DateTimeToUnixNano(query.Start);
        var end = TimeConversion.DateTimeToUnixNano(query.End);
        await _engine.DefineAsync(conn, PointsScope(type, start, end), cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("name", query.MetricName);
        parameters.Add("services", services);
        parameters.Add("start", start);
        parameters.Add("end", end);
        var where = new StringBuilder("tenant_id = @tenantId AND metric_name = @name AND service_name IN @services AND len(exemplars) > 0 AND time_unix_nano >= @start AND time_unix_nano <= @end");
        var i = 0;
        foreach (var (k, v) in query.LabelFilters ?? [])
        {
            parameters.Add($"lblKey{i}", k);
            parameters.Add($"lblVal{i}", v.ToLowerInvariant());
            where.Append($" AND lower(element_at(attributes, @lblKey{i})[1]) = @lblVal{i}");
            i++;
        }

        // newest cap + 1 exemplars: the extra one only says that more exist
        var found = (await conn.QueryAsync<ExemplarRow>(new CommandDefinition($"""
            SELECT service_name AS ServiceName, to_json(attributes) AS PointAttributes, time_unix_nano AS PointNano, {valueColumns},
                   ex.time_unix_nano AS ExNano, ex.value AS ExValue, ex.trace_hi AS ExHi, ex.trace_lo AS ExLo, ex.span_id AS ExSpan,
                   to_json(ex.filtered_attributes) AS ExFiltered
            FROM (SELECT *, unnest(exemplars) AS ex FROM {table} WHERE {where})
            ORDER BY ex.time_unix_nano DESC
            LIMIT {cap + 1}
            """, parameters, cancellationToken: cancellationToken))).ToList();

        page.Truncated = found.Count > cap;
        page.Exemplars = found.Take(cap).Select(r =>
        {
            var labels = ToStrings(r.PointAttributes);
            var filtered = ToObjects(r.ExFiltered);
            return new MetricExemplar
            {
                Exemplar = new ExemplarModel
                {
                    TimeUnixNano = r.ExNano, ValueDouble = r.ExValue,
                    TraceIdHex = r.ExHi == 0 && r.ExLo == 0 ? null : ParquetIds.TraceIdHex(r.ExHi, r.ExLo),
                    SpanIdHex = r.ExSpan == 0 ? null : ParquetIds.SpanIdHex(r.ExSpan),
                    FilteredAttributes = filtered.Count > 0 ? filtered : null
                },
                SeriesName = BuildSeriesDisplayName(r.ServiceName, labels),
                ServiceName = string.IsNullOrEmpty(r.ServiceName) ? "unknown" : r.ServiceName,
                Labels = new Dictionary<string, string>(labels),
                PointTimestamp = TimeConversion.UnixNanoToDateTime(r.PointNano),
                PointCount = type is MetricType.HISTOGRAM or MetricType.EXPONENTIAL_HISTOGRAM ? r.PointCount : null,
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

        var start = TimeConversion.DateTimeToUnixNano(startTime ?? DateTime.UtcNow.AddHours(-24));
        var end = TimeConversion.DateTimeToUnixNano(endTime ?? DateTime.UtcNow);
        var p = new DynamicParameters();
        p.Add("tenantId", TenantId);
        p.Add("name", metricName);
        p.Add("start", start);
        p.Add("end", end);

        // One row per series that reported in the window (a series row is refreshed every CatalogRefreshSeconds while it reports).
        await using var conn = await _engine.OpenAsync([SeriesScope(start - NanosPerHour, end + NanosPerDay)], cancellationToken);
        var sets = (await conn.QueryAsync<AttributesRow>(new CommandDefinition($"""
            SELECT to_json(arg_max(attributes, last_seen_unix_nano)) AS SeriesAttributes
            FROM metric_series
            WHERE tenant_id = @tenantId AND metric_name = @name
            GROUP BY service_name, series_id
            HAVING max(last_seen_unix_nano) >= @start AND min(first_seen_unix_nano) <= @end
            LIMIT {MaxLabelSampleRows}
            """, p, cancellationToken: cancellationToken))).ToList();

        var labels = new Dictionary<string, HashSet<string>>();
        foreach (var set in sets)
            foreach (var (key, value) in ToStrings(set.SeriesAttributes))
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
        public long Cnt { get; set; }
    }

    private sealed class LatestRow
    {
        public string MetricName { get; set; } = "";
        public double Value { get; set; }
    }

    private sealed class AttributesRow
    {
        public string? SeriesAttributes { get; set; }
    }

    private sealed class SeriesInfoRow
    {
        public ulong SeriesId { get; set; }
        public string ServiceName { get; set; } = "";
        public string? SeriesAttributes { get; set; }
    }

    private sealed class ElementRow
    {
        public ulong SeriesId { get; set; }
        public int Bucket { get; set; }
        public int Idx { get; set; }
        public long C { get; set; }
    }

    private sealed class SparseKeyRow
    {
        public ulong SeriesId { get; set; }
        public int Bucket { get; set; }
        public int Scale { get; set; }
        public int PositiveOffset { get; set; }
        public long ZeroCount { get; set; }
    }

    private sealed class SparseElementRow
    {
        public ulong SeriesId { get; set; }
        public int Bucket { get; set; }
        public int Scale { get; set; }
        public int PositiveOffset { get; set; }
        public int Idx { get; set; }
        public long C { get; set; }
    }

    private sealed class ExemplarRow
    {
        public string ServiceName { get; set; } = "";
        public string? PointAttributes { get; set; }
        public long PointNano { get; set; }
        public double PointValue { get; set; }
        public long PointCount { get; set; }
        public long ExNano { get; set; }
        public double ExValue { get; set; }
        public ulong ExHi { get; set; }
        public ulong ExLo { get; set; }
        public ulong ExSpan { get; set; }
        public string? ExFiltered { get; set; }
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
        public long? Cnt { get; set; }
        public double? SumValue { get; set; }
        public string? BucketCountsJson { get; set; }
        public string? ExplicitBoundsJson { get; set; }
        public int? Scale { get; set; }
        public long? ZeroCount { get; set; }
        public int? PositiveOffset { get; set; }
        public string? PositiveCountsJson { get; set; }
        public string? QuantilesJson { get; set; }
    }
}
