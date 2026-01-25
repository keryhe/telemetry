using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.Data.Access;
using Keryhe.Telemetry.Data.Access.Models;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Data;

// =============================================================================
// METRICS REPOSITORY IMPLEMENTATION
// =============================================================================

public class MetricRepository : IMetricRepository
{
    private readonly OpenTelemetryDbContext _context;
    private readonly ILogger<MetricRepository> _logger;

    public MetricRepository(OpenTelemetryDbContext context, ILogger<MetricRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Stores a single metric with all its data points
    /// </summary>
    public async Task<long> StoreMetricAsync(MetricModel metric, CancellationToken cancellationToken = default)
    {
        if (metric == null)
            throw new ArgumentNullException(nameof(metric));

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            // Get or create resource and scope
            var resource = await GetOrCreateResourceAsync(metric.Resource, cancellationToken);
            var scope = await GetOrCreateInstrumentationScopeAsync(metric.InstrumentationScope, cancellationToken);

            // Create metric entity
            var metricEntity = new Metric
            {
                ResourceId = resource.Id,
                ScopeId = scope.Id,
                Name = metric.Name,
                Description = metric.Description,
                Unit = metric.Unit,
                Type = metric.Type
            };

            _context.Metrics.Add(metricEntity);
            await _context.SaveChangesAsync(cancellationToken);

            // Store data points based on metric type
            await StoreDataPointsAsync(metricEntity.Id, metric, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored metric {MetricName} with ID {MetricId}",
                metric.Name, metricEntity.Id);

            return metricEntity.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing metric {MetricName}", metric.Name);
            throw;
        }
        finally
        {
            Threading.LockSignal.Release();
        }
    }

    /// <summary>
    /// Stores multiple metrics in a batch
    /// </summary>
    public async Task<IEnumerable<long>> StoreMetricsBatchAsync(IEnumerable<MetricModel> metrics, CancellationToken cancellationToken = default)
    {
        if (metrics == null)
            throw new ArgumentNullException(nameof(metrics));

        var metricsList = metrics.ToList();
        if (!metricsList.Any())
            return Enumerable.Empty<long>();

        var metricIds = new List<long>();

        Threading.LockSignal.Wait();
        try
        {
            using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var resourceCache = new Dictionary<string, Resource>();
            var scopeCache = new Dictionary<string, InstrumentationScope>();

            foreach (var metric in metricsList)
            {
                // Get or create resource
                var resourceKey = GenerateResourceKey(metric.Resource);
                if (!resourceCache.TryGetValue(resourceKey, out var resource))
                {
                    resource = await GetOrCreateResourceAsync(metric.Resource, cancellationToken);
                    resourceCache[resourceKey] = resource;
                }

                // Get or create scope
                var scopeKey = GenerateScopeKey(metric.InstrumentationScope);
                if (!scopeCache.TryGetValue(scopeKey, out var scope))
                {
                    scope = await GetOrCreateInstrumentationScopeAsync(metric.InstrumentationScope, cancellationToken);
                    scopeCache[scopeKey] = scope;
                }

                // Create metric entity
                var metricEntity = new Metric
                {
                    ResourceId = resource.Id,
                    ScopeId = scope.Id,
                    Name = metric.Name,
                    Description = metric.Description,
                    Unit = metric.Unit,
                    Type = metric.Type
                };

                _context.Metrics.Add(metricEntity);
                await _context.SaveChangesAsync(cancellationToken);

                metricIds.Add(metricEntity.Id);

                // Store data points
                await StoreDataPointsAsync(metricEntity.Id, metric, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            _logger.LogDebug("Successfully stored batch of {Count} metrics", metricsList.Count);
            return metricIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error storing metrics batch");
            throw;
        }
        finally
        {
            Threading.LockSignal.Release();
        }
    }

    /// <summary>
    /// Gets a metric by ID with all related data
    /// </summary>
    public async Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var metric = await _context.Metrics
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
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
    public async Task<List<MetricInfo>> GetMetricsByServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(serviceName))
            throw new ArgumentException("Service name cannot be null or empty", nameof(serviceName));

        try
        {
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
                })
                .ToListAsync(cancellationToken);

            // Then filter by service name in memory
            var result = rawData
                .Where(m => m.ResourceAttributes != null &&
                           m.ResourceAttributes.ContainsKey("service.name") &&
                           m.ResourceAttributes["service.name"]?.ToString() == serviceName)
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
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
    public async Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
                .Include(m => m.Resource)
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit)
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
        DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        try
        {
            var metric = await _context.Metrics
                .FirstOrDefaultAsync(m => m.Name == metricName, cancellationToken);

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
                    series.Points = await GetGaugeDataPointsAsync(metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.SUM:
                    series.Points = await GetSumDataPointsAsync(metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.HISTOGRAM:
                    series.Points = await GetHistogramDataPointsAsync(metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM:
                    series.Points = await GetExponentialHistogramDataPointsAsync(metric.Id, labelFilters, startTime, endTime, cancellationToken);
                    break;
                case MetricType.SUMMARY:
                    series.Points = await GetSummaryDataPointsAsync(metric.Id, labelFilters, startTime, endTime, cancellationToken);
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
            var series = await GetMetricSeriesAsync(metricName, labelFilters, startTime, endTime, cancellationToken);
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
            var result = new Dictionary<string, double>();

            // Get latest gauge values - retrieve raw data first
            var gaugeRawData = await _context.GaugeDataPoints
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
            var sumRawData = await _context.SumDataPoints
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
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
            // First, get raw data from database with Resource.Attributes
            var rawData = await _context.Metrics
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
            // Find the metric
            var metric = await _context.Metrics
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
                    var gaugeAttributes = await _context.GaugeDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.Attributes != null)
                        .Select(dp => dp.Attributes)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(gaugeAttributes.Where(a => a != null)!);
                    break;
                case MetricType.SUM:
                    var sumAttributes = await _context.SumDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.Attributes != null)
                        .Select(dp => dp.Attributes)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(sumAttributes.Where(a => a != null)!);
                    break;
                case MetricType.HISTOGRAM:
                    var histogramAttributes = await _context.HistogramDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.Attributes != null)
                        .Select(dp => dp.Attributes)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(histogramAttributes.Where(a => a != null)!);
                    break;
                case MetricType.EXPONENTIAL_HISTOGRAM:
                    var expHistogramAttributes = await _context.ExponentialHistogramDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.Attributes != null)
                        .Select(dp => dp.Attributes)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(expHistogramAttributes.Where(a => a != null)!);
                    break;
                case MetricType.SUMMARY:
                    var summaryAttributes = await _context.SummaryDataPoints
                        .Where(dp => dp.MetricId == metric.Id && dp.Attributes != null)
                        .Select(dp => dp.Attributes)
                        .ToListAsync(cancellationToken);
                    allAttributes.AddRange(summaryAttributes.Where(a => a != null)!);
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
    
    /// <summary>
    /// Deletes a metric by ID
    /// </summary>
    public async Task<bool> DeleteMetricAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            var metric = await _context.Metrics.FindAsync(new object[] { id }, cancellationToken);
            if (metric == null)
                return false;

            _context.Metrics.Remove(metric);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted metric with ID {MetricId}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting metric with ID {MetricId}", id);
            throw;
        }
    }

    /// <summary>
    /// Deletes metrics by name
    /// </summary>
    public async Task<int> DeleteMetricsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        try
        {
            var metrics = await _context.Metrics
                .Where(m => m.Name == name)
                .ToListAsync(cancellationToken);

            var deleteCount = metrics.Count;
            _context.Metrics.RemoveRange(metrics);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted {Count} metrics with name {MetricName}", deleteCount, name);
            return deleteCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting metrics with name {MetricName}", name);
            throw;
        }
    }

    /// <summary>
    /// Deletes metrics within a time range
    /// </summary>
    public async Task<int> DeleteMetricsByTimeRangeAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken = default)
    {
        if (startTime >= endTime)
            throw new ArgumentException("Start time must be before end time");

        try
        {
            var metrics = await _context.Metrics
                .Where(m => m.CreatedAt >= startTime && m.CreatedAt <= endTime)
                .ToListAsync(cancellationToken);

            var deleteCount = metrics.Count;
            _context.Metrics.RemoveRange(metrics);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted {Count} metrics for time range {StartTime} to {EndTime}", 
                deleteCount, startTime, endTime);
            
            return deleteCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting metrics for time range {StartTime} to {EndTime}", startTime, endTime);
            throw;
        }
    }

    /// <summary>
    /// Deletes old metrics based on retention period
    /// </summary>
    public async Task<int> DeleteOldMetricsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow - retentionPeriod;

            var metrics = await _context.Metrics
                .Where(m => m.CreatedAt < cutoffTime)
                .ToListAsync(cancellationToken);

            var deleteCount = metrics.Count;
            _context.Metrics.RemoveRange(metrics);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogDebug("Successfully deleted {Count} old metrics older than {CutoffTime}", deleteCount, cutoffTime);
            return deleteCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting old metrics with retention period {RetentionPeriod}", retentionPeriod);
            throw;
        }
    }
    
    private MetricModel? ConvertToMetricModel(Metric? metric)
    {
        if (metric == null)
        {
            return null;
        }
        
        var model = new MetricModel();
        
        return model;
    }

    // =============================================================================
    // PRIVATE HELPER METHODS
    // =============================================================================

    private async Task StoreDataPointsAsync(long metricId, MetricModel metric, CancellationToken cancellationToken)
    {
        switch (metric.Type)
        {
            case MetricType.GAUGE:
                if (metric.GaugeDataPoints?.Any() == true)
                    await StoreGaugeDataPointsAsync(metricId, metric.GaugeDataPoints, cancellationToken);
                break;

            case MetricType.SUM:
                if (metric.SumDataPoints?.Any() == true)
                    await StoreSumDataPointsAsync(metricId, metric.SumDataPoints, cancellationToken);
                break;

            case MetricType.HISTOGRAM:
                if (metric.HistogramDataPoints?.Any() == true)
                    await StoreHistogramDataPointsAsync(metricId, metric.HistogramDataPoints, cancellationToken);
                break;

            case MetricType.EXPONENTIAL_HISTOGRAM:
                if (metric.ExponentialHistogramDataPoints?.Any() == true)
                    await StoreExponentialHistogramDataPointsAsync(metricId, metric.ExponentialHistogramDataPoints, cancellationToken);
                break;

            case MetricType.SUMMARY:
                if (metric.SummaryDataPoints?.Any() == true)
                    await StoreSummaryDataPointsAsync(metricId, metric.SummaryDataPoints, cancellationToken);
                break;
        }
    }

    private async Task StoreGaugeDataPointsAsync(long metricId, List<GaugeDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<GaugeDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            // Store exemplar if present
            long? exemplarId = null;
            if (dp.Exemplar != null)
            {
                var exemplar = await CreateExemplarAsync(dp.Exemplar, cancellationToken);
                exemplars.Add(exemplar);
                exemplarId = exemplar.Id;
            }

            var entity = new GaugeDataPoint
            {
                MetricId = metricId,
                StartTimeUnixNano = dp.StartTimeUnixNano,
                TimeUnixNano = dp.TimeUnixNano,
                ValueDouble = dp.ValueDouble,
                ValueInt = dp.ValueInt,
                Flags = dp.Flags,
                ExemplarId = exemplarId,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Any())
        {
            _context.Exemplars.AddRange(exemplars);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _context.GaugeDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreSumDataPointsAsync(long metricId, List<SumDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<SumDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            // Store exemplar if present
            long? exemplarId = null;
            if (dp.Exemplar != null)
            {
                var exemplar = await CreateExemplarAsync(dp.Exemplar, cancellationToken);
                exemplars.Add(exemplar);
                exemplarId = exemplar.Id;
            }

            var entity = new SumDataPoint
            {
                MetricId = metricId,
                StartTimeUnixNano = dp.StartTimeUnixNano,
                TimeUnixNano = dp.TimeUnixNano,
                ValueDouble = dp.ValueDouble,
                ValueInt = dp.ValueInt,
                AggregationTemporality = dp.AggregationTemporality,
                IsMonotonic = dp.IsMonotonic,
                Flags = dp.Flags,
                ExemplarId = exemplarId,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Any())
        {
            _context.Exemplars.AddRange(exemplars);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _context.SumDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreHistogramDataPointsAsync(long metricId, List<HistogramDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<HistogramDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            // Store exemplars if present
            long? exemplarId = null;
            if (dp.Exemplars?.Any() == true)
            {
                var exemplar = await CreateExemplarAsync(dp.Exemplars.First(), cancellationToken);
                exemplars.Add(exemplar);
                exemplarId = exemplar.Id;
            }

            var entity = new HistogramDataPoint
            {
                MetricId = metricId,
                StartTimeUnixNano = dp.StartTimeUnixNano,
                TimeUnixNano = dp.TimeUnixNano,
                Count = dp.Count,
                SumValue = dp.Sum,
                BucketCountsArray = dp.BucketCounts,
                ExplicitBoundsArray = dp.ExplicitBounds,
                AggregationTemporality = dp.AggregationTemporality,
                Flags = dp.Flags,
                MinValue = dp.Min,
                MaxValue = dp.Max,
                ExemplarId = exemplarId,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Any())
        {
            _context.Exemplars.AddRange(exemplars);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _context.HistogramDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreExponentialHistogramDataPointsAsync(long metricId, List<ExponentialHistogramDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<ExponentialHistogramDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            // Store exemplars if present
            long? exemplarId = null;
            if (dp.Exemplars?.Any() == true)
            {
                var exemplar = await CreateExemplarAsync(dp.Exemplars.First(), cancellationToken);
                exemplars.Add(exemplar);
                exemplarId = exemplar.Id;
            }

            var entity = new ExponentialHistogramDataPoint
            {
                MetricId = metricId,
                StartTimeUnixNano = dp.StartTimeUnixNano,
                TimeUnixNano = dp.TimeUnixNano,
                Count = dp.Count,
                SumValue = dp.Sum,
                Scale = dp.Scale,
                ZeroCount = dp.ZeroCount,
                PositiveOffset = dp.PositiveOffset,
                PositiveBucketCountsArray = dp.PositiveBucketCounts,
                NegativeOffset = dp.NegativeOffset,
                NegativeBucketCountsArray = dp.NegativeBucketCounts,
                AggregationTemporality = dp.AggregationTemporality,
                Flags = dp.Flags,
                MinValue = dp.Min,
                MaxValue = dp.Max,
                ExemplarId = exemplarId,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Any())
        {
            _context.Exemplars.AddRange(exemplars);
            await _context.SaveChangesAsync(cancellationToken);
        }

        _context.ExponentialHistogramDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreSummaryDataPointsAsync(long metricId, List<SummaryDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = dataPoints.Select(dp => new SummaryDataPoint
        {
            MetricId = metricId,
            StartTimeUnixNano = dp.StartTimeUnixNano,
            TimeUnixNano = dp.TimeUnixNano,
            Count = dp.Count,
            SumValue = dp.Sum,
            QuantileValuesArray = dp.QuantileValues?.Select(qv => new QuantileValue { Quantile = qv.Quantile, Value = qv.Value }).ToArray(),
            Flags = dp.Flags,
            Attributes = dp.Attributes
            
        }).ToList();

        _context.SummaryDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<Exemplar> CreateExemplarAsync(ExemplarModel exemplarModel, CancellationToken cancellationToken)
    {
        var exemplar = new Exemplar
        {
            FilteredAttributesDictionary = exemplarModel.FilteredAttributes,
            TimeUnixNano = exemplarModel.TimeUnixNano,
            ValueDouble = exemplarModel.ValueDouble,
            ValueInt = exemplarModel.ValueInt,
            SpanId = exemplarModel.SpanIdHex,
            TraceId = exemplarModel.TraceIdHex
        };

        return exemplar;
    }

    private static long GetDataPointId(object entity)
    {
        return entity switch
        {
            GaugeDataPoint gdp => gdp.Id,
            SumDataPoint sdp => sdp.Id,
            HistogramDataPoint hdp => hdp.Id,
            ExponentialHistogramDataPoint ehdp => ehdp.Id,
            SummaryDataPoint sdp => sdp.Id,
            _ => throw new ArgumentException($"Unknown data point entity type: {entity.GetType()}")
        };
    }

    // Time series data retrieval methods
    private async Task<List<MetricDataPoint>> GetGaugeDataPointsAsync(long metricId, Dictionary<string, string>? labelFilters, 
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = _context.GaugeDataPoints.Where(gdp => gdp.MetricId == metricId);

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

        var dataPoints = await query
            .OrderBy(gdp => gdp.TimeUnixNano)
            .Select(gdp => new MetricDataPoint
            {
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(gdp.TimeUnixNano),
                DoubleValue = gdp.ValueDouble,
                IntValue = gdp.ValueInt,
                Attributes = gdp.Attributes
            })
            .ToListAsync(cancellationToken);

        return dataPoints;
    }

    private async Task<List<MetricDataPoint>> GetSumDataPointsAsync(long metricId, Dictionary<string, string>? labelFilters, 
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = _context.SumDataPoints.Where(sdp => sdp.MetricId == metricId);

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

        var dataPoints = await query
            .OrderBy(sdp => sdp.TimeUnixNano)
            .Select(sdp => new MetricDataPoint
            {
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.TimeUnixNano),
                DoubleValue = sdp.ValueDouble,
                IntValue = sdp.ValueInt,
                Attributes = sdp.Attributes
            })
            .ToListAsync(cancellationToken);

        return dataPoints;
    }

    private async Task<List<MetricDataPoint>> GetHistogramDataPointsAsync(long metricId, Dictionary<string, string>? labelFilters, 
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = _context.HistogramDataPoints.Where(hdp => hdp.MetricId == metricId);

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

        var dataPoints = await query
            .OrderBy(hdp => hdp.TimeUnixNano)
            .Select(hdp => new MetricDataPoint
            {
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(hdp.TimeUnixNano),
                Count = hdp.Count,
                Sum = hdp.SumValue,
                Min = hdp.MinValue,
                Max = hdp.MaxValue,
                BucketCounts = (hdp.BucketCountsArray != null) ? hdp.BucketCountsArray.ToList() : new List<long>(),
                BucketBounds = (hdp.ExplicitBoundsArray != null) ? hdp.ExplicitBoundsArray.ToList() : new List<double>(),
                Attributes = hdp.Attributes
            })
            .ToListAsync(cancellationToken);

        return dataPoints;
    }

    private async Task<List<MetricDataPoint>> GetExponentialHistogramDataPointsAsync(long metricId, Dictionary<string, string>? labelFilters, 
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = _context.ExponentialHistogramDataPoints.Where(ehdp => ehdp.MetricId == metricId);

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

        var dataPoints = await query
            .OrderBy(ehdp => ehdp.TimeUnixNano)
            .Select(ehdp => new MetricDataPoint
            {
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(ehdp.TimeUnixNano),
                Count = ehdp.Count,
                Sum = ehdp.SumValue,
                Min = ehdp.MinValue,
                Max = ehdp.MaxValue,
                Attributes = ehdp.Attributes
            })
            .ToListAsync(cancellationToken);

        return dataPoints;
    }

    private async Task<List<MetricDataPoint>> GetSummaryDataPointsAsync(long metricId, Dictionary<string, string>? labelFilters, 
        DateTime? startTime, DateTime? endTime, CancellationToken cancellationToken)
    {
        var query = _context.SummaryDataPoints.Where(sdp => sdp.MetricId == metricId);

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

        var dataPoints = await query
            .OrderBy(sdp => sdp.TimeUnixNano)
            .Select(sdp => new MetricDataPoint
            {
                Timestamp = OpenTelemetryDbContextExtensions.UnixNanoToDateTime(sdp.TimeUnixNano),
                Count = sdp.Count,
                Sum = sdp.SumValue,
                Quantiles = sdp.QuantileValuesArray != null ? sdp.QuantileValuesArray.Select(qv => qv.Quantile).ToList() : null,
                QuantileValues = sdp.QuantileValuesArray != null ? sdp.QuantileValuesArray.Select(qv => qv.Value).ToList() : null,
                Attributes = sdp.Attributes
            })
            .ToListAsync(cancellationToken);

        return dataPoints;
    }

    private async Task<Resource> GetOrCreateResourceAsync(ResourceModel? resourceModel, CancellationToken cancellationToken)
    {
        if (resourceModel == null)
        {
            resourceModel = new ResourceModel
            {
                Attributes = new Dictionary<string, object> { { "service.name", "unknown" } }
            };
        }

        var resourceHash = GenerateResourceHash(resourceModel);

        var existingResource = await _context.Resources
            .FirstOrDefaultAsync(r => r.ResourceHash == resourceHash, cancellationToken);

        if (existingResource != null)
            return existingResource;

        var resource = new Resource
        {
            ResourceHash = resourceHash,
            SchemaUrl = resourceModel.SchemaUrl,
            Attributes = resourceModel.Attributes
        };

        _context.Resources.Add(resource);
        await _context.SaveChangesAsync(cancellationToken);

        return resource;
    }

    private async Task<InstrumentationScope> GetOrCreateInstrumentationScopeAsync(InstrumentationScopeModel? scopeModel, CancellationToken cancellationToken)
    {
        if (scopeModel == null)
        {
            scopeModel = new InstrumentationScopeModel { Name = "unknown" };
        }

        var scopeHash = GenerateScopeHash(scopeModel);

        var existingScope = await _context.InstrumentationScopes
            .FirstOrDefaultAsync(s => s.ScopeHash == scopeHash, cancellationToken);

        if (existingScope != null)
            return existingScope;

        var scope = new InstrumentationScope
        {
            Name = scopeModel.Name,
            Version = scopeModel.Version,
            SchemaUrl = scopeModel.SchemaUrl,
            ScopeHash = scopeHash,
            Attributes = scopeModel.Attributes
        };

        _context.InstrumentationScopes.Add(scope);
        await _context.SaveChangesAsync(cancellationToken);

        return scope;
    }

    private static string GenerateResourceHash(ResourceModel resourceModel)
    {
        var content = $"{resourceModel.SchemaUrl ?? ""}__{JsonSerializer.Serialize(resourceModel.Attributes ?? new Dictionary<string, object>())}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateScopeHash(InstrumentationScopeModel scopeModel)
    {
        var content = $"{scopeModel.Name}__{scopeModel.Version ?? ""}__{scopeModel.SchemaUrl ?? ""}__{JsonSerializer.Serialize(scopeModel.Attributes ?? new Dictionary<string, object>())}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateResourceKey(ResourceModel? resourceModel)
    {
        return GenerateResourceHash(resourceModel ?? new ResourceModel());
    }

    private static string GenerateScopeKey(InstrumentationScopeModel? scopeModel)
    {
        return GenerateScopeHash(scopeModel ?? new InstrumentationScopeModel { Name = "unknown" });
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

    private static AttributeType DetermineAttributeType(object value)
    {
        return value switch
        {
            string => AttributeType.STRING,
            bool => AttributeType.BOOL,
            int or long => AttributeType.INT,
            double or float => AttributeType.DOUBLE,
            byte[] => AttributeType.BYTES,
            Array or IEnumerable<object> => AttributeType.ARRAY,
            _ => AttributeType.KVLIST
        };
    }
}
