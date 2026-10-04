using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Dapper reads of the summary rollups (plans/summary-rollups.md): one statement per call over the
/// minute rows, summed per (chart bucket, service) -- or (chart bucket, severity) for logs -- so the
/// result is at most buckets x services rows whatever the traffic. The bucket is
/// <c>floor(minute / width) * width</c>, aligned to UTC multiples of the width. Each call runs under
/// <see cref="QueryOptions.SummaryTimeoutSeconds"/> on its own connection and reports a timeout
/// instead of throwing (<see cref="TimedQuery"/>). Partial rows are summed here; nothing reads
/// <c>FINAL</c>.
///
/// A provider with an hour tier (MySQL) overrides <see cref="HourTierSource"/>: a bucket of an hour or
/// wider is then read from the hour rows below <c>compacted_through</c> and the minute rows after it.
/// </summary>
public abstract class RollupReadRepositoryBase : DapperReadRepository, IRollupReadRepository
{

    private readonly int _summaryTimeoutSeconds;

    protected RollupReadRepositoryBase(ITenantContext tenantContext, IConfiguration configuration) : base(tenantContext)
    {
        _summaryTimeoutSeconds = int.TryParse(configuration[$"{QueryOptions.SectionName}:SummaryTimeoutSeconds"], out var configured)
            ? configured
            : 5;
    }

    public async Task<RollupReadResult<RequestRollupAggregate>> GetRequestRollupAsync(RollupQuery query, CancellationToken cancellationToken = default)
    {
        var (rows, timedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => ExecuteWithRetryAsync(() => ReadRequestsAsync(query, timeoutSeconds, ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return new RollupReadResult<RequestRollupAggregate> { Rows = rows ?? [], TimedOut = timedOut };
    }

    public async Task<RollupReadResult<LogRollupAggregate>> GetLogRollupAsync(RollupQuery query, CancellationToken cancellationToken = default)
    {
        var (rows, timedOut) = await TimedQuery.RunAsync(
            (timeoutSeconds, ct) => ExecuteWithRetryAsync(() => ReadLogsAsync(query, timeoutSeconds, ct)),
            _summaryTimeoutSeconds, cancellationToken);
        return new RollupReadResult<LogRollupAggregate> { Rows = rows ?? [], TimedOut = timedOut };
    }

    /// <summary>
    /// The statement's source for a window read partly from the hour tier, or null to read the minute rows alone. Called
    /// with a <paramref name="branch"/> that builds <c>SELECT &lt;bucket expr&gt;, columns FROM table WHERE &lt;tenant and
    /// <paramref name="where"/&gt;</c>; an override returns a <c>UNION ALL</c> of branches, binding any parameters it
    /// needs on <paramref name="parameters"/> (<c>@start</c>, <c>@end</c>, <c>@width</c>, <c>@tenantId</c> are always bound).
    /// </summary>
    /// <param name="kind"><c>request</c> or <c>log</c>.</param>
    protected virtual string? HourTierSource(string kind, Func<string, string, string> branch, RollupQuery query, string filters, DynamicParameters parameters)
        => null;

    private DynamicParameters Parameters(RollupQuery query)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", TenantId);
        parameters.Add("start", query.StartNano);
        parameters.Add("end", query.EndNano);
        parameters.Add("width", Math.Max(1, query.BucketSeconds) * 1_000_000_000L);
        if (query.Service != null) parameters.Add("service", query.Service);
        if (query.MinSeverity.HasValue) parameters.Add("minSeverity", query.MinSeverity.Value);
        return parameters;
    }

    private async Task<IReadOnlyList<RequestRollupAggregate>> ReadRequestsAsync(RollupQuery query, int commandTimeout, CancellationToken ct)
    {
        var filters = query.Service != null ? " AND service_name = @service" : "";
        var parameters = Parameters(query);
        string Branch(string table, string where) => $"""
            SELECT {BucketIndexExpr("bucket_start_unix_nano", "@width")} * @width AS b,
                service_name AS svc, request_count, error_count, sum_duration_nanos, max_duration_nanos,
                h00, h01, h02, h03, h04, h05, h06, h07, h08, h09, h10, h11, h12, h13, h14, h15, h16, h17, h18, h19, h20, h21, h22, h23
            FROM {table}
            WHERE tenant_id = @tenantId AND {where}{filters}
            """;
        var source = HourTierSource("request", Branch, query, filters, parameters)
                     ?? Branch("request_rollup_minute", "bucket_start_unix_nano >= @start AND bucket_start_unix_nano < @end");

        var sql = $"""
            SELECT b AS BucketStart,
                svc AS Service,
                {BigintExpr("SUM(request_count)")} AS RequestCount,
                {BigintExpr("SUM(error_count)")} AS ErrorCount,
                {BigintExpr("SUM(sum_duration_nanos)")} AS SumDurationNanos,
                {BigintExpr("MAX(max_duration_nanos)")} AS MaxDurationNanos,
                {BigintExpr("SUM(h00)")} AS H00,
                {BigintExpr("SUM(h01)")} AS H01,
                {BigintExpr("SUM(h02)")} AS H02,
                {BigintExpr("SUM(h03)")} AS H03,
                {BigintExpr("SUM(h04)")} AS H04,
                {BigintExpr("SUM(h05)")} AS H05,
                {BigintExpr("SUM(h06)")} AS H06,
                {BigintExpr("SUM(h07)")} AS H07,
                {BigintExpr("SUM(h08)")} AS H08,
                {BigintExpr("SUM(h09)")} AS H09,
                {BigintExpr("SUM(h10)")} AS H10,
                {BigintExpr("SUM(h11)")} AS H11,
                {BigintExpr("SUM(h12)")} AS H12,
                {BigintExpr("SUM(h13)")} AS H13,
                {BigintExpr("SUM(h14)")} AS H14,
                {BigintExpr("SUM(h15)")} AS H15,
                {BigintExpr("SUM(h16)")} AS H16,
                {BigintExpr("SUM(h17)")} AS H17,
                {BigintExpr("SUM(h18)")} AS H18,
                {BigintExpr("SUM(h19)")} AS H19,
                {BigintExpr("SUM(h20)")} AS H20,
                {BigintExpr("SUM(h21)")} AS H21,
                {BigintExpr("SUM(h22)")} AS H22,
                {BigintExpr("SUM(h23)")} AS H23
            FROM (
                {source}
            ) r
            GROUP BY b, svc
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: commandTimeout, cancellationToken: ct));
        var result = new List<RequestRollupAggregate>();
        foreach (var row in rows)
        {
            var values = new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase);
            var bands = new long[DurationBands.Count];
            for (var i = 0; i < bands.Length; i++) bands[i] = Convert.ToInt64(values[$"H{i:00}"]);
            result.Add(new RequestRollupAggregate
            {
                BucketStartNano = Convert.ToInt64(values["BucketStart"]),
                Service = Convert.ToString(values["Service"]) ?? "",
                RequestCount = Convert.ToInt64(values["RequestCount"]),
                ErrorCount = Convert.ToInt64(values["ErrorCount"]),
                SumDurationNanos = Convert.ToInt64(values["SumDurationNanos"]),
                MaxDurationNanos = Convert.ToInt64(values["MaxDurationNanos"]),
                Bands = bands
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<LogRollupAggregate>> ReadLogsAsync(RollupQuery query, int commandTimeout, CancellationToken ct)
    {
        var filters = (query.Service != null ? " AND service_name = @service" : "")
                      + (query.MinSeverity.HasValue ? " AND severity_number >= @minSeverity" : "");
        var parameters = Parameters(query);
        string Branch(string table, string where) => $"""
            SELECT {BucketIndexExpr("bucket_start_unix_nano", "@width")} * @width AS b,
                severity_number AS sev, record_count
            FROM {table}
            WHERE tenant_id = @tenantId AND {where}{filters}
            """;
        var source = HourTierSource("log", Branch, query, filters, parameters)
                     ?? Branch("log_rollup_minute", "bucket_start_unix_nano >= @start AND bucket_start_unix_nano < @end");

        var sql = $"""
            SELECT b AS BucketStart, sev AS SeverityNumber, {BigintExpr("SUM(record_count)")} AS RecordCount
            FROM (
                {source}
            ) r
            GROUP BY b, sev
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, parameters, commandTimeout: commandTimeout, cancellationToken: ct));
        var result = new List<LogRollupAggregate>();
        foreach (var row in rows)
        {
            var values = new Dictionary<string, object?>((IDictionary<string, object?>)row, StringComparer.OrdinalIgnoreCase);
            result.Add(new LogRollupAggregate
            {
                BucketStartNano = Convert.ToInt64(values["BucketStart"]),
                SeverityNumber = Convert.ToInt32(values["SeverityNumber"]),
                RecordCount = Convert.ToInt64(values["RecordCount"])
            });
        }
        return result;
    }
}
