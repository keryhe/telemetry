using System.Text.Json;
using DuckDB.NET.Data;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Parquet;

public sealed partial class ParquetBulkWriter
{
    public Task FlushMetricsAsync(List<MetricModel> metrics, CancellationToken cancellationToken = default)
        => FlushMetricsAsync(metrics, NewToken(), cancellationToken);

    public async Task FlushMetricsAsync(List<MetricModel> metrics, string token, CancellationToken ct = default)
    {
        if (metrics.Count == 0) return;
        ValidateToken(token);

        // Points are grouped by destination table across the whole batch, so a flush is a few files per hour, not one per metric.
        var context = new FlushContext();
        var built = new MetricRows();
        foreach (var metric in metrics) AddMetric(metric, context, built);

        if (built.Gauge.Count > 0) await WriteRowsAsync(ParquetTables.GaugeTable, built.Gauge, p => p.C.TenantId, p => p.C.Time, AppendGauge, token, ct);
        if (built.Sum.Count > 0) await WriteRowsAsync(ParquetTables.SumTable, built.Sum, p => p.C.TenantId, p => p.C.Time, AppendSum, token, ct);
        if (built.Histogram.Count > 0) await WriteRowsAsync(ParquetTables.HistogramTable, built.Histogram, p => p.C.TenantId, p => p.C.Time, AppendHistogram, token, ct);
        if (built.ExpHistogram.Count > 0) await WriteRowsAsync(ParquetTables.ExpHistogramTable, built.ExpHistogram, p => p.C.TenantId, p => p.C.Time, AppendExpHistogram, token, ct);
        if (built.Summary.Count > 0) await WriteRowsAsync(ParquetTables.SummaryTable, built.Summary, p => p.C.TenantId, p => p.C.Time, AppendSummary, token, ct);

        await built.Derived.WriteAsync(this, token, ct);
        logger.LogDebug("Flushed {Count} metrics", metrics.Count);
    }

    // =========================================================================
    // POINT ROWS
    // =========================================================================

    private sealed record PointCommon(
        long TenantId, string Service, string MetricName, ulong SeriesId, long Time, long StartTime, string Unit, string Description,
        ulong Flags, string AttributesJson, ResourceInfo Resource, ScopeInfo Scope);

    private sealed record GaugePoint(PointCommon C, double Value, string Exemplars);
    private sealed record SumPoint(PointCommon C, double Value, string Temporality, bool IsMonotonic, string Exemplars);
    private sealed record HistogramPoint(
        PointCommon C, ulong Count, double? Sum, double? Min, double? Max, string BucketCounts, string Bounds, string Temporality, string Exemplars);
    private sealed record ExpHistogramPoint(
        PointCommon C, ulong Count, double? Sum, double? Min, double? Max, int Scale, ulong ZeroCount, int PositiveOffset,
        string PositiveCounts, int NegativeOffset, string NegativeCounts, string Temporality, string Exemplars);
    private sealed record SummaryPoint(PointCommon C, ulong Count, double Sum, string Quantiles);

    private sealed class MetricRows
    {
        public readonly List<GaugePoint> Gauge = [];
        public readonly List<SumPoint> Sum = [];
        public readonly List<HistogramPoint> Histogram = [];
        public readonly List<ExpHistogramPoint> ExpHistogram = [];
        public readonly List<SummaryPoint> Summary = [];
        public readonly MetricDerived Derived = new();
    }

    private static void AppendCommon(PointCommon c, IDuckDBAppenderRow row)
        => row.AppendValue(c.TenantId).AppendValue(c.Service).AppendValue(c.MetricName).AppendValue(c.SeriesId)
            .AppendValue(c.Time).AppendValue(c.StartTime).AppendValue(c.Unit).AppendValue(c.Description).AppendValue(c.Flags)
            .AppendValue(c.AttributesJson).AppendValue(c.Resource.AttributesJson).AppendValue(c.Resource.SchemaUrl)
            .AppendValue(c.Scope.Name).AppendValue(c.Scope.Version).AppendValue(c.Scope.AttributesJson).AppendValue(c.Scope.SchemaUrl);

    private static void AppendGauge(GaugePoint p, IDuckDBAppenderRow row)
    {
        AppendCommon(p.C, row);
        row.AppendValue(p.Value).AppendValue(p.Exemplars);
    }

    private static void AppendSum(SumPoint p, IDuckDBAppenderRow row)
    {
        AppendCommon(p.C, row);
        row.AppendValue(p.Value).AppendValue(p.Temporality).AppendValue(p.IsMonotonic).AppendValue(p.Exemplars);
    }

    private static void AppendHistogram(HistogramPoint p, IDuckDBAppenderRow row)
    {
        AppendCommon(p.C, row);
        row.AppendValue(p.Count).AppendValue(p.Sum).AppendValue(p.Min).AppendValue(p.Max)
            .AppendValue(p.BucketCounts).AppendValue(p.Bounds).AppendValue(p.Temporality).AppendValue(p.Exemplars);
    }

    private static void AppendExpHistogram(ExpHistogramPoint p, IDuckDBAppenderRow row)
    {
        AppendCommon(p.C, row);
        row.AppendValue(p.Count).AppendValue(p.Sum).AppendValue(p.Min).AppendValue(p.Max).AppendValue(p.Scale).AppendValue(p.ZeroCount)
            .AppendValue(0d).AppendValue(p.PositiveOffset).AppendValue(p.PositiveCounts).AppendValue(p.NegativeOffset).AppendValue(p.NegativeCounts)
            .AppendValue(p.Temporality).AppendValue(p.Exemplars);
    }

    private static void AppendSummary(SummaryPoint p, IDuckDBAppenderRow row)
    {
        AppendCommon(p.C, row);
        row.AppendValue(p.Count).AppendValue(p.Sum).AppendValue(p.Quantiles);
    }

    private static void AddMetric(MetricModel metric, FlushContext context, MetricRows rows)
    {
        var resource = context.Resource(metric.Resource);
        var scope = context.Scope(metric.InstrumentationScope);
        var prefix = ParquetIds.SeriesPrefix(resource.TenantId, resource.Key, scope.Key, metric.Name, metric.Type);
        var unit = metric.Unit ?? "";
        var description = metric.Description ?? "";
        var catalogKey = (ulong)ParquetIds.MetricId(resource.TenantId, resource.ServiceName, metric.Name, metric.Type);

        PointCommon Common(Dictionary<string, object>? pointAttributes, long time, long? start, int flags)
        {
            var seriesId = ParquetIds.SeriesId(prefix, pointAttributes);
            var json = ParquetAttributes.ToJson(pointAttributes);
            rows.Derived.Note(catalogKey, seriesId, resource, scope, metric, unit, description, json, Math.Max(0L, time));
            return new PointCommon(
                resource.TenantId, resource.ServiceName, metric.Name, seriesId, Math.Max(0L, time), Math.Max(0L, start ?? 0), unit, description,
                ToUInt64(flags), json, resource, scope);
        }

        switch (metric.Type)
        {
            case MetricType.GAUGE when metric.GaugeDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    rows.Gauge.Add(new GaugePoint(Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags), d.ValueDouble ?? d.ValueInt ?? 0d, ExemplarsJson(d.Exemplars)));
                break;
            case MetricType.SUM when metric.SumDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    rows.Sum.Add(new SumPoint(Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags), d.ValueDouble ?? d.ValueInt ?? 0d,
                        d.AggregationTemporality.ToString(), d.IsMonotonic, ExemplarsJson(d.Exemplars)));
                break;
            case MetricType.HISTOGRAM when metric.HistogramDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    rows.Histogram.Add(new HistogramPoint(Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags), ToUInt64(d.Count), d.Sum, d.Min, d.Max,
                        ArrayJson(d.BucketCounts), ArrayJson(d.ExplicitBounds), d.AggregationTemporality.ToString(), ExemplarsJson(d.Exemplars)));
                break;
            case MetricType.EXPONENTIAL_HISTOGRAM when metric.ExponentialHistogramDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    rows.ExpHistogram.Add(new ExpHistogramPoint(Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags), ToUInt64(d.Count), d.Sum, d.Min, d.Max,
                        d.Scale, ToUInt64(d.ZeroCount), d.PositiveOffset ?? 0, ArrayJson(d.PositiveBucketCounts), d.NegativeOffset ?? 0,
                        ArrayJson(d.NegativeBucketCounts), d.AggregationTemporality.ToString(), ExemplarsJson(d.Exemplars)));
                break;
            case MetricType.SUMMARY when metric.SummaryDataPoints is { Count: > 0 } points:
                foreach (var d in points)
                    rows.Summary.Add(new SummaryPoint(Common(d.Attributes, d.TimeUnixNano, d.StartTimeUnixNano, d.Flags), ToUInt64(d.Count), d.Sum,
                        QuantilesJson(d.QuantileValues)));
                break;
        }
    }

    // ---- nested values as JSON text (the appender cannot write lists of structs, phase 0 spike 1) ----

    private static double Finite(double value) => double.IsFinite(value) ? value : 0d;

    private static string ArrayJson(long[]? values)
        => values is not { Length: > 0 } ? "[]" : JsonSerializer.Serialize(values.Select(ToUInt64));

    private static string ArrayJson(double[]? values)
        => values is not { Length: > 0 } ? "[]" : JsonSerializer.Serialize(values.Select(Finite));

    private static string ExemplarsJson(List<ExemplarModel>? exemplars)
    {
        if (exemplars is not { Count: > 0 }) return "[]";
        return JsonSerializer.Serialize(exemplars.Select(e =>
        {
            var (hi, lo) = ParquetIds.TraceId(e.TraceIdHex);
            return new
            {
                time_unix_nano = Math.Max(0L, e.TimeUnixNano), value = Finite(e.ValueDouble ?? e.ValueInt ?? 0d), trace_hi = hi, trace_lo = lo,
                span_id = ParquetIds.SpanId(e.SpanIdHex), filtered_attributes = ParquetAttributes.ToMap(e.FilteredAttributes)
            };
        }));
    }

    private static string QuantilesJson(List<QuantileValueModel>? quantiles)
        => quantiles is not { Count: > 0 } ? "[]" : JsonSerializer.Serialize(quantiles.Select(q => new { quantile = Finite(q.Quantile), value = Finite(q.Value) }));

    // =========================================================================
    // CATALOG AND SERIES
    // =========================================================================

    /// <summary>
    /// Remembers when each series (or metric) was last written, so a row is written the first time it is seen and then at most every
    /// <see cref="ParquetOptions.CatalogRefreshSeconds"/> while it keeps reporting. A key is marked only after its write succeeded.
    /// Bounded by <see cref="ParquetOptions.MaxTrackedSeries"/>: past it, expired entries go first, then everything is forgotten.
    /// </summary>
    internal sealed class CatalogTracker(TimeProvider time, ParquetOptions options)
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, long> _written = new();

        public void Clear() => _written.Clear();

        public bool IsDue(ulong key)
            => !_written.TryGetValue(key, out var at) || time.GetUtcNow().Ticks - at >= TimeSpan.FromSeconds(options.CatalogRefreshSeconds).Ticks;

        public void MarkWritten(IEnumerable<ulong> keys)
        {
            var now = time.GetUtcNow().Ticks;
            foreach (var key in keys) _written[key] = now;
            if (_written.Count <= options.MaxTrackedSeries) return;

            var expiry = TimeSpan.FromSeconds(options.CatalogRefreshSeconds).Ticks;
            foreach (var (key, at) in _written)
                if (now - at >= expiry) _written.TryRemove(key, out _);
            if (_written.Count > options.MaxTrackedSeries) _written.Clear();
        }
    }

    private sealed record CatalogRow(long TenantId, string Service, string Name, string Type, string Unit, string Description, long First, long Last);

    private sealed record SeriesRow(
        long TenantId, string Service, string Metric, ulong SeriesId, string AttributesJson, string ResourceJson, string ScopeName, string ScopeVersion,
        long First, long Last);

    private static void AppendCatalog(CatalogRow r, IDuckDBAppenderRow row)
        => row.AppendValue(r.TenantId).AppendValue(r.Service).AppendValue(r.Name).AppendValue(r.Type).AppendValue(r.Unit)
            .AppendValue(r.Description).AppendValue(r.First).AppendValue(r.Last);

    private static void AppendSeries(SeriesRow r, IDuckDBAppenderRow row)
        => row.AppendValue(r.TenantId).AppendValue(r.Service).AppendValue(r.Metric).AppendValue(r.SeriesId).AppendValue(r.AttributesJson)
            .AppendValue(r.ResourceJson).AppendValue(r.ScopeName).AppendValue(r.ScopeVersion).AppendValue(r.First).AppendValue(r.Last);

    /// <summary>The catalog and series rows a metrics batch makes due.</summary>
    private sealed class MetricDerived
    {
        private sealed record Catalog(ResourceInfo Resource, MetricModel Metric, string Unit, string Description, long First, long Last);
        private sealed record Series(ResourceInfo Resource, ScopeInfo Scope, string Metric, string AttributesJson, long First, long Last);

        private readonly Dictionary<ulong, Catalog> _catalog = [];
        private readonly Dictionary<ulong, Series> _series = [];

        public void Note(ulong catalogKey, ulong seriesId, ResourceInfo resource, ScopeInfo scope, MetricModel metric,
            string unit, string description, string attributesJson, long time)
        {
            _catalog[catalogKey] = _catalog.TryGetValue(catalogKey, out var c)
                ? c with { First = Math.Min(c.First, time), Last = Math.Max(c.Last, time) }
                : new Catalog(resource, metric, unit, description, time, time);
            _series[seriesId] = _series.TryGetValue(seriesId, out var s)
                ? s with { First = Math.Min(s.First, time), Last = Math.Max(s.Last, time) }
                : new Series(resource, scope, metric.Name, attributesJson, time, time);
        }

        public async Task WriteAsync(ParquetBulkWriter writer, string token, CancellationToken ct)
        {
            var catalogRows = new List<CatalogRow>();
            var catalogKeys = new List<ulong>();
            foreach (var (key, c) in _catalog)
            {
                if (!writer._metricTracker.IsDue(key)) continue;
                catalogKeys.Add(key);
                catalogRows.Add(new CatalogRow(c.Resource.TenantId, c.Resource.ServiceName, c.Metric.Name, c.Metric.Type.ToString(), c.Unit, c.Description, c.First, c.Last));
            }

            var seriesRows = new List<SeriesRow>();
            var seriesKeys = new List<ulong>();
            foreach (var (id, s) in _series)
            {
                if (!writer._seriesTracker.IsDue(id)) continue;
                seriesKeys.Add(id);
                seriesRows.Add(new SeriesRow(s.Resource.TenantId, s.Resource.ServiceName, s.Metric, id, s.AttributesJson, s.Resource.AttributesJson,
                    s.Scope.Name, s.Scope.Version, s.First, s.Last));
            }

            if (catalogRows.Count > 0 && await writer.WriteDerivedAsync(ParquetTables.MetricCatalogTable, catalogRows, r => r.TenantId, r => r.Last, AppendCatalog, token, ct))
                writer._metricTracker.MarkWritten(catalogKeys);
            if (seriesRows.Count > 0 && await writer.WriteDerivedAsync(ParquetTables.MetricSeriesTable, seriesRows, r => r.TenantId, r => r.Last, AppendSeries, token, ct))
                writer._seriesTracker.MarkWritten(seriesKeys);
        }
    }
}
