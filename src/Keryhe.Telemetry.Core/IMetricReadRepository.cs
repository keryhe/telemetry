using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// METRIC READ REPOSITORY INTERFACE
// =============================================================================

public interface IMetricReadRepository
{
    // Retrieve operations
    Task<MetricModel?> GetMetricByIdAsync(long id, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByNameAsync(string name, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetMetricsByTypeAsync(MetricType type, CancellationToken cancellationToken = default);
    Task<List<MetricInfo>> GetAllMetricsAsync(int limit = 100, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default);

    /// <summary>True (unbounded) distinct-metric-name counts per type for a time range, unaffected by <see cref="GetAllMetricsAsync"/>'s row limit.</summary>
    Task<MetricsSummary> GetMetricsSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, CancellationToken cancellationToken = default);

    // Time series data — Phase 4 (list-pages-server-side plan): one bucketed query over every
    // metric row sharing the name, with per-stream math, top-N ranking and "other" folding done
    // per decisions 21-23, 42. Replaces the former GetMetricSeriesAsync(raw)/GetGroupedMetricSeriesAsync
    // pair; see MetricReadRepositoryBase's doc comment for the aggregation pipeline.
    Task<MetricSeriesResult?> GetMetricSeriesAsync(MetricSeriesQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest exemplars for a metric, independent of the series reads. Returns an empty page for
    /// SUMMARY, whose table has no exemplars_json column (OTLP declares no exemplars on Summary).
    /// Analytics-tier providers override this for real keyset paging (decision 26); the base
    /// implementation is the standard-tier newest-500 scan with <see cref="MetricExemplarPage.Capped"/>.
    /// </summary>
    Task<MetricExemplarPage?> GetMetricExemplarsAsync(MetricExemplarQuery query, CancellationToken cancellationToken = default);

    // Aggregation and analysis
    Task<Dictionary<string, double>> GetLatestMetricValuesAsync(string serviceName, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetMetricCountsByTypeAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    Task<List<string>> GetUniqueMetricNamesAsync(string? serviceName = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Distinct attribute (label) sets for a metric, time-bounded to <paramref name="startTime"/>/
    /// <paramref name="endTime"/> (defaults to the last 24 hours) and capped at 1,000 distinct rows
    /// (list-pages-server-side plan, Phase 1, decision 25); <see cref="MetricLabelsResult.Partial"/>
    /// is true when the cap was hit.
    /// </summary>
    Task<MetricLabelsResult> GetMetricLabelsAsync(string metricName, DateTime? startTime = null,
        DateTime? endTime = null, CancellationToken cancellationToken = default);
}
