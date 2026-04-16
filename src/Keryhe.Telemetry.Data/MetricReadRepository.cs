using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

public class MetricReadRepository : IMetricReadRepository
{
    private readonly IDbContextFactory<TelemetryReadDbContext> _contextFactory;
    private readonly ILogger<MetricReadRepository> _logger;

    public MetricReadRepository(IDbContextFactory<TelemetryReadDbContext> contextFactory, ILogger<MetricReadRepository> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a metric by ID with all related data
    /// </summary>
    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var metric = await context.Metrics
                .Include(m => m.Resource)
                .Include(m => m.Scope)
                    .ThenInclude(s => s.Attributes)
                .Include(m => m.GaugeDataPoints)
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

            return ConvertToMetricModel(metric);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metric with ID {MetricId}", id);
            throw;
        }
    }

    /// <summary>
    /// Gets metrics by name
    /// </summary>
    public async Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var rawData = await context.Metrics
                .Include(m => m.Resource)
                .Where(m => m.Name == name)
                .Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.Unit,
                    m.Type,
                    m.CreatedAt,
                    ResourceAttributes = m.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then extract service name in memory
            var result = rawData.Select(m => new MetricInfo
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                Unit = m.Unit,
                Type = m.Type,
                ServiceName = ExtractServiceName(m.ResourceAttributes) ?? "",
                FirstSeen = m.CreatedAt,
                LastSeen = m.CreatedAt
            }).ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metrics by name {MetricName}", name);
            throw;
        }
    }

    /// <summary>
    /// Gets metrics by service name
    /// </summary>
    public async Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var query = context.Metrics
                .Include(m => m.Resource)
                .Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.Unit,
                    m.Type,
                    m.CreatedAt,
                    ResourceAttributes = m.Resource.Attributes
                });

            var metricIdsWithData = await GetMetricIdsWithDataInRangeAsync(context, startTime, endTime, cancellationToken);
            if (metricIdsWithData is { Count: 0 })
                return new List<MetricInfo>();

            if (metricIdsWithData != null)
            {
                query = query.Where(m => metricIdsWithData.Contains(m.Id));
            }

            var rawData = await query.ToListAsync(cancellationToken);

            // Then filter by service name in memory
            var result = rawData
                .Where(m => m.ResourceAttributes != null &&
                           m.ResourceAttributes.ContainsKey("service.name") &&
                           string.Equals(m.ResourceAttributes["service.name"]?.ToString(), serviceName,
                               StringComparison.OrdinalIgnoreCase))
                .Select(m => new MetricInfo
                {
                    Id = m.Id,
                    Name = m.Name,
                    Description = m.Description,
                    Unit = m.Unit,
                    Type = m.Type,
                    ServiceName = serviceName,
                    FirstSeen = m.CreatedAt,
                    LastSeen = m.CreatedAt
                })
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metrics for service {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Gets metrics by type
    /// </summary>
    public async Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var rawData = await context.Metrics
                .Include(m => m.Resource)
                .Where(m => m.Type == type)
                .Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.Unit,
                    m.Type,
                    m.CreatedAt,
                    ResourceAttributes = m.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then extract service name in memory
            var result = rawData.Select(m => new MetricInfo
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                Unit = m.Unit,
                Type = m.Type,
                ServiceName = ExtractServiceName(m.ResourceAttributes) ?? "",
                FirstSeen = m.CreatedAt,
                LastSeen = m.CreatedAt
            }).ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metrics by type {MetricType}", type);
            throw;
        }
    }

    /// <summary>
    /// Gets all metrics with pagination
    /// </summary>
    public async Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var query = context.Metrics
                .Include(m => m.Resource)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new
                {
                    m.Id,
                    m.Name,
                    m.Description,
                    m.Unit,
                    m.Type,
                    m.CreatedAt,
                    ResourceAttributes = m.Resource.Attributes
                });

            var metricIdsWithData = await GetMetricIdsWithDataInRangeAsync(context, startTime, endTime, cancellationToken);
            if (metricIdsWithData is { Count: 0 })
                return new List<MetricInfo>();

            if (metricIdsWithData != null)
            {
                query = query.Where(m => metricIdsWithData.Contains(m.Id));
            }

            var rawData = await query
                .Take(limit)
                .ToListAsync(cancellationToken);

            // Then extract service name in memory and create MetricInfo objects
            var result = rawData.Select(m => new MetricInfo
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                Unit = m.Unit,
                Type = m.Type,
                ServiceName = ExtractServiceName(m.ResourceAttributes),
                FirstSeen = m.CreatedAt,
                LastSeen = m.CreatedAt
            }).ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all metrics");
            throw;
        }
    }

    /// <summary>
    /// Gets time series data for a specific metric
    /// </summary>
    public async Task<MetricSeries?> GetMetricSeriesAsync(string metricName, Dictionary<string, string>? labelFilters = null,
        DateTime? startTime = null, DateTime? endTime = null, long? metricId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var metricQuery = context.Metrics.Where(m => m.Name == metricName);
            if (metricId.HasValue)
            {
                metricQuery = metricQuery.Where(m => m.Id == metricId.Value);
            }

            var metric = await metricQuery
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (metric == null)
                return null;

            var series = new MetricSeries
            {
                Name = metricName,
                Type = metric.Type
            };

            // Get data points based on metric type
            switch (metric.Type)
            {
                case MetricType.GAUGE:
                    series.Points = await GetGaugeDataPointsAsync(context, metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.SUM:
                    series.Points = await GetSumDataPointsAsync(context, metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.HISTOGRAM:
                    series.Points = await GetHistogramDataPointsAsync(context, metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM:
                    series.Points = await GetExponentialHistogramDataPointsAsync(context, metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.SUMMARY:
                    series.Points = await GetSummaryDataPointsAsync(context, metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
            }

            return series;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metric series for {MetricName}", metricName);
            throw;
        }
    }

    public async Task<MultiSeriesMetricData?> GetMetricSeriesByServiceAsync(string metricName,
        DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var metrics = await context.Metrics
                .Include(m => m.Resource)
                .Where(m => m.Name == metricName)
                .ToListAsync(cancellationToken);

            if (!metrics.Any())
                return null;

            var firstMetric = metrics.First();
            var result = new MultiSeriesMetricData
            {
                Name = metricName,
                Type = firstMetric.Type
            };

            foreach (var metric in metrics)
            {
                var serviceName = ExtractServiceName(metric.Resource.Attributes) ?? "unknown";

                List<MetricDataPoint> points = metric.Type switch
                {
                    MetricType.GAUGE => await GetGaugeDataPointsAsync(context, metric.Id, null, startTime, endTime, cancellationToken),
                    MetricType.SUM => await GetSumDataPointsAsync(context, metric.Id, null, startTime, endTime, cancellationToken),
                    MetricType.HISTOGRAM => await GetHistogramDataPointsAsync(context, metric.Id, null, startTime, endTime, cancellationToken),
                    MetricType.EXPONENTIAL_HISTOGRAM => await GetExponentialHistogramDataPointsAsync(context, metric.Id, null, startTime, endTime, cancellationToken),
                    MetricType.SUMMARY => await GetSummaryDataPointsAsync(context, metric.Id, null, startTime, endTime, cancellationToken),
                    _ => new List<MetricDataPoint>()
                };

                result.Series.Add(new NamedMetricSeries
                {
                    SeriesName = serviceName,
                    MetricId = metric.Id,
                    Points = points
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metric series by service for {MetricName}", metricName);
            throw;
        }
    }

    /// <summary>
    /// Gets time series data for multiple metrics
    /// </summary>
    public async Task<List<MetricSeries>> GetMultipleMetricSeriesAsync(List<string> metricNames,
        Dictionary<string, string>? labelFilters = null, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        if (metricNames == null || !metricNames.Any())
            throw new ArgumentException("Metric names list cannot be null or empty", nameof(metricNames));

        var seriesList = new List<MetricSeries>();

        foreach (var metricName in metricNames)
        {
            var series = await GetMetricSeriesAsync(
                metricName,
                labelFilters,
                startTime,
                endTime,
                metricId: null,
                cancellationToken: cancellationToken);
            if (series != null)
                seriesList.Add(series);
        }

        return seriesList;
    }

    /// <summary>
    /// Gets latest values for all metrics of a service
    /// </summary>
    public async Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var result = new Dictionary<string, double>();

            // Get latest gauge values - retrieve raw data first
            var gaugeRawData = await context.GaugeDataPoints
                .Include(gdp => gdp.Metric)
                    .ThenInclude(m => m.Resource)
                .Select(gdp => new
                {
                    MetricName = gdp.Metric.Name,
                    gdp.TimeUnixNano,
                    Value = gdp.ValueDouble ?? gdp.ValueInt ?? 0,
                    ResourceAttributes = gdp.Metric.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Filter by service name in memory
            var gaugeValues = gaugeRawData
                .Where(g => g.ResourceAttributes != null &&
                           g.ResourceAttributes.ContainsKey("service.name") &&
                           g.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(g => g.MetricName)
                .Select(g => new
                {
                    MetricName = g.Key,
                    LatestValue = g.OrderByDescending(x => x.TimeUnixNano).First().Value
                })
                .ToList();

            foreach (var gauge in gaugeValues)
            {
                result[gauge.MetricName] = gauge.LatestValue;
            }

            // Get latest sum values - retrieve raw data first
            var sumRawData = await context.SumDataPoints
                .Include(sdp => sdp.Metric)
                    .ThenInclude(m => m.Resource)
                .Select(sdp => new
                {
                    MetricName = sdp.Metric.Name,
                    sdp.TimeUnixNano,
                    Value = sdp.ValueDouble ?? sdp.ValueInt ?? 0,
                    ResourceAttributes = sdp.Metric.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Filter by service name in memory
            var sumValues = sumRawData
                .Where(s => s.ResourceAttributes != null &&
                           s.ResourceAttributes.ContainsKey("service.name") &&
                           s.ResourceAttributes["service.name"]?.ToString() == serviceName)
                .GroupBy(s => s.MetricName)
                .Select(g => new
                {
                    MetricName = g.Key,
                    LatestValue = g.OrderByDescending(x => x.TimeUnixNano).First().Value
                })
                .ToList();

            foreach (var sum in sumValues)
            {
                result[sum.MetricName] = sum.LatestValue;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving latest metric values for service {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Gets metric summaries by service
    /// </summary>
    public async Task<List<ServiceMetricSummary>> GetServiceMetricSummariesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var rawData = await context.Metrics
                .Include(m => m.Resource)
                .Select(m => new
                {
                    m.Type,
                    m.CreatedAt,
                    ResourceAttributes = m.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Then group by service name in memory
            var result = rawData
                .GroupBy(m => ExtractServiceName(m.ResourceAttributes) ?? "unknown")
                .Select(g => new ServiceMetricSummary
                {
                    ServiceName = g.Key,
                    MetricCount = g.Count(),
                    GaugeCount = g.Count(m => m.Type == MetricType.GAUGE),
                    CounterCount = g.Count(m => m.Type == MetricType.SUM),
                    HistogramCount = g.Count(m => m.Type == MetricType.HISTOGRAM || m.Type == MetricType.EXPONENTIAL_HISTOGRAM),
                    SummaryCount = g.Count(m => m.Type == MetricType.SUMMARY),
                    LastUpdated = g.Max(m => m.CreatedAt)
                })
                .OrderBy(s => s.ServiceName)
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving service metric summaries");
            throw;
        }
    }

    /// <summary>
    /// Gets metric counts by type
    /// </summary>
    public async Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var rawData = await context.Metrics
                .Include(m => m.Resource)
                .Select(m => new
                {
                    m.Type,
                    ResourceAttributes = m.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Filter by service name in memory if provided
            if (!string.IsNullOrEmpty(serviceName))
            {
                rawData = rawData
                    .Where(m => m.ResourceAttributes != null &&
                               m.ResourceAttributes.ContainsKey("service.name") &&
                               m.ResourceAttributes["service.name"]?.ToString() == serviceName)
                    .ToList();
            }

            // Group by type in memory
            var result = rawData
                .GroupBy(m => m.Type)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metric counts by type for service {ServiceName}", serviceName ?? "all");
            throw;
        }
    }

    /// <summary>
    /// Gets unique metric names
    /// </summary>
    public async Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // First, get raw data from database with Resource.Attributes
            var rawData = await context.Metrics
                .Include(m => m.Resource)
                .Select(m => new
                {
                    m.Name,
                    ResourceAttributes = m.Resource.Attributes
                })
                .ToListAsync(cancellationToken);

            // Filter by service name in memory if provided
            if (!string.IsNullOrEmpty(serviceName))
            {
                rawData = rawData
                    .Where(m => m.ResourceAttributes != null &&
                               m.ResourceAttributes.ContainsKey("service.name") &&
                               m.ResourceAttributes["service.name"]?.ToString() == serviceName)
                    .ToList();
            }

            // Get distinct names and sort in memory
            var result = rawData
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(name => name)
                .ToList();

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving unique metric names for service {ServiceName}", serviceName ?? "all");
            throw;
        }
    }

    /// <summary>
    /// Gets metric labels (attribute keys and values) for a specific metric
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetMetricLabelsAsync(string metricName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            // Find the metric
            var metric = await context.Metrics
                .FirstOrDefaultAsync(m => m.Name == metricName, cancellationToken);
            if (metric == null)
            {
                _logger.LogWarning("Metric {MetricName} not found", metricName);
                return new Dictionary<string, List<string>>();
            }
            // Get all attribute JSON from all data points for this metric based on type
            List<Dictionary<string, object>> allAttributes = new List<Dictionary<string, object>>();
            switch (metric.Type)
            {
                case MetricType.GAUGE:
                    var gaugeAttributeJson = await context.GaugeDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.AttributesJson != null)
                        .Select(dp => dp.AttributesJson)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(ParseAttributesJson(gaugeAttributeJson));
                    break;
                case MetricType.SUM:
                    var sumAttributeJson = await context.SumDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.AttributesJson != null)
                        .Select(dp => dp.AttributesJson)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(ParseAttributesJson(sumAttributeJson));
                    break;
                case MetricType.HISTOGRAM:
                    var histogramAttributeJson = await context.HistogramDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.AttributesJson != null)
                        .Select(dp => dp.AttributesJson)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(ParseAttributesJson(histogramAttributeJson));
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM:
                    var expHistogramAttributeJson = await context.ExponentialHistogramDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.AttributesJson != null)
                        .Select(dp => dp.AttributesJson)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(ParseAttributesJson(expHistogramAttributeJson));
                    break;
                case MetricType.SUMMARY:
                    var summaryAttributeJson = await context.SummaryDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.AttributesJson != null)
                        .Select(dp => dp.AttributesJson)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(ParseAttributesJson(summaryAttributeJson));
                    break;
            }
            // Extract all unique keys and their unique values
            var labelDictionary = new Dictionary<string, HashSet<string>>();
            foreach (var attributes in allAttributes)
            {
                foreach (var kvp in attributes)
                {
                    var key = kvp.Key;
                    var value = ConvertAttributeValueToString(kvp.Value);
                    if (!labelDictionary.ContainsKey(key))
                    {
                        labelDictionary[key] = new HashSet<string>();
                    }
                    labelDictionary[key].Add(value);
                }
            }
            // Convert HashSet to List and sort values
            var result = labelDictionary.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(v => v).ToList() );
            _logger.LogDebug("Retrieved {KeyCount} label keys with values for metric {MetricName}",
                result.Count, metricName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metric labels for {MetricName}", metricName);
            throw;
        }
    }

    private IEnumerable<Dictionary<string, object>> ParseAttributesJson(IEnumerable<string?> attributeJsonValues)
    {
        foreach (var attributesJson in attributeJsonValues)
        {
            if (string.IsNullOrWhiteSpace(attributesJson))
                continue;

            Dictionary<string, object>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, object>>(attributesJson);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed != null)
            {
                yield return parsed;
            }
        }
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private MetricModel? ConvertToMetricModel(Metric? metric)
    {
        if (metric == null)
        {
            return null;
        }

        var model = new MetricModel();

        return model;
    }

    private async Task<HashSet<long>?> GetMetricIdsWithDataInRangeAsync(
        TelemetryReadDbContext context,
        DateTime? startTime,
        DateTime? endTime,
        CancellationToken cancellationToken)
    {
        if (!startTime.HasValue && !endTime.HasValue)
            return null;

        var startTimeNano = startTime.HasValue
            ? OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value)
            : (long?)null;
        var endTimeNano = endTime.HasValue
            ? OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value)
            : (long?)null;

        var metricIds = new HashSet<long>();

        async Task AddMetricIdsAsync(IQueryable<long> query)
        {
            var ids = await query.Distinct().ToListAsync(cancellationToken);
            foreach (var id in ids)
            {
                metricIds.Add(id);
            }
        }

        var gaugeQuery = context.GaugeDataPoints.AsQueryable();
        if (startTimeNano.HasValue) gaugeQuery = gaugeQuery.Where(x => x.TimeUnixNano >= startTimeNano.Value);
        if (endTimeNano.HasValue) gaugeQuery = gaugeQuery.Where(x => x.TimeUnixNano <= endTimeNano.Value);
        await AddMetricIdsAsync(gaugeQuery.Select(x => x.MetricId));

        var sumQuery = context.SumDataPoints.AsQueryable();
        if (startTimeNano.HasValue) sumQuery = sumQuery.Where(x => x.TimeUnixNano >= startTimeNano.Value);
        if (endTimeNano.HasValue) sumQuery = sumQuery.Where(x => x.TimeUnixNano <= endTimeNano.Value);
        await AddMetricIdsAsync(sumQuery.Select(x => x.MetricId));

        var histogramQuery = context.HistogramDataPoints.AsQueryable();
        if (startTimeNano.HasValue) histogramQuery = histogramQuery.Where(x => x.TimeUnixNano >= startTimeNano.Value);
        if (endTimeNano.HasValue) histogramQuery = histogramQuery.Where(x => x.TimeUnixNano <= endTimeNano.Value);
        await AddMetricIdsAsync(histogramQuery.Select(x => x.MetricId));

        var exponentialHistogramQuery = context.ExponentialHistogramDataPoints.AsQueryable();
        if (startTimeNano.HasValue) exponentialHistogramQuery = exponentialHistogramQuery.Where(x => x.TimeUnixNano >= startTimeNano.Value);
        if (endTimeNano.HasValue) exponentialHistogramQuery = exponentialHistogramQuery.Where(x => x.TimeUnixNano <= endTimeNano.Value);
        await AddMetricIdsAsync(exponentialHistogramQuery.Select(x => x.MetricId));

        var summaryQuery = context.SummaryDataPoints.AsQueryable();
        if (startTimeNano.HasValue) summaryQuery = summaryQuery.Where(x => x.TimeUnixNano >= startTimeNano.Value);
        if (endTimeNano.HasValue) summaryQuery = summaryQuery.Where(x => x.TimeUnixNano <= endTimeNano.Value);
        await AddMetricIdsAsync(summaryQuery.Select(x => x.MetricId));

        return metricIds;
    }

    // Time series data retrieval methods
    private async Task<List<MetricDataPoint>> GetGaugeDataPointsAsync(TelemetryReadDbContext context,
        long metricId, Dictionary<string, string>? labelFilters,
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = context.GaugeDataPoints
            .Include(gdp => gdp.Exemplar)
            .Where(gdp => gdp.MetricId == metricId);

        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(gdp => gdp.TimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(gdp => gdp.TimeUnixNano <= endTimeNano);
        }

        var entities = await query
            .OrderBy(gdp => gdp.TimeUnixNano)
            .ToListAsync(cancellationToken);

        var dataPoints = entities
            .Select(gdp => new MetricDataPoint
            {
                StartTimestamp = gdp.StartTimeUnixNano.HasValue
                    ? OpenTelemetryDbContextExtensions.UnixNanoToDateTime(gdp.StartTimeUnixNano.Value)
                    : null,
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(gdp.TimeUnixNano),
                DoubleValue = gdp.ValueDouble,
                IntValue = gdp.ValueInt,
                Flags = gdp.Flags,
                Attributes = gdp.Attributes,
                Exemplars = BuildExemplarList(gdp.Exemplar)
            })
            .ToList();

        return FilterByLabelFilters(dataPoints, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetSumDataPointsAsync(TelemetryReadDbContext context,
        long metricId, Dictionary<string, string>? labelFilters,
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = context.SumDataPoints
            .Include(sdp => sdp.Exemplar)
            .Where(sdp => sdp.MetricId == metricId);

        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(sdp => sdp.TimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(sdp => sdp.TimeUnixNano <= endTimeNano);
        }

        var entities = await query
            .OrderBy(sdp => sdp.TimeUnixNano)
            .ToListAsync(cancellationToken);

        var dataPoints = entities
            .Select(sdp => new MetricDataPoint
            {
                StartTimestamp = sdp.StartTimeUnixNano.HasValue
                    ? OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.StartTimeUnixNano.Value)
                    : null,
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.TimeUnixNano),
                DoubleValue = sdp.ValueDouble,
                IntValue = sdp.ValueInt,
                AggregationTemporality = sdp.AggregationTemporality,
                IsMonotonic = sdp.IsMonotonic,
                Flags = sdp.Flags,
                Attributes = sdp.Attributes,
                Exemplars = BuildExemplarList(sdp.Exemplar)
            })
            .ToList();

        return FilterByLabelFilters(dataPoints, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetHistogramDataPointsAsync(TelemetryReadDbContext context,
        long metricId, Dictionary<string, string>? labelFilters,
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = context.HistogramDataPoints
            .Include(hdp => hdp.Exemplar)
            .Where(hdp => hdp.MetricId == metricId);

        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(hdp => hdp.TimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(hdp => hdp.TimeUnixNano <= endTimeNano);
        }

        var entities = await query
            .OrderBy(hdp => hdp.TimeUnixNano)
            .ToListAsync(cancellationToken);

        var dataPoints = entities
            .Select(hdp => new MetricDataPoint
            {
                StartTimestamp = hdp.StartTimeUnixNano.HasValue
                    ? OpenTelemetryDbContextExtensions.UnixNanoToDateTime(hdp.StartTimeUnixNano.Value)
                    : null,
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(hdp.TimeUnixNano),
                Count = hdp.Count,
                Sum = hdp.SumValue,
                Min = hdp.MinValue,
                Max = hdp.MaxValue,
                AggregationTemporality = hdp.AggregationTemporality,
                Flags = hdp.Flags,
                BucketCounts = (hdp.BucketCountsArray != null) ? hdp.BucketCountsArray.ToList() : new List<long>(),
                BucketBounds = (hdp.ExplicitBoundsArray != null) ? hdp.ExplicitBoundsArray.ToList() : new List<double>(),
                Attributes = hdp.Attributes,
                Exemplars = BuildExemplarList(hdp.Exemplar)
            })
            .ToList();

        return FilterByLabelFilters(dataPoints, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetExponentialHistogramDataPointsAsync(TelemetryReadDbContext context,
        long metricId, Dictionary<string, string>? labelFilters,
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = context.ExponentialHistogramDataPoints
            .Include(ehdp => ehdp.Exemplar)
            .Where(ehdp => ehdp.MetricId == metricId);

        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(ehdp => ehdp.TimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(ehdp => ehdp.TimeUnixNano <= endTimeNano);
        }

        var entities = await query
            .OrderBy(ehdp => ehdp.TimeUnixNano)
            .ToListAsync(cancellationToken);

        var dataPoints = entities
            .Select(ehdp => new MetricDataPoint
            {
                StartTimestamp = ehdp.StartTimeUnixNano.HasValue
                    ? OpenTelemetryDbContextExtensions.UnixNanoToDateTime(ehdp.StartTimeUnixNano.Value)
                    : null,
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(ehdp.TimeUnixNano),
                Count = ehdp.Count,
                Sum = ehdp.SumValue,
                Min = ehdp.MinValue,
                Max = ehdp.MaxValue,
                Scale = ehdp.Scale,
                ZeroCount = ehdp.ZeroCount,
                PositiveOffset = ehdp.PositiveOffset,
                PositiveBucketCounts = ehdp.PositiveBucketCountsArray?.ToList(),
                NegativeOffset = ehdp.NegativeOffset,
                NegativeBucketCounts = ehdp.NegativeBucketCountsArray?.ToList(),
                AggregationTemporality = ehdp.AggregationTemporality,
                Flags = ehdp.Flags,
                Attributes = ehdp.Attributes,
                Exemplars = BuildExemplarList(ehdp.Exemplar)
            })
            .ToList();

        return FilterByLabelFilters(dataPoints, labelFilters);
    }

    private async Task<List<MetricDataPoint>> GetSummaryDataPointsAsync(TelemetryReadDbContext context,
        long metricId, Dictionary<string, string>? labelFilters,
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = context.SummaryDataPoints.Where(sdp => sdp.MetricId == metricId);

        if (startTime.HasValue)
        {
            var startTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(startTime.Value);
            query = query.Where(sdp => sdp.TimeUnixNano >= startTimeNano);
        }

        if (endTime.HasValue)
        {
            var endTimeNano = OpenTelemetryDbContextExtensions.DateTimeToUnixNano(endTime.Value);
            query = query.Where(sdp => sdp.TimeUnixNano <= endTimeNano);
        }

        var entities = await query
            .OrderBy(sdp => sdp.TimeUnixNano)
            .ToListAsync(cancellationToken);

        var dataPoints = entities
            .Select(sdp => new MetricDataPoint
            {
                StartTimestamp = sdp.StartTimeUnixNano.HasValue
                    ? OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.StartTimeUnixNano.Value)
                    : null,
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.TimeUnixNano),
                Count = sdp.Count,
                Sum = sdp.SumValue,
                Flags = sdp.Flags,
                Quantiles = sdp.QuantileValuesArray != null ? sdp.QuantileValuesArray.Select(qv => qv.Quantile).ToList() : null,
                QuantileValues = sdp.QuantileValuesArray != null ? sdp.QuantileValuesArray.Select(qv => qv.Value).ToList() : null,
                Attributes = sdp.Attributes
            })
            .ToList();

        return FilterByLabelFilters(dataPoints, labelFilters);
    }

    private static List<MetricDataPoint> FilterByLabelFilters(List<MetricDataPoint> points,
        Dictionary<string, string>? labelFilters)
    {
        if (labelFilters == null || labelFilters.Count == 0)
            return points;

        return points
            .Where(point => MatchesLabelFilters(point.Attributes, labelFilters))
            .ToList();
    }

    private static bool MatchesLabelFilters(Dictionary<string, object>? attributes,
        Dictionary<string, string> labelFilters)
    {
        if (labelFilters.Count == 0)
            return true;

        if (attributes == null || attributes.Count == 0)
            return false;

        foreach (var filter in labelFilters)
        {
            var key = attributes.Keys.FirstOrDefault(k =>
                string.Equals(k, filter.Key, StringComparison.OrdinalIgnoreCase));

            if (key == null)
                return false;

            var rawValue = attributes[key];
            var value = ConvertAttributeValueToString(rawValue);

            if (!string.Equals(value, filter.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static List<ExemplarModel>? BuildExemplarList(Exemplar? exemplar)
    {
        if (exemplar == null)
            return null;

        return new List<ExemplarModel>
        {
            new()
            {
                FilteredAttributes = exemplar.FilteredAttributesDictionary,
                TimeUnixNano = exemplar.TimeUnixNano,
                ValueDouble = exemplar.ValueDouble,
                ValueInt = exemplar.ValueInt,
                SpanIdHex = exemplar.SpanId,
                TraceIdHex = exemplar.TraceId
            }
        };
    }

    /// <summary>
    /// Extracts the service name from Resource attributes
    /// </summary>
    private static string? ExtractServiceName(Dictionary<string, object>? attributes)
    {
        if (attributes == null || !attributes.ContainsKey("service.name"))
            return null;

        return attributes["service.name"]?.ToString();
    }

    /// <summary>
    /// Helper method to convert attribute values to strings for label display
    /// </summary>
    private static string ConvertAttributeValueToString(object value)
    {
        return value switch
        {
            null => "",
            string str => str,
            bool b => b.ToString().ToLower(),
            int i => i.ToString(),
            long l => l.ToString(),
            double d => d.ToString("G17"),
            float f => f.ToString("G9"),
            byte[] bytes => Convert.ToBase64String(bytes),
            JsonElement jsonElement => ConvertJsonElementToString(jsonElement),
            _ => JsonSerializer.Serialize(value)
        };
    }

    /// <summary>
    /// Helper method to convert JsonElement to string
    /// </summary>
    private static string ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetDouble().ToString("G17"),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            JsonValueKind.Array => JsonSerializer.Serialize(element),
            JsonValueKind.Object => JsonSerializer.Serialize(element),
            _ => element.ToString()
        };
    }
}
