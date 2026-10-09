using System.Collections.Concurrent;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// The derived tables (<c>trace_index</c>, both rollups, <c>metric_catalog</c>, <c>metric_series</c>), written by the
/// flush itself after its raw insert succeeded (plans/clickhouse-redesign README R1, phase 3). No materialized views.
/// Each derived insert's token is the raw batch's token plus the table name, so a retry of the whole flush neither
/// double-counts a rollup nor doubles a <c>trace_index</c> row. A derived insert that fails after the raw rows landed
/// is retried by itself; if it still fails, the raw rows stay, the derived rows for the batch are lost and counted on
/// <c>derived_rows_dropped</c> (the row model's accepted under-count).
/// </summary>
public sealed partial class ClickHouseBulkWriter
{
    /// <summary>Forgets which catalog and series rows were written, so the next flush writes them again. For tests that truncate the tables.</summary>
    internal void ResetCatalogTrackers() { _seriesTracker.Clear(); _metricTracker.Clear(); }

    private const long MinuteNanos = 60_000_000_000L;

    private Task InsertDerivedAsync(string table, string[] columns, List<object?[]> rows, string token, CancellationToken ct)
        => rows.Count == 0 ? Task.CompletedTask : TryInsertDerivedAsync(table, columns, rows, token, ct);

    /// <summary>Whether the insert of <paramref name="rows"/> into a derived table succeeded (false: dropped, nothing to remember).</summary>
    private async Task<bool> TryInsertDerivedAsync(string table, string[] columns, List<object?[]> rows, string token, CancellationToken ct)
    {
        var dropped = false;
        var attempt = 0;
        while (true)
        {
            try
            {
                await BulkInsertAsync(table, columns, rows, token, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > _shared.MaxFlushRetries)
                {
                    logger.LogError(ex, "Derived insert into {Table} failed after {Attempts} attempts; {Count} derived rows dropped", table, attempt, rows.Count);
                    _metrics?.RecordDerivedRowsDropped(table, rows.Count);
                    dropped = true;
                    break;
                }
                var delay = Math.Min(_shared.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1), _shared.RetryMaxDelayMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(delay * (0.5 + Random.Shared.NextDouble() * 0.5)), ct);
            }
        }
        return !dropped;
    }

    // =========================================================================
    // LOG TIMES
    // =========================================================================

    /// <summary>A log's partition time and observed time: a missing timestamp falls back to the observed one, then to now.</summary>
    private static (long Time, long Observed) LogTimes(LogRecordModel r, long nowNano)
    {
        var observed = r.ObservedTimeUnixNano is > 0 ? r.ObservedTimeUnixNano.Value : 0L;
        var time = r.TimeUnixNano is > 0 ? r.TimeUnixNano.Value : observed;
        if (time == 0) time = observed = nowNano;
        if (observed == 0) observed = time;
        return (time, observed);
    }

    private static long MinuteOf(long unixNano)
    {
        var r = unixNano % MinuteNanos;
        return unixNano - (r < 0 ? r + MinuteNanos : r);
    }

    // =========================================================================
    // TRACE INDEX
    // =========================================================================

    private static readonly string[] TraceIndexColumns =
        ["tenant_id", "trace_id", "service_name", "day", "start_min", "end_max", "span_count", "has_error"];

    /// <summary>
    /// One row per (tenant, trace, service, day) in the batch, folded in C#. The day is the span's own partition day, so
    /// a trace across midnight gets a row per day.
    /// </summary>
    private static List<object?[]> TraceIndexRows(List<SpanModel> spans, FlushContext context)
    {
        var folds = new Dictionary<(long Tenant, Guid Trace, string Service, DateTime Day), (long Min, long Max, ulong Count, byte Error)>();
        foreach (var span in spans)
        {
            var resource = context.Resource(span.Resource);
            var start = Math.Max(0L, span.StartTimeUnixNano);
            var end = Math.Max(start, span.EndTimeUnixNano);
            var key = (resource.TenantId, ClickHouseIds.TraceIdToGuid(span.TraceIdHex), resource.ServiceName, ToDateTime(start).Date);
            var error = (byte)(span.StatusCode == SpanStatusCode.ERROR ? 1 : 0);
            folds[key] = folds.TryGetValue(key, out var f)
                ? (Math.Min(f.Min, start), Math.Max(f.Max, end), f.Count + 1, Math.Max(f.Error, error))
                : (start, end, 1UL, error);
        }

        var rows = new List<object?[]>(folds.Count);
        foreach (var (key, f) in folds)
            rows.Add([(ulong)key.Tenant, key.Trace, key.Service, key.Day, ToDateTime(f.Min), ToDateTime(f.Max), f.Count, f.Error]);
        return rows;
    }

    // =========================================================================
    // ROLLUPS
    // =========================================================================

    private static readonly string[] RequestRollupColumns =
    [
        "tenant_id", "service_name", "bucket_start_unix_nano", "operation", "request_count", "error_count",
        "sum_duration_nanos", "max_duration_nanos",
        .. Enumerable.Range(0, DurationBands.Count).Select(i => $"h{i:00}")
    ];

    /// <summary>Inbound spans (SERVER, CONSUMER) per (tenant, service, minute, operation), with the shared duration bands.</summary>
    private static List<object?[]> RequestRollupRows(List<SpanModel> spans, FlushContext context)
    {
        var folds = new Dictionary<(long Tenant, string Service, long Minute, string Operation), RequestFold>();
        foreach (var span in spans)
        {
            if (span.Kind != SpanKind.SERVER && span.Kind != SpanKind.CONSUMER) continue;
            var resource = context.Resource(span.Resource);
            var key = (resource.TenantId, resource.ServiceName, MinuteOf(span.StartTimeUnixNano), span.Name ?? "");
            if (!folds.TryGetValue(key, out var fold)) folds[key] = fold = new RequestFold();

            var duration = Math.Max(0L, span.EndTimeUnixNano - span.StartTimeUnixNano);
            fold.Requests++;
            if (span.StatusCode == SpanStatusCode.ERROR) fold.Errors++;
            fold.SumDuration += duration;
            if (duration > fold.MaxDuration) fold.MaxDuration = duration;
            fold.Bands[DurationBands.IndexOf(duration)]++;
        }

        var rows = new List<object?[]>(folds.Count);
        foreach (var (key, fold) in folds)
        {
            var row = new object?[8 + DurationBands.Count];
            row[0] = (ulong)key.Tenant; row[1] = key.Service; row[2] = key.Minute; row[3] = key.Operation;
            row[4] = fold.Requests; row[5] = fold.Errors; row[6] = fold.SumDuration; row[7] = fold.MaxDuration;
            for (var i = 0; i < DurationBands.Count; i++) row[8 + i] = fold.Bands[i];
            rows.Add(row);
        }
        return rows;
    }

    private sealed class RequestFold
    {
        public long Requests, Errors, SumDuration, MaxDuration;
        public readonly long[] Bands = new long[DurationBands.Count];
    }

    private static readonly string[] LogRollupColumns =
        ["tenant_id", "service_name", "bucket_start_unix_nano", "severity_number", "record_count"];

    /// <summary>Log records per (tenant, service, minute, severity); an unspecified severity is -1, as in the relational rollup.</summary>
    private static List<object?[]> LogRollupRows(List<LogRecordModel> records, FlushContext context, long nowNano)
    {
        var folds = new Dictionary<(long Tenant, string Service, long Minute, short Severity), long>();
        foreach (var r in records)
        {
            var resource = context.Resource(r.Resource);
            var key = (resource.TenantId, resource.ServiceName, MinuteOf(LogTimes(r, nowNano).Time), (short)(r.SeverityNumber ?? -1));
            folds[key] = folds.GetValueOrDefault(key) + 1;
        }
        return folds.Select(kv => new object?[] { (ulong)kv.Key.Tenant, kv.Key.Service, kv.Key.Minute, kv.Key.Severity, kv.Value }).ToList();
    }

    // =========================================================================
    // METRIC CATALOG AND SERIES
    // =========================================================================

    private static readonly string[] CatalogColumns =
        ["tenant_id", "service_name", "metric_name", "metric_type", "unit", "description", "first_seen", "last_seen"];

    private static readonly string[] SeriesColumns =
    [
        "tenant_id", "service_name", "metric_name", "series_id", "attributes", "resource_attributes",
        "scope_name", "scope_version", "first_seen", "last_seen"
    ];

    /// <summary>
    /// Remembers when each series (or metric) was last written, so a row is written the first time it is seen and
    /// then at most every <see cref="ClickHouseIngestionOptions.CatalogRefreshSeconds"/> while it keeps reporting.
    /// A key is marked only after its insert succeeded. Bounded by <see cref="ClickHouseIngestionOptions.MaxTrackedSeries"/>:
    /// past it, expired entries go first, then everything is forgotten (a forgotten key is simply re-written).
    /// </summary>
    internal sealed class CatalogTracker(TimeProvider time, ClickHouseIngestionOptions options)
    {
        private readonly ConcurrentDictionary<ulong, long> _written = new();

        /// <summary>Forgets everything written (tests that truncate the tables).</summary>
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

    /// <summary>The catalog and series rows a metrics batch makes due.</summary>
    private sealed class MetricDerived
    {
        private sealed record Catalog(ResourceInfo Resource, MetricModel Metric, string Unit, string Description, long First, long Last);
        private sealed record Series(ResourceInfo Resource, ScopeInfo Scope, string Metric, Dictionary<string, string> Attributes, long First, long Last);

        private readonly Dictionary<ulong, Catalog> _catalog = [];
        private readonly Dictionary<ulong, Series> _series = [];

        public void Note(ulong catalogKey, ulong seriesId, ResourceInfo resource, ScopeInfo scope, MetricModel metric,
            string unit, string description, Dictionary<string, string> attributes, long time)
        {
            _catalog[catalogKey] = _catalog.TryGetValue(catalogKey, out var c)
                ? c with { First = Math.Min(c.First, time), Last = Math.Max(c.Last, time) }
                : new Catalog(resource, metric, unit, description, time, time);
            _series[seriesId] = _series.TryGetValue(seriesId, out var s)
                ? s with { First = Math.Min(s.First, time), Last = Math.Max(s.Last, time) }
                : new Series(resource, scope, metric.Name, attributes, time, time);
        }

        /// <summary>Folds another chunk's notes in: first/last are the min/max over both.</summary>
        public void Merge(MetricDerived other)
        {
            foreach (var (key, c) in other._catalog)
                _catalog[key] = _catalog.TryGetValue(key, out var mine)
                    ? mine with { First = Math.Min(mine.First, c.First), Last = Math.Max(mine.Last, c.Last) }
                    : c;
            foreach (var (id, s) in other._series)
                _series[id] = _series.TryGetValue(id, out var mine)
                    ? mine with { First = Math.Min(mine.First, s.First), Last = Math.Max(mine.Last, s.Last) }
                    : s;
        }

        public async Task WriteAsync(ClickHouseBulkWriter writer, string token, CancellationToken ct)
        {
            var catalogRows = new List<object?[]>();
            var catalogKeys = new List<ulong>();
            foreach (var (key, c) in _catalog)
            {
                if (!writer._metricTracker.IsDue(key)) continue;
                catalogKeys.Add(key);
                catalogRows.Add([(ulong)c.Resource.TenantId, c.Resource.ServiceName, c.Metric.Name, c.Metric.Type.ToString(),
                    c.Unit, c.Description, ToDateTime(c.First), ToDateTime(c.Last)]);
            }

            var seriesRows = new List<object?[]>();
            var seriesKeys = new List<ulong>();
            foreach (var (id, s) in _series)
            {
                if (!writer._seriesTracker.IsDue(id)) continue;
                seriesKeys.Add(id);
                seriesRows.Add([(ulong)s.Resource.TenantId, s.Resource.ServiceName, s.Metric, id, s.Attributes, s.Resource.Attributes,
                    s.Scope.Name, s.Scope.Version, ToDateTime(s.First), ToDateTime(s.Last)]);
            }

            if (catalogRows.Count > 0 && await writer.TryInsertDerivedAsync("metric_catalog", CatalogColumns, catalogRows, token, ct))
                writer._metricTracker.MarkWritten(catalogKeys);
            if (seriesRows.Count > 0 && await writer.TryInsertDerivedAsync("metric_series", SeriesColumns, seriesRows, token, ct))
                writer._seriesTracker.MarkWritten(seriesKeys);
        }
    }
}
