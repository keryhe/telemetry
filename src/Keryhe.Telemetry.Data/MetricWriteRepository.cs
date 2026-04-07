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

public class MetricWriteRepository : IMetricWriteRepository
{
    private readonly TelemetryWriteDbContext _context;
    private readonly ILogger<MetricWriteRepository> _logger;

    public MetricWriteRepository(TelemetryWriteDbContext context, ILogger<MetricWriteRepository> logger)
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
            Exemplar? exemplar = null;
            if (dp.Exemplar != null)
            {
                exemplar = CreateExemplar(dp.Exemplar);
                exemplars.Add(exemplar);
            }

            var entity = new GaugeDataPoint
            {
                MetricId = metricId,
                StartTimeUnixNano = dp.StartTimeUnixNano,
                TimeUnixNano = dp.TimeUnixNano,
                ValueDouble = dp.ValueDouble,
                ValueInt = dp.ValueInt,
                Flags = dp.Flags,
                Exemplar = exemplar,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Count > 0)
            _context.Exemplars.AddRange(exemplars);

        _context.GaugeDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreSumDataPointsAsync(long metricId, List<SumDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<SumDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            Exemplar? exemplar = null;
            if (dp.Exemplar != null)
            {
                exemplar = CreateExemplar(dp.Exemplar);
                exemplars.Add(exemplar);
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
                Exemplar = exemplar,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Count > 0)
            _context.Exemplars.AddRange(exemplars);

        _context.SumDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreHistogramDataPointsAsync(long metricId, List<HistogramDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<HistogramDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            Exemplar? exemplar = null;
            if (dp.Exemplars?.Any() == true)
            {
                exemplar = CreateExemplar(dp.Exemplars.First());
                exemplars.Add(exemplar);
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
                Exemplar = exemplar,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Count > 0)
            _context.Exemplars.AddRange(exemplars);

        _context.HistogramDataPoints.AddRange(entities);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task StoreExponentialHistogramDataPointsAsync(long metricId, List<ExponentialHistogramDataPointModel> dataPoints, CancellationToken cancellationToken)
    {
        var entities = new List<ExponentialHistogramDataPoint>();
        var exemplars = new List<Exemplar>();

        foreach (var dp in dataPoints)
        {
            Exemplar? exemplar = null;
            if (dp.Exemplars?.Any() == true)
            {
                exemplar = CreateExemplar(dp.Exemplars.First());
                exemplars.Add(exemplar);
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
                Exemplar = exemplar,
                Attributes = dp.Attributes
            };

            entities.Add(entity);
        }

        if (exemplars.Count > 0)
            _context.Exemplars.AddRange(exemplars);

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

    private static Exemplar CreateExemplar(ExemplarModel exemplarModel)
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

    private async Task<Resource> GetOrCreateResourceAsync(ResourceModel? resourceModel, CancellationToken cancellationToken)
    {
        resourceModel = NormalizeResourceModel(resourceModel);

        var resourceHash = GenerateResourceHash(resourceModel);

                var resourceIdRows = await _context.Database.SqlQueryRaw<long>(
            @"INSERT INTO resources (""AttributesJson"", ""CreatedAt"", ""ResourceHash"", ""SchemaUrl"")
              VALUES (CAST({0} AS jsonb), {1}, {2}, {3})
              ON CONFLICT (""ResourceHash"")
              DO UPDATE SET ""ResourceHash"" = EXCLUDED.""ResourceHash""
              RETURNING ""Id"";",
            SerializeAttributesDeterministically(resourceModel.Attributes),
            DateTime.UtcNow,
            resourceHash,
            (object?)resourceModel.SchemaUrl ?? DBNull.Value)
                        .ToListAsync(cancellationToken);

                var resourceId = resourceIdRows.Single();

        return new Resource { Id = resourceId };
    }

    private async Task<InstrumentationScope> GetOrCreateInstrumentationScopeAsync(InstrumentationScopeModel? scopeModel, CancellationToken cancellationToken)
    {
        if (scopeModel == null)
        {
            scopeModel = new InstrumentationScopeModel { Name = "unknown" };
        }

        var scopeHash = GenerateScopeHash(scopeModel);

                var scopeIdRows = await _context.Database.SqlQueryRaw<long>(
            @"INSERT INTO instrumentation_scopes (""Name"", ""Version"", ""SchemaUrl"", ""ScopeHash"", ""CreatedAt"", ""AttributesJson"")
              VALUES ({0}, {1}, {2}, {3}, {4}, CAST({5} AS jsonb))
              ON CONFLICT (""ScopeHash"")
              DO UPDATE SET ""ScopeHash"" = EXCLUDED.""ScopeHash""
              RETURNING ""Id"";",
            scopeModel.Name,
            (object?)scopeModel.Version ?? DBNull.Value,
            (object?)scopeModel.SchemaUrl ?? DBNull.Value,
            scopeHash,
            DateTime.UtcNow,
            SerializeAttributesDeterministically(scopeModel.Attributes))
                        .ToListAsync(cancellationToken);

                var scopeId = scopeIdRows.Single();

        return new InstrumentationScope { Id = scopeId };
    }

    private static string GenerateResourceHash(ResourceModel resourceModel)
    {
        var content = $"{resourceModel.SchemaUrl ?? ""}__{SerializeAttributesDeterministically(resourceModel.Attributes)}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateScopeHash(InstrumentationScopeModel scopeModel)
    {
        var content = $"{scopeModel.Name}__{scopeModel.Version ?? ""}__{scopeModel.SchemaUrl ?? ""}__{SerializeAttributesDeterministically(scopeModel.Attributes)}";
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateResourceKey(ResourceModel? resourceModel)
    {
        return GenerateResourceHash(NormalizeResourceModel(resourceModel));
    }

    private static string GenerateScopeKey(InstrumentationScopeModel? scopeModel)
    {
        return GenerateScopeHash(scopeModel ?? new InstrumentationScopeModel { Name = "unknown" });
    }

    private static ResourceModel NormalizeResourceModel(ResourceModel? resourceModel)
    {
        return resourceModel ?? new ResourceModel
        {
            Attributes = new Dictionary<string, object> { { "service.name", "unknown" } }
        };
    }

    private static string SerializeAttributesDeterministically(Dictionary<string, object>? attributes)
    {
        var ordered = (attributes ?? new Dictionary<string, object>())
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return JsonSerializer.Serialize(ordered);
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
