using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Per-collector in-memory accumulator behind the summary rollups (plans/summary-rollups.md,
/// decision 4). <c>TelemetryIngestionWorker</c> feeds it after a flush commits;
/// <c>RollupWorker</c> drains the minutes that have closed and appends them.
///
/// No lost increments: <see cref="AddSpans"/>/<see cref="AddLogs"/> fold a batch into a local
/// dictionary without a lock and then merge it into the shared state under one lock, and the
/// drains take the same lock. (The <see cref="MetricTouchTracker"/> swap-without-a-lock pattern is
/// deliberately not used: an add racing a swap would be a permanent under-count here.) Adds happen
/// once per flushed batch, not per span, so the lock is uncontended in practice.
/// </summary>
public sealed class RollupAccumulator(TimeProvider? timeProvider = null)
{
    private const long MinuteNanos = 60_000_000_000L;

    private readonly object _gate = new();
    private readonly SortedDictionary<long, Dictionary<(long Tenant, string Service), RequestRollupRow>> _requests = new();
    private readonly SortedDictionary<long, Dictionary<(long Tenant, string Service, int Severity), LogRollupRow>> _logs = new();

    /// <summary>False when the provider writes the rollup itself (ClickHouse, with each flush): adds are then skipped.</summary>
    public bool Enabled { get; set; } = true;

    public TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;

    /// <summary>Rows currently held (request + log), for tests and diagnostics.</summary>
    public int HeldRows
    {
        get
        {
            lock (_gate)
                return _requests.Values.Sum(d => d.Count) + _logs.Values.Sum(d => d.Count);
        }
    }

    private static long MinuteOf(long unixNano) => unixNano - Mod(unixNano, MinuteNanos);

    private static long Mod(long value, long modulus)
    {
        var r = value % modulus;
        return r < 0 ? r + modulus : r;
    }

    /// <summary>Counts the inbound (SERVER/CONSUMER) spans of a successfully flushed batch.</summary>
    public void AddSpans(IReadOnlyList<SpanModel> spans)
    {
        if (!Enabled || spans.Count == 0) return;

        var local = new Dictionary<(long Minute, long Tenant, string Service), RequestRollupRow>();
        var resources = new Dictionary<ResourceModel, (long Tenant, string Service)>(ReferenceEqualityComparer.Instance);
        foreach (var span in spans)
        {
            if (span.Kind != SpanKind.SERVER && span.Kind != SpanKind.CONSUMER) continue;

            var (tenant, service) = Resolve(span.Resource, resources);
            var minute = MinuteOf(span.StartTimeUnixNano);
            var key = (minute, tenant, service);
            if (!local.TryGetValue(key, out var row))
            {
                row = new RequestRollupRow { TenantId = tenant, ServiceName = service, BucketStartUnixNano = minute };
                local[key] = row;
            }

            var duration = Math.Max(0, span.EndTimeUnixNano - span.StartTimeUnixNano);
            row.RequestCount++;
            if (span.StatusCode == SpanStatusCode.ERROR) row.ErrorCount++;
            row.SumDurationNanos += duration;
            if (duration > row.MaxDurationNanos) row.MaxDurationNanos = duration;
            row.Bands[DurationBands.IndexOf(duration)]++;
        }

        if (local.Count == 0) return;
        lock (_gate)
        {
            foreach (var ((minute, tenant, service), add) in local)
            {
                if (!_requests.TryGetValue(minute, out var cells))
                    _requests[minute] = cells = new();
                if (!cells.TryGetValue((tenant, service), out var cell))
                {
                    cells[(tenant, service)] = add;
                    continue;
                }
                cell.RequestCount += add.RequestCount;
                cell.ErrorCount += add.ErrorCount;
                cell.SumDurationNanos += add.SumDurationNanos;
                if (add.MaxDurationNanos > cell.MaxDurationNanos) cell.MaxDurationNanos = add.MaxDurationNanos;
                for (var i = 0; i < add.Bands.Length; i++) cell.Bands[i] += add.Bands[i];
            }
        }
    }

    /// <summary>Counts the log records of a successfully flushed batch.</summary>
    public void AddLogs(IReadOnlyList<LogRecordModel> logs)
    {
        if (!Enabled || logs.Count == 0) return;

        var local = new Dictionary<(long Minute, long Tenant, string Service, int Severity), LogRollupRow>();
        var resources = new Dictionary<ResourceModel, (long Tenant, string Service)>(ReferenceEqualityComparer.Instance);
        foreach (var log in logs)
        {
            var (tenant, service) = Resolve(log.Resource, resources);
            var minute = MinuteOf(log.TimeUnixNano ?? 0L);
            var severity = log.SeverityNumber ?? -1;
            var key = (minute, tenant, service, severity);
            if (!local.TryGetValue(key, out var row))
            {
                row = new LogRollupRow { TenantId = tenant, ServiceName = service, SeverityNumber = severity, BucketStartUnixNano = minute };
                local[key] = row;
            }
            row.RecordCount++;
        }

        lock (_gate)
        {
            foreach (var ((minute, tenant, service, severity), add) in local)
            {
                if (!_logs.TryGetValue(minute, out var cells))
                    _logs[minute] = cells = new();
                if (cells.TryGetValue((tenant, service, severity), out var cell))
                    cell.RecordCount += add.RecordCount;
                else
                    cells[(tenant, service, severity)] = add;
            }
        }
    }

    /// <summary>
    /// Removes and returns the minutes whose end is at or before <paramref name="cutoffUnixNano"/>;
    /// open minutes stay. Rows come back sorted by (tenant, minute, service[, severity]).
    /// </summary>
    public (List<RequestRollupRow> Requests, List<LogRollupRow> Logs) DrainClosed(long cutoffUnixNano)
        => Drain(minute => minute + MinuteNanos <= cutoffUnixNano);

    /// <summary>Removes and returns everything, open minutes included (shutdown).</summary>
    public (List<RequestRollupRow> Requests, List<LogRollupRow> Logs) DrainAll() => Drain(_ => true);

    private (List<RequestRollupRow>, List<LogRollupRow>) Drain(Func<long, bool> take)
    {
        var requests = new List<RequestRollupRow>();
        var logs = new List<LogRollupRow>();
        lock (_gate)
        {
            foreach (var minute in _requests.Keys.Where(take).ToList())
            {
                requests.AddRange(_requests[minute].Values);
                _requests.Remove(minute);
            }
            foreach (var minute in _logs.Keys.Where(take).ToList())
            {
                logs.AddRange(_logs[minute].Values);
                _logs.Remove(minute);
            }
        }
        requests.Sort(static (a, b) => Compare(a.TenantId, a.BucketStartUnixNano, a.ServiceName, 0, b.TenantId, b.BucketStartUnixNano, b.ServiceName, 0));
        logs.Sort(static (a, b) => Compare(a.TenantId, a.BucketStartUnixNano, a.ServiceName, a.SeverityNumber, b.TenantId, b.BucketStartUnixNano, b.ServiceName, b.SeverityNumber));
        return (requests, logs);
    }

    /// <summary>The sort every append uses, so concurrent collectors always lock rows in the same order.</summary>
    internal static int Compare(long tenantA, long minuteA, string serviceA, int severityA,
                                long tenantB, long minuteB, string serviceB, int severityB)
    {
        var c = tenantA.CompareTo(tenantB);
        if (c != 0) return c;
        c = minuteA.CompareTo(minuteB);
        if (c != 0) return c;
        c = string.CompareOrdinal(serviceA, serviceB);
        return c != 0 ? c : severityA.CompareTo(severityB);
    }

    private static (long Tenant, string Service) Resolve(
        ResourceModel? resource, Dictionary<ResourceModel, (long, string)> cache)
    {
        if (resource != null && cache.TryGetValue(resource, out var hit)) return hit;
        var (tenant, service) = TelemetryIngestionHelpers.TenantAndService(resource);
        var value = (tenant, service ?? "");
        if (resource != null) cache[resource] = value;
        return value;
    }
}
