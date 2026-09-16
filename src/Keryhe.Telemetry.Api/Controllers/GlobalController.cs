using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.Api.Controllers;

/// <summary>
/// Cross-tenant read endpoints backing the Global Dashboard. Unlike every other controller
/// here, these deliberately ignore the <c>X-Tenant-Id</c> header: the client still sends it
/// (the Angular interceptor is global and stamps whichever tenant is selected), but a global
/// view must not be scoped by it.
/// </summary>
[ApiController]
[Route("api/global")]
public class GlobalController : ControllerBase
{
    /// <summary>
    /// How far back <c>GET /overview</c> looks for a tenant's last activity, independent of the
    /// dashboard's own time window — a tenant idle for weeks still needs a "last seen" to
    /// distinguish "silent" from "never reported". Bounded so the underlying MAX() stays a
    /// range scan.
    /// </summary>
    private static readonly TimeSpan ActivityLookback = TimeSpan.FromDays(30);

    private readonly ITenantCatalogRepository _tenants;
    private readonly ITraceReadRepository _traces;
    private readonly ILogReadRepository _logs;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<GlobalController> _logger;

    public GlobalController(
        ITenantCatalogRepository tenants,
        ITraceReadRepository traces,
        ILogReadRepository logs,
        ITenantContext tenantContext,
        ILogger<GlobalController> logger)
    {
        _tenants = tenants;
        _traces = traces;
        _logs = logs;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    // GET /api/global/overview?start=&end=&bucketCount=
    /// <summary>
    /// One card's worth of stats per tenant: RED metrics, window percentiles, a volume
    /// histogram for the sparkline, and last-seen.
    /// </summary>
    [HttpGet("overview")]
    public async Task<ActionResult<GlobalOverviewDto>> GetOverview(
        [FromQuery] DateTime start,
        [FromQuery] DateTime end,
        [FromQuery] int bucketCount = 24,
        CancellationToken ct = default)
    {
        // Validated here rather than left to the repository: GetTraceOverviewAsync throws
        // ArgumentException for an inverted range and there is no exception filter, so without
        // this the caller gets a 500 for what is plainly a bad request.
        if (start >= end)
            return BadRequest("Start time must be before end time.");

        var tenants = await _tenants.GetAllTenantsAsync(ct);
        var lastSeen = (await _tenants.GetTenantActivityAsync(DateTime.UtcNow - ActivityLookback, ct))
            .ToDictionary(a => a.TenantId, a => a.LastSeenUtc);

        var query = new HistogramQuery { Start = start, End = end, BucketCount = bucketCount };

        // One bucket, because the card wants window totals rather than a log time series. The log
        // histogram is a SQL GROUP BY aggregate (unlike the trace path, which materializes spans),
        // so this adds far less per tenant than the overview call it sits beside.
        var logQuery = new HistogramQuery { Start = start, End = end, BucketCount = 1 };

        var results = new List<GlobalTenantStatsDto>(tenants.Count);

        // Sequential by necessity, not by oversight. ApiTenantContext holds a single mutable
        // tenant id shared by every repository in this request scope, so the repositories read
        // whatever the last SetTenantId call wrote — running these concurrently would race and
        // attribute one tenant's traces to another. Parallelism would need a DI scope per
        // tenant (see AlertEvaluationWorker); this loop mirrors AlertService.EvaluateAllAsync.
        foreach (var tenant in tenants)
        {
            if (ct.IsCancellationRequested) break;

            lastSeen.TryGetValue(tenant.Id, out var seen);
            var lastSeenUtc = seen == default ? (DateTime?)null : seen;

            try
            {
                _tenantContext.SetTenantId(tenant.Id);
                var overview = await _traces.GetTraceOverviewAsync(query, ct);
                var logs = await _logs.GetLogHistogramAsync(logQuery, ct);
                results.Add(ToDto(tenant, overview, logs, lastSeenUtc));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unhealthy tenant must not blank the whole page — it gets a card flagged
                // Failed instead, so the operator can see that it failed rather than that it is
                // quiet.
                _logger.LogError(ex, "Global overview failed for tenant {TenantId} ({TenantName})",
                    tenant.Id, tenant.Name);
                results.Add(new GlobalTenantStatsDto
                {
                    TenantId = tenant.Id,
                    TenantName = tenant.Name,
                    LastSeenUtc = lastSeenUtc,
                    Failed = true,
                });
            }
        }

        return Ok(new GlobalOverviewDto { Tenants = results });
    }

    private static GlobalTenantStatsDto ToDto(
        TenantInfo tenant, TraceOverview overview, List<LogVolumeBucket> logs, DateTime? lastSeenUtc)
    {
        var summary = overview.Summary;

        // Summed rather than read off a single bucket: GetLogHistogramAsync returns one row per
        // *non-empty* bucket, so a window with no logs yields an empty list, not a zeroed row.
        var logCount = logs.Sum(b => b.Trace + b.Debug + b.Info + b.Warn + b.Error + b.Fatal);
        var logErrorCount = logs.Sum(b => b.Error + b.Fatal);

        // Prefer the in-window maximum when the tenant is active: it is exact and needs no
        // second query. Fall back to the unbounded lookback for a tenant that sent nothing in
        // this window, which is precisely the case the card renders as "silent".
        var lastSeen = summary.LastTraceStartTime ?? lastSeenUtc;

        return new GlobalTenantStatsDto
        {
            TenantId = tenant.Id,
            TenantName = tenant.Name,
            TraceCount = summary.Count,
            ErrorCount = summary.ErrorCount,
            P50Ms = summary.P50Ms,
            P95Ms = summary.P95Ms,
            P99Ms = summary.P99Ms,
            ServiceCount = summary.ServiceCount,
            LogCount = logCount,
            LogErrorCount = logErrorCount,
            LastSeenUtc = lastSeen,
            Buckets = overview.Buckets,
        };
    }
}

public sealed class GlobalOverviewDto
{
    public List<GlobalTenantStatsDto> Tenants { get; init; } = [];
}

/// <summary>
/// One tenant card. Rates and health classification are left to the client: the thresholds that
/// decide healthy/degraded live in the UI so the cards and their legend cannot drift apart.
/// </summary>
public sealed class GlobalTenantStatsDto
{
    public long TenantId { get; init; }
    public string TenantName { get; init; } = string.Empty;

    public int TraceCount { get; init; }
    public int ErrorCount { get; init; }

    /// <summary>Window percentiles (ms). 0 when <c>TraceCount == 0</c> — "no data", not a value.</summary>
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }

    public int ServiceCount { get; init; }

    /// <summary>All log records in the window, every severity.</summary>
    public int LogCount { get; init; }

    /// <summary>Error + Fatal log records only (severity_number >= 17).</summary>
    public int LogErrorCount { get; init; }

    /// <summary>
    /// Most recent trace start, from the window when the tenant is active and from a 30-day
    /// lookback otherwise. Null means nothing in either — the tenant has never reported, or not
    /// within the lookback.
    /// </summary>
    public DateTime? LastSeenUtc { get; init; }

    /// <summary>Volume histogram over the window; drives the card's sparkline.</summary>
    public List<TraceVolumeBucket> Buckets { get; init; } = [];

    /// <summary>True when this tenant's query threw. The card renders "unknown", not "silent".</summary>
    public bool Failed { get; init; }
}
