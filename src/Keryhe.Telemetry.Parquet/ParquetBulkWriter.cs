using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using static Keryhe.Telemetry.Core.Data.TelemetryIngestionHelpers;

namespace Keryhe.Telemetry.Parquet;

/// <summary>The token-taking write seam (the ClickHouse R2 shape): the ingestion worker (phase 2) reuses a token on retry.</summary>
internal interface IParquetTokenWriter : IPartitionedTokenWriter;

/// <summary>
/// Parquet implementation of <see cref="ITelemetryBulkWriter"/> for the row model in the plan's README. Every row
/// carries its own resource and scope (no reference tables, no <c>ResourceScopeCache</c>), copied onto each span as the
/// ClickHouse writer does; attributes are <see cref="ParquetAttributes"/>, ids <see cref="ParquetIds"/>.
///
/// <b>Tokens.</b> A flush groups its rows by (tenant, UTC date, hour) of the table's partition time and writes one file per
/// partition, named by <c>part-&lt;writer&gt;-&lt;seq&gt;-&lt;token&gt;.parquet</c> through <see cref="ManifestWriter"/>: a retry under
/// the same token replaces the file it wrote before, so nothing is stored twice. The interface methods mint a fresh
/// token per call. A flush is complete when every file is committed and every manifest updated; one that fails
/// partway leaves the already committed partitions in place and the retry rewrites them under the same names.
/// </summary>
public sealed partial class ParquetBulkWriter(
    IParquetFileStore store,
    ParquetFileWriter fileWriter,
    ManifestWriter manifests,
    ILogger<ParquetBulkWriter> logger,
    IngestionMetrics? metrics = null,
    IOptions<TelemetryIngestionOptions>? sharedOptions = null,
    IOptions<ParquetOptions>? parquetOptions = null,
    TimeProvider? time = null) : ITelemetryBulkWriter, IParquetTokenWriter
{
    private readonly ParquetOptions _parquet = parquetOptions?.Value ?? new ParquetOptions();
    private readonly CatalogTracker _seriesTracker = new(time ?? TimeProvider.System, parquetOptions?.Value ?? new ParquetOptions());
    private readonly CatalogTracker _metricTracker = new(time ?? TimeProvider.System, parquetOptions?.Value ?? new ParquetOptions());

    /// <summary>Forgets which catalog and series rows were written, so the next flush writes them again. For tests that delete the files.</summary>
    internal void ResetCatalogTrackers() { _seriesTracker.Clear(); _metricTracker.Clear(); }

    public Task FlushTracesAsync(List<SpanModel> spans, CancellationToken cancellationToken = default)
        => FlushTracesAsync(spans, NewToken(), cancellationToken);

    public async Task FlushTracesAsync(List<SpanModel> spans, string token, CancellationToken ct = default)
    {
        if (spans.Count == 0) return;
        ValidateToken(token);

        var context = new FlushContext();
        var rows = new List<SpanRow>(spans.Count);
        foreach (var span in spans) rows.Add(BuildSpanRow(span, context));

        var partitions = await WriteRowsAsync(ParquetTables.SpanTable, rows, r => r.TenantId, r => r.StartTimeUnixNano, AppendSpan, token, ct);

        // Derived data follows the raw files (README D8, phase 3 decision 1). A derived failure leaves the spans; it is
        // retried under the same token and, past the retries, counted (the accepted under-count of the row model).
        await WriteDerivedAsync(ParquetTables.TraceIndexTable, TraceIndexRows(rows), r => r.TenantId, r => r.StartMin, AppendTraceIndex, token, ct);
        await WriteDerivedAsync(ParquetTables.RequestRollupTable, RequestRollupRows(rows), r => r.TenantId, r => r.Minute, AppendRequestRollup, token, ct);
        logger.LogDebug("Flushed {SpanCount} spans into {Partitions} partitions", spans.Count, partitions);
    }

    /// <summary>Groups rows by (tenant, UTC hour of the partition time) and writes one file per group; returns the group count.</summary>
    private async Task<int> WriteRowsAsync<T>(
        ParquetTable table, List<T> rows, Func<T, long> tenantOf, Func<T, long> timeOf, Action<T, IDuckDBAppenderRow> append,
        string token, CancellationToken ct)
    {
        var groups = new Dictionary<ParquetPartition, List<T>>();
        foreach (var row in rows)
        {
            var partition = ParquetPartition.For(table.Name, tenantOf(row), timeOf(row));
            if (!groups.TryGetValue(partition, out var list)) groups[partition] = list = [];
            list.Add(row);
        }

        foreach (var (partition, group) in groups.OrderBy(g => g.Key.Date).ThenBy(g => g.Key.Hour).ThenBy(g => g.Key.TenantId))
        {
            ct.ThrowIfCancellationRequested();
            await WritePartitionAsync(table, partition, group, append, token, timeOf, ct);
        }
        return groups.Count;
    }

    /// <summary>Writes a derived table's rows, retried under the flush's token; false when they were dropped after the retries.</summary>
    private async Task<bool> WriteDerivedAsync<T>(
        ParquetTable table, List<T> rows, Func<T, long> tenantOf, Func<T, long> timeOf, Action<T, IDuckDBAppenderRow> append,
        string token, CancellationToken ct)
    {
        if (rows.Count == 0) return true;
        var shared = sharedOptions?.Value ?? new TelemetryIngestionOptions();
        var attempt = 0;
        while (true)
        {
            try
            {
                await WriteRowsAsync(table, rows, tenantOf, timeOf, append, token, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > shared.MaxFlushRetries)
                {
                    logger.LogError(ex, "Derived write to {Table} failed after {Attempts} attempts; {Count} derived rows dropped", table.Name, attempt, rows.Count);
                    metrics?.RecordDerivedRowsDropped(table.Name, rows.Count);
                    return false;
                }
                var delay = Math.Min(shared.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1), shared.RetryMaxDelayMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(delay * (0.5 + Random.Shared.NextDouble() * 0.5)), ct);
            }
        }
    }

    private async Task WritePartitionAsync<T>(
        ParquetTable table, ParquetPartition partition, List<T> rows, Action<T, IDuckDBAppenderRow> append, string token,
        Func<T, long> partitionTime, CancellationToken ct)
    {
        var temp = store.NewTempPath(partition, manifests.WriterId, $"{token}.parquet");
        try
        {
            var bytes = await fileWriter.WriteAsync(table, rows, append, temp, ct);
            long min = long.MaxValue, max = long.MinValue;
            foreach (var row in rows)
            {
                var t = partitionTime(row);
                if (t < min) min = t;
                if (t > max) max = t;
            }
            await manifests.CommitAsync(partition, temp, token, rows.Count, bytes, min, max, ct);
        }
        catch
        {
            // A temp file that was never committed (committing moves it away).
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    // =========================================================================
    // SPAN ROWS
    // =========================================================================

    /// <summary>A span with everything the staging table needs, built once so the partition grouping and the append share it.</summary>
    private sealed record SpanRow(
        long TenantId, string ServiceName, string Name, long StartTimeUnixNano, long EndTimeUnixNano, long DurationNs,
        ulong TraceHi, ulong TraceLo, ulong SpanId, ulong ParentSpanId, string TraceState, ulong Flags, string Kind,
        string StatusCode, string StatusMessage, string AttributesJson, ulong DroppedAttributes, string EventsJson,
        ulong DroppedEvents, string LinksJson, ulong DroppedLinks, ResourceInfo Resource, ScopeInfo Scope);

    private static SpanRow BuildSpanRow(SpanModel span, FlushContext context)
    {
        var resource = context.Resource(span.Resource);
        var scope = context.Scope(span.InstrumentationScope);
        var (hi, lo) = ParquetIds.TraceId(span.TraceIdHex);

        return new SpanRow(
            resource.TenantId, resource.ServiceName, span.Name ?? "", span.StartTimeUnixNano, span.EndTimeUnixNano,
            Math.Max(0L, span.EndTimeUnixNano - span.StartTimeUnixNano), hi, lo,
            ParquetIds.SpanId(span.SpanIdHex), ParquetIds.SpanId(span.ParentSpanIdHex), span.TraceState ?? "",
            ToUInt64(span.Flags), span.Kind.ToString(), span.StatusCode.ToString(), span.StatusMessage ?? "",
            ParquetAttributes.ToJson(span.Attributes), ToUInt64(span.DroppedAttributesCount),
            EventsJson(span.Events), ToUInt64(span.DroppedEventsCount),
            LinksJson(span.Links), ToUInt64(span.DroppedLinksCount), resource, scope);
    }

    private static void AppendSpan(SpanRow s, IDuckDBAppenderRow row)
    {
        // The order is ParquetTables.SpanTable's.
        row.AppendValue(s.TenantId).AppendValue(s.ServiceName).AppendValue(s.Name)
            .AppendValue(s.StartTimeUnixNano).AppendValue(s.EndTimeUnixNano).AppendValue(s.DurationNs)
            .AppendValue(s.TraceHi).AppendValue(s.TraceLo).AppendValue(s.SpanId).AppendValue(s.ParentSpanId)
            .AppendValue(s.TraceState).AppendValue(s.Flags).AppendValue(s.Kind).AppendValue(s.StatusCode).AppendValue(s.StatusMessage)
            .AppendValue(s.AttributesJson).AppendValue(s.DroppedAttributes)
            .AppendValue(s.EventsJson).AppendValue(s.DroppedEvents)
            .AppendValue(s.LinksJson).AppendValue(s.DroppedLinks)
            .AppendValue(s.Resource.AttributesJson).AppendValue(s.Resource.SchemaUrl)
            .AppendValue(s.Scope.Name).AppendValue(s.Scope.Version).AppendValue(s.Scope.AttributesJson).AppendValue(s.Scope.SchemaUrl);
    }

    private static string EventsJson(List<SpanEventModel> events)
    {
        if (events.Count == 0) return "[]";
        var items = new object[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            items[i] = new
            {
                time_unix_nano = e.TimeUnixNano, name = e.Name ?? "", attributes = ParquetAttributes.ToMap(e.Attributes),
                dropped_attributes_count = ToUInt64(e.DroppedAttributesCount)
            };
        }
        return JsonSerializer.Serialize(items);
    }

    private static string LinksJson(List<SpanLinkModel> links)
    {
        if (links.Count == 0) return "[]";
        var items = new object[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            var l = links[i];
            var (hi, lo) = ParquetIds.TraceId(l.LinkedTraceIdHex);
            items[i] = new
            {
                trace_hi = hi, trace_lo = lo, span_id = ParquetIds.SpanId(l.LinkedSpanIdHex), trace_state = l.TraceState ?? "",
                flags = ToUInt64(l.Flags), attributes = ParquetAttributes.ToMap(l.Attributes),
                dropped_attributes_count = ToUInt64(l.DroppedAttributesCount)
            };
        }
        return JsonSerializer.Serialize(items);
    }

    // =========================================================================
    // DERIVED ROWS
    // =========================================================================

    private const long MinuteNanos = 60_000_000_000L;

    private static long MinuteOf(long unixNano)
    {
        var r = unixNano % MinuteNanos;
        return unixNano - (r < 0 ? r + MinuteNanos : r);
    }

    private sealed record TraceIndexRow(long TenantId, ulong TraceHi, ulong TraceLo, string Service, long StartMin, long EndMax, long SpanCount, byte HasError);

    /// <summary>One row per (tenant, trace, service, hour of the span's start), so a trace across an hour boundary gets a row per hour.</summary>
    private static List<TraceIndexRow> TraceIndexRows(List<SpanRow> spans)
    {
        var folds = new Dictionary<(long Tenant, ulong Hi, ulong Lo, string Service, long Hour), (long Min, long Max, long Count, byte Error)>();
        foreach (var s in spans)
        {
            var start = Math.Max(0L, s.StartTimeUnixNano);
            var end = Math.Max(start, s.EndTimeUnixNano);
            var key = (s.TenantId, s.TraceHi, s.TraceLo, s.ServiceName, start / ParquetIngestionWorker.NanosPerHour);
            var error = (byte)(s.StatusCode == nameof(SpanStatusCode.ERROR) ? 1 : 0);
            folds[key] = folds.TryGetValue(key, out var f)
                ? (Math.Min(f.Min, start), Math.Max(f.Max, end), f.Count + 1, Math.Max(f.Error, error))
                : (start, end, 1L, error);
        }
        return folds.Select(kv => new TraceIndexRow(kv.Key.Tenant, kv.Key.Hi, kv.Key.Lo, kv.Key.Service, kv.Value.Min, kv.Value.Max, kv.Value.Count, kv.Value.Error)).ToList();
    }

    private static void AppendTraceIndex(TraceIndexRow r, IDuckDBAppenderRow row)
        => row.AppendValue(r.TenantId).AppendValue(r.TraceHi).AppendValue(r.TraceLo).AppendValue(r.Service)
            .AppendValue(r.StartMin).AppendValue(r.EndMax).AppendValue(r.SpanCount).AppendValue(r.HasError);

    private sealed class RequestFold
    {
        public long Requests, Errors, SumDuration, MaxDuration;
        public readonly long[] Bands = new long[DurationBands.Count];
    }

    private sealed record RequestRollupRow(long TenantId, string Service, long Minute, string Operation, RequestFold Fold);

    /// <summary>Inbound spans (SERVER, CONSUMER) per (tenant, service, minute, operation).</summary>
    private static List<RequestRollupRow> RequestRollupRows(List<SpanRow> spans)
    {
        var folds = new Dictionary<(long Tenant, string Service, long Minute, string Operation), RequestFold>();
        foreach (var s in spans)
        {
            if (s.Kind != nameof(SpanKind.SERVER) && s.Kind != nameof(SpanKind.CONSUMER)) continue;
            var key = (s.TenantId, s.ServiceName, MinuteOf(s.StartTimeUnixNano), s.Name);
            if (!folds.TryGetValue(key, out var fold)) folds[key] = fold = new RequestFold();
            var duration = s.DurationNs;
            fold.Requests++;
            if (s.StatusCode == nameof(SpanStatusCode.ERROR)) fold.Errors++;
            fold.SumDuration += duration;
            if (duration > fold.MaxDuration) fold.MaxDuration = duration;
            fold.Bands[DurationBands.IndexOf(duration)]++;
        }
        return folds.Select(kv => new RequestRollupRow(kv.Key.Tenant, kv.Key.Service, kv.Key.Minute, kv.Key.Operation, kv.Value)).ToList();
    }

    private static void AppendRequestRollup(RequestRollupRow r, IDuckDBAppenderRow row)
    {
        row.AppendValue(r.TenantId).AppendValue(r.Service).AppendValue(r.Minute).AppendValue(r.Operation)
            .AppendValue(r.Fold.Requests).AppendValue(r.Fold.Errors).AppendValue(r.Fold.SumDuration).AppendValue(r.Fold.MaxDuration);
        foreach (var band in r.Fold.Bands) row.AppendValue(band);
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    internal static ulong ToUInt64(long value) => (ulong)Math.Max(0L, value);

    private static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>The token becomes part of a file name, so it must be a plain one.</summary>
    private static void ValidateToken(string token)
    {
        if (token.Length is 0 or > 100 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("A flush token must be 1-100 letters, digits, '-' or '_'.", nameof(token));
    }

    private sealed record ResourceInfo(long TenantId, string ServiceName, string SchemaUrl, string AttributesJson, string Key);

    private sealed record ScopeInfo(string Name, string Version, string SchemaUrl, string AttributesJson, string Key);

    /// <summary>
    /// Converts each distinct resource and scope instance once per flush (the gRPC services share one instance across
    /// every record of a block), so a span costs a lookup, not an attribute serialization.
    /// </summary>
    private sealed class FlushContext
    {
        private readonly Dictionary<ResourceModel, ResourceInfo> _resources = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<InstrumentationScopeModel, ScopeInfo> _scopes = new(ReferenceEqualityComparer.Instance);

        public ResourceInfo Resource(ResourceModel? resource)
        {
            if (resource is not null && _resources.TryGetValue(resource, out var cached)) return cached;

            var model = NormalizeResource(resource);
            var info = new ResourceInfo(
                model.TenantId, ExtractServiceName(model.Attributes) ?? "", model.SchemaUrl ?? "",
                ParquetAttributes.ToJson(model.Attributes), HashResource(model));
            if (resource is not null) _resources[resource] = info;
            return info;
        }

        public ScopeInfo Scope(InstrumentationScopeModel? scope)
        {
            if (scope is not null && _scopes.TryGetValue(scope, out var cached)) return cached;

            var model = NormalizeScope(scope);
            var info = new ScopeInfo(
                model.Name ?? "", model.Version ?? "", model.SchemaUrl ?? "", ParquetAttributes.ToJson(model.Attributes), HashScope(model));
            if (scope is not null) _scopes[scope] = info;
            return info;
        }
    }
}
