using System.Data.Common;
using System.Text.Json;
using Dapper;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data.Read;

/// <summary>
/// Dapper implementation of <see cref="IMetricReadRepository"/>. Mirrors the former EF
/// repository: row-level predicates (tenant, name, type, time, metric ids) are pushed into
/// SQL, while service-name extraction, label filtering, latest-value selection and the
/// various groupings are performed in memory for output parity.
/// </summary>
public abstract class MetricReadRepositoryBase : DapperReadRepository, IMetricReadRepository
{
    protected MetricReadRepositoryBase(ITenantContext tenantContext) : base(tenantContext) { }

    private const string MetricSelect = """
        SELECT m.id AS Id, m.name AS Name, m.description AS Description, m.unit AS Unit,
               m.type AS Type, m.created_at AS CreatedAt, r.attributes_json AS ResourceAttributesJson
        FROM metrics m JOIN resources r ON m.resource_id = r.id
        WHERE r.tenant_id = @tenantId
        """;

    // =========================================================================
    // METRIC METADATA READS
    // =========================================================================

    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var exists = await conn.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "SELECT m.id FROM metrics m JOIN resources r ON m.resource_id = r.id WHERE m.id = @id AND r.tenant_id = @tenantId",
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
        return rows.Select(m => ToMetricInfo(m, ExtractServiceName(DeserializeAttributes(m.ResourceAttributesJson)) ?? "", stats.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            MetricSelect + " AND m.type = @type", new { tenantId = TenantId, type = type.ToString() }, cancellationToken: cancellationToken))).ToList();

        var stats = await GetMetricStatsAsync(conn, rows, cancellationToken);
        return rows.Select(m => ToMetricInfo(m, ExtractServiceName(DeserializeAttributes(m.ResourceAttributesJson)) ?? "", stats.GetValueOrDefault(m.Id))).ToList();
    }

    public async Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        var metricIdsWithData = await GetMetricIdsWithDataInRangeAsync(conn, startTime, endTime, cancellationToken);
        if (metricIdsWithData is { Count: 0 })
            return new List<MetricInfo>();

        var sql = MetricSelect;
        if (metricIdsWithData != null) sql += $" AND m.id IN ({IdInList(metricIdsWithData)})";
        sql += " ORDER BY m.created_at DESC";

        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(sql,
            new { tenantId = TenantId }, cancellationToken: cancellationToken)))
            .Take(limit)
            .ToList();

        var stats = await GetMetricStatsAsync(conn, rows, cancellationToken);
        return rows
            .Select(m => ToMetricInfo(m, ExtractServiceName(DeserializeAttributes(m.ResourceAttributesJson)), stats.GetValueOrDefault(m.Id)))
            .ToList();
    }

    // =========================================================================
    // TIME SERIES READS
    // =========================================================================

    public async Task<MetricSeries?> GetMetricSeriesAsync(string metricName, Dictionary<string, string>? labelFilters = null,
        DateTime? startTime = null, DateTime? endTime = null, long? metricId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        await using var conn = await OpenConnectionAsync(cancellationToken);

        List<MetricRow> metrics;
        if (metricId.HasValue)
        {
            metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
                MetricSelect + " AND m.name = @name AND m.id = @metricId",
                new { tenantId = TenantId, name = metricName, metricId = metricId.Value }, cancellationToken: cancellationToken))).ToList();
        }
        else
        {
            metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
                MetricSelect + " AND m.name = @name ORDER BY m.created_at DESC",
                new { tenantId = TenantId, name = metricName }, cancellationToken: cancellationToken))).ToList();
        }

        if (metrics.Count == 0) return null;

        var primaryType = Enum.Parse<MetricType>(metrics[0].Type);
        var metricIds = metrics.Select(m => m.Id).ToList();

        var series = new MetricSeries { Name = metricName, Type = primaryType };
        series.Points = await GetDataPointsAsync(conn, primaryType, metricIds, labelFilters, startTime, endTime, cancellationToken);
        return series;
    }

    public async Task<MultiSeriesMetricData?> GetGroupedMetricSeriesAsync(string metricName,
        DateTime? startTime = null, DateTime? endTime = null, long? metricId = null,
        Dictionary<string, string>? labelFilters = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        await using var conn = await OpenConnectionAsync(cancellationToken);

        var sql = MetricSelect + " AND m.name = @name";
        if (metricId.HasValue) sql += " AND m.id = @metricId";
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            sql, new { tenantId = TenantId, name = metricName, metricId }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0) return null;

        var result = new MultiSeriesMetricData { Name = metricName, Type = Enum.Parse<MetricType>(metrics[0].Type) };

        // Group every data point (across all metric rows sharing this name) by its series
        // identity: originating service.name + the point's own attribute (label) set.
        var groups = new Dictionary<string, NamedMetricSeries>();
        foreach (var metric in metrics)
        {
            var serviceName = ExtractServiceName(DeserializeAttributes(metric.ResourceAttributesJson)) ?? "unknown";
            var type = Enum.Parse<MetricType>(metric.Type);
            var points = await GetDataPointsAsync(conn, type, new List<long> { metric.Id }, labelFilters, startTime, endTime, cancellationToken);

            foreach (var point in points)
            {
                var labels = ToLabelDictionary(point.Attributes);
                var key = SeriesKey(serviceName, labels);
                if (!groups.TryGetValue(key, out var series))
                {
                    series = new NamedMetricSeries
                    {
                        SeriesName = BuildSeriesDisplayName(serviceName, labels),
                        MetricId = metric.Id,
                        ServiceName = serviceName,
                        Labels = labels
                    };
                    groups[key] = series;
                }
                series.Points.Add(point);
            }
        }

        foreach (var series in groups.Values)
            series.Points = series.Points.OrderBy(p => p.Timestamp).ToList();

        result.Series = groups.Values.OrderBy(s => s.SeriesName, StringComparer.Ordinal).ToList();
        return result;
    }

    public async Task<List<MetricSeries>> GetMultipleMetricSeriesAsync(List<string> metricNames,
        Dictionary<string, string>? labelFilters = null, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        if (metricNames == null || metricNames.Count == 0)
            throw new ArgumentException("Metric names list cannot be null or empty", nameof(metricNames));

        var seriesList = new List<MetricSeries>();
        foreach (var metricName in metricNames)
        {
            var series = await GetMetricSeriesAsync(metricName, labelFilters, startTime, endTime, null, cancellationToken);
            if (series != null) seriesList.Add(series);
        }
        return seriesList;
    }

    // =========================================================================
    // AGGREGATION / ANALYSIS READS
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
                       COALESCE(dp.value_double, dp.value_int, 0) AS Value,
                       r.attributes_json AS ResourceAttributesJson
                FROM {table} dp
                JOIN metrics m   ON dp.metric_id = m.id
                JOIN resources r ON m.resource_id = r.id
                WHERE r.tenant_id = @tenantId
                """, new { tenantId = TenantId }, cancellationToken: cancellationToken));

            var values = rows
                .Where(x =>
                {
                    var attrs = DeserializeAttributes(x.ResourceAttributesJson);
                    return attrs != null && attrs.ContainsKey("service.name") &&
                           attrs["service.name"]?.ToString() == serviceName;
                })
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
        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(MetricSelect, new { tenantId = TenantId }, cancellationToken: cancellationToken))).ToList();

        IEnumerable<MetricRow> filtered = rows;
        if (!string.IsNullOrEmpty(serviceName))
        {
            filtered = rows.Where(m =>
            {
                var attrs = DeserializeAttributes(m.ResourceAttributesJson);
                return attrs != null && attrs.ContainsKey("service.name") && attrs["service.name"]?.ToString() == serviceName;
            });
        }

        return filtered
            .GroupBy(m => Enum.Parse<MetricType>(m.Type))
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
    }

    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = (await conn.QueryAsync<MetricRow>(new CommandDefinition(MetricSelect, new { tenantId = TenantId }, cancellationToken: cancellationToken))).ToList();

        IEnumerable<MetricRow> filtered = rows;
        if (!string.IsNullOrEmpty(serviceName))
        {
            filtered = rows.Where(m =>
            {
                var attrs = DeserializeAttributes(m.ResourceAttributesJson);
                return attrs != null && attrs.ContainsKey("service.name") && attrs["service.name"]?.ToString() == serviceName;
            });
        }

        return filtered.Select(m => m.Name).Distinct().OrderBy(name => name).ToList();
    }

    public async Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        await using var conn = await OpenConnectionAsync(cancellationToken);
        // A metric name is not unique: the same name is emitted by multiple services/resources,
        // so this returns many rows. Aggregate labels across all of them (they share a type).
        var metrics = (await conn.QueryAsync<MetricRow>(new CommandDefinition(
            MetricSelect + " AND m.name = @name", new { tenantId = TenantId, name = metricName }, cancellationToken: cancellationToken))).ToList();

        if (metrics.Count == 0)
            return new Dictionary<string, List<string>>();

        var table = TableFor(Enum.Parse<MetricType>(metrics[0].Type));
        var jsonValues = await conn.QueryAsync<string?>(new CommandDefinition(
            $"SELECT attributes_json FROM {table} WHERE metric_id IN ({IdInList(metrics.Select(m => m.Id))}) AND attributes_json IS NOT NULL",
            cancellationToken: cancellationToken));

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

        return labelDictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OrderBy(v => v).ToList());
    }

    public async Task<List<string>> GetDistinctServicesAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            """
            SELECT DISTINCT r.attributes_json
            FROM metrics m
            JOIN resources r ON m.resource_id = r.id
            WHERE r.tenant_id = @tenantId
            """,
            new { tenantId = TenantId }, cancellationToken: cancellationToken));

        return rows
            .Select(json => ExtractServiceName(DeserializeAttributes(json)))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    // =========================================================================
    // DATA POINT FETCH
    // =========================================================================

    private async Task<List<MetricDataPoint>> GetDataPointsAsync(DbConnection conn, MetricType type, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        return type switch
        {
            MetricType.GAUGE => await GetGaugeDataPointsAsync(conn, metricIds, labelFilters, startTime, endTime, ct),
            MetricType.SUM => await GetSumDataPointsAsync(conn, metricIds, labelFilters, startTime, endTime, ct),
            MetricType.HISTOGRAM => await GetHistogramDataPointsAsync(conn, metricIds, labelFilters, startTime, endTime, ct),
            MetricType.EXPONENTIAL_HISTOGRAM => await GetExponentialHistogramDataPointsAsync(conn, metricIds, labelFilters, startTime, endTime, ct),
            MetricType.SUMMARY => await GetSummaryDataPointsAsync(conn, metricIds, labelFilters, startTime, endTime, ct),
            _ => new List<MetricDataPoint>()
        };
    }

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

    private async Task<List<MetricDataPoint>> GetGaugeDataPointsAsync(DbConnection conn, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var rows = await conn.QueryAsync<GaugeRow>(new CommandDefinition($"""
            SELECT dp.start_time_unix_nano AS StartTimeUnixNano, dp.time_unix_nano AS TimeUnixNano,
                   dp.value_double AS ValueDouble, dp.value_int AS ValueInt, dp.flags AS Flags, dp.attributes_json AS AttributesJson,
                   dp.exemplar_id AS ExemplarId, {ExemplarColumns}
            FROM gauge_data_points dp LEFT JOIN exemplars e ON dp.exemplar_id = e.id
            WHERE dp.metric_id IN ({IdInList(metricIds)}){timeClause}
            ORDER BY dp.time_unix_nano
            """, tp, cancellationToken: ct));

        var points = rows.Select(r => new MetricDataPoint
        {
            StartTimestamp = r.StartTimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(r.StartTimeUnixNano.Value) : null,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            DoubleValue = r.ValueDouble,
            IntValue = r.ValueInt,
            Flags = r.Flags,
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = BuildExemplarList(r)
        }).ToList();

        return FilterByLabelFilters(points, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetSumDataPointsAsync(DbConnection conn, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var rows = await conn.QueryAsync<SumRow>(new CommandDefinition($"""
            SELECT dp.start_time_unix_nano AS StartTimeUnixNano, dp.time_unix_nano AS TimeUnixNano,
                   dp.value_double AS ValueDouble, dp.value_int AS ValueInt,
                   dp.aggregation_temporality AS AggregationTemporality, dp.is_monotonic AS IsMonotonic,
                   dp.flags AS Flags, dp.attributes_json AS AttributesJson,
                   dp.exemplar_id AS ExemplarId, {ExemplarColumns}
            FROM sum_data_points dp LEFT JOIN exemplars e ON dp.exemplar_id = e.id
            WHERE dp.metric_id IN ({IdInList(metricIds)}){timeClause}
            ORDER BY dp.time_unix_nano
            """, tp, cancellationToken: ct));

        var points = rows.Select(r => new MetricDataPoint
        {
            StartTimestamp = r.StartTimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(r.StartTimeUnixNano.Value) : null,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            DoubleValue = r.ValueDouble,
            IntValue = r.ValueInt,
            AggregationTemporality = Enum.Parse<AggregationTemporality>(r.AggregationTemporality),
            IsMonotonic = r.IsMonotonic,
            Flags = r.Flags,
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = BuildExemplarList(r)
        }).ToList();

        return FilterByLabelFilters(points, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetHistogramDataPointsAsync(DbConnection conn, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var rows = await conn.QueryAsync<HistogramRow>(new CommandDefinition($"""
            SELECT dp.start_time_unix_nano AS StartTimeUnixNano, dp.time_unix_nano AS TimeUnixNano,
                   dp.count AS Count, dp.sum_value AS SumValue, dp.min_value AS MinValue, dp.max_value AS MaxValue,
                   dp.aggregation_temporality AS AggregationTemporality, dp.flags AS Flags,
                   dp.bucket_counts AS BucketCounts, dp.explicit_bounds AS ExplicitBounds, dp.attributes_json AS AttributesJson,
                   dp.exemplar_id AS ExemplarId, {ExemplarColumns}
            FROM histogram_data_points dp LEFT JOIN exemplars e ON dp.exemplar_id = e.id
            WHERE dp.metric_id IN ({IdInList(metricIds)}){timeClause}
            ORDER BY dp.time_unix_nano
            """, tp, cancellationToken: ct));

        var points = rows.Select(r => new MetricDataPoint
        {
            StartTimestamp = r.StartTimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(r.StartTimeUnixNano.Value) : null,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            Count = r.Count,
            Sum = r.SumValue,
            Min = r.MinValue,
            Max = r.MaxValue,
            AggregationTemporality = Enum.Parse<AggregationTemporality>(r.AggregationTemporality),
            Flags = r.Flags,
            BucketCounts = DeserializeArray<long>(r.BucketCounts)?.ToList() ?? new List<long>(),
            BucketBounds = DeserializeArray<double>(r.ExplicitBounds)?.ToList() ?? new List<double>(),
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = BuildExemplarList(r)
        }).ToList();

        return FilterByLabelFilters(points, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetExponentialHistogramDataPointsAsync(DbConnection conn, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var rows = await conn.QueryAsync<ExpHistogramRow>(new CommandDefinition($"""
            SELECT dp.start_time_unix_nano AS StartTimeUnixNano, dp.time_unix_nano AS TimeUnixNano,
                   dp.count AS Count, dp.sum_value AS SumValue, dp.min_value AS MinValue, dp.max_value AS MaxValue,
                   dp.scale AS Scale, dp.zero_count AS ZeroCount,
                   dp.positive_offset AS PositiveOffset, dp.positive_bucket_counts AS PositiveBucketCounts,
                   dp.negative_offset AS NegativeOffset, dp.negative_bucket_counts AS NegativeBucketCounts,
                   dp.aggregation_temporality AS AggregationTemporality, dp.flags AS Flags, dp.attributes_json AS AttributesJson,
                   dp.exemplar_id AS ExemplarId, {ExemplarColumns}
            FROM exponential_histogram_data_points dp LEFT JOIN exemplars e ON dp.exemplar_id = e.id
            WHERE dp.metric_id IN ({IdInList(metricIds)}){timeClause}
            ORDER BY dp.time_unix_nano
            """, tp, cancellationToken: ct));

        var points = rows.Select(r => new MetricDataPoint
        {
            StartTimestamp = r.StartTimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(r.StartTimeUnixNano.Value) : null,
            Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
            Count = r.Count,
            Sum = r.SumValue,
            Min = r.MinValue,
            Max = r.MaxValue,
            Scale = r.Scale,
            ZeroCount = r.ZeroCount,
            PositiveOffset = r.PositiveOffset,
            PositiveBucketCounts = DeserializeArray<long>(r.PositiveBucketCounts)?.ToList(),
            NegativeOffset = r.NegativeOffset,
            NegativeBucketCounts = DeserializeArray<long>(r.NegativeBucketCounts)?.ToList(),
            AggregationTemporality = Enum.Parse<AggregationTemporality>(r.AggregationTemporality),
            Flags = r.Flags,
            Attributes = DeserializeAttributes(r.AttributesJson),
            Exemplars = BuildExemplarList(r)
        }).ToList();

        return FilterByLabelFilters(points, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetSummaryDataPointsAsync(DbConnection conn, IList<long> metricIds,
        Dictionary<string, string>? labelFilters, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        var (timeClause, tp) = TimeRange(startTime, endTime);
        var rows = await conn.QueryAsync<SummaryRow>(new CommandDefinition($"""
            SELECT dp.start_time_unix_nano AS StartTimeUnixNano, dp.time_unix_nano AS TimeUnixNano,
                   dp.count AS Count, dp.sum_value AS SumValue, dp.flags AS Flags,
                   dp.quantile_values AS QuantileValues, dp.attributes_json AS AttributesJson
            FROM summary_data_points dp
            WHERE dp.metric_id IN ({IdInList(metricIds)}){timeClause}
            ORDER BY dp.time_unix_nano
            """, tp, cancellationToken: ct));

        var points = rows.Select(r =>
        {
            var quantiles = DeserializeArray<QuantileValueModel>(r.QuantileValues);
            return new MetricDataPoint
            {
                StartTimestamp = r.StartTimeUnixNano.HasValue ? TimeConversion.UnixNanoToDateTime(r.StartTimeUnixNano.Value) : null,
                Timestamp = TimeConversion.UnixNanoToDateTime(r.TimeUnixNano),
                Count = r.Count,
                Sum = r.SumValue,
                Flags = r.Flags,
                Quantiles = quantiles?.Select(qv => qv.Quantile).ToList(),
                QuantileValues = quantiles?.Select(qv => qv.Value).ToList(),
                Attributes = DeserializeAttributes(r.AttributesJson)
            };
        }).ToList();

        return FilterByLabelFilters(points, labelFilters);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private async Task<HashSet<long>?> GetMetricIdsWithDataInRangeAsync(DbConnection conn, DateTime? startTime, DateTime? endTime, CancellationToken ct)
    {
        if (!startTime.HasValue && !endTime.HasValue)
            return null;

        var (timeClause, tp) = TimeRange(startTime, endTime);
        var ids = new HashSet<long>();

        foreach (var table in new[] { "gauge_data_points", "sum_data_points", "histogram_data_points", "exponential_histogram_data_points", "summary_data_points" })
        {
            var tableIds = await conn.QueryAsync<long>(new CommandDefinition($"""
                SELECT DISTINCT dp.metric_id
                FROM {table} dp
                JOIN metrics m   ON dp.metric_id = m.id
                JOIN resources r ON m.resource_id = r.id
                WHERE r.tenant_id = @tenantId{timeClause}
                """, Merge(new { tenantId = TenantId }, tp), cancellationToken: ct));
            foreach (var id in tableIds) ids.Add(id);
        }

        return ids;
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

    private const string ExemplarColumns =
        "e.filtered_attributes AS ExFilteredAttributes, e.time_unix_nano AS ExTimeUnixNano, e.value_double AS ExValueDouble, e.value_int AS ExValueInt, e.span_id AS ExSpanId, e.trace_id AS ExTraceId";

    private static List<ExemplarModel>? BuildExemplarList(IExemplarCarrier r)
    {
        if (r.ExemplarId == null)
            return null;
        return new List<ExemplarModel>
        {
            new()
            {
                FilteredAttributes = DeserializeAttributes(r.ExFilteredAttributes),
                TimeUnixNano = r.ExTimeUnixNano ?? 0,
                ValueDouble = r.ExValueDouble,
                ValueInt = r.ExValueInt,
                SpanIdHex = r.ExSpanId,
                TraceIdHex = r.ExTraceId
            }
        };
    }

    private static List<MetricDataPoint> FilterByLabelFilters(List<MetricDataPoint> points, Dictionary<string, string>? labelFilters)
    {
        if (labelFilters == null || labelFilters.Count == 0)
            return points;
        return points.Where(point => MatchesLabelFilters(point.Attributes, labelFilters)).ToList();
    }

    private static bool MatchesLabelFilters(Dictionary<string, object>? attributes, Dictionary<string, string> labelFilters)
    {
        if (labelFilters.Count == 0) return true;
        if (attributes == null || attributes.Count == 0) return false;

        foreach (var filter in labelFilters)
        {
            var key = attributes.Keys.FirstOrDefault(k => string.Equals(k, filter.Key, StringComparison.OrdinalIgnoreCase));
            if (key == null) return false;
            var value = ConvertAttributeValueToString(attributes[key]);
            if (!string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

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
        return $"{serviceName}{labelPart}";
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

    private static T[]? DeserializeArray<T>(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T[]>(json);

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
    // ROW DTOs
    // =========================================================================

    private interface IExemplarCarrier
    {
        long? ExemplarId { get; }
        string? ExFilteredAttributes { get; }
        long? ExTimeUnixNano { get; }
        double? ExValueDouble { get; }
        long? ExValueInt { get; }
        string? ExSpanId { get; }
        string? ExTraceId { get; }
    }

    private abstract class ExemplarCarrierRow : IExemplarCarrier
    {
        public long? ExemplarId { get; set; }
        public string? ExFilteredAttributes { get; set; }
        public long? ExTimeUnixNano { get; set; }
        public double? ExValueDouble { get; set; }
        public long? ExValueInt { get; set; }
        public string? ExSpanId { get; set; }
        public string? ExTraceId { get; set; }
        long? IExemplarCarrier.ExemplarId => ExemplarId;
    }

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
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class LatestValueRow
    {
        public string MetricName { get; set; } = null!;
        public long TimeUnixNano { get; set; }
        public double Value { get; set; }
        public string? ResourceAttributesJson { get; set; }
    }

    private sealed class GaugeRow : ExemplarCarrierRow
    {
        public long? StartTimeUnixNano { get; set; }
        public long TimeUnixNano { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public int Flags { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class SumRow : ExemplarCarrierRow
    {
        public long? StartTimeUnixNano { get; set; }
        public long TimeUnixNano { get; set; }
        public double? ValueDouble { get; set; }
        public long? ValueInt { get; set; }
        public string AggregationTemporality { get; set; } = null!;
        public bool IsMonotonic { get; set; }
        public int Flags { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class HistogramRow : ExemplarCarrierRow
    {
        public long? StartTimeUnixNano { get; set; }
        public long TimeUnixNano { get; set; }
        public long Count { get; set; }
        public double? SumValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public string AggregationTemporality { get; set; } = null!;
        public int Flags { get; set; }
        public string? BucketCounts { get; set; }
        public string? ExplicitBounds { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class ExpHistogramRow : ExemplarCarrierRow
    {
        public long? StartTimeUnixNano { get; set; }
        public long TimeUnixNano { get; set; }
        public long Count { get; set; }
        public double? SumValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public int Scale { get; set; }
        public long ZeroCount { get; set; }
        public int? PositiveOffset { get; set; }
        public string? PositiveBucketCounts { get; set; }
        public int? NegativeOffset { get; set; }
        public string? NegativeBucketCounts { get; set; }
        public string AggregationTemporality { get; set; } = null!;
        public int Flags { get; set; }
        public string? AttributesJson { get; set; }
    }

    private sealed class SummaryRow
    {
        public long? StartTimeUnixNano { get; set; }
        public long TimeUnixNano { get; set; }
        public long Count { get; set; }
        public double SumValue { get; set; }
        public int Flags { get; set; }
        public string? QuantileValues { get; set; }
        public string? AttributesJson { get; set; }
    }
}
