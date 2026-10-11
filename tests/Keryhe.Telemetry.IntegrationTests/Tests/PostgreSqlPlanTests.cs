using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// PostgreSQL (schema-simplification plan, Tests section): trace/span ids are
/// <c>text</c> columns, so Npgsql's default <c>text</c> parameter matches and the trace-detail lookup
/// is an index scan -- never a sequential scan of <c>spans</c>. Negative-controlled: a non-sargable
/// form of the same predicate must show a sequential scan, proving the check can fail.
/// </summary>
[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlPlanTests : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    public PostgreSqlPlanTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TraceDetailLookup_UsesAnIndex_NotASequentialScan()
    {
        var spans = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 2000);
        using (var write = _fixture.Services.CreateScope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushTracesAsync(spans);

        await using var conn = new NpgsqlConnection(_fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, "ANALYZE spans");
        // Make a sequential scan the planner's last resort, so it only appears when no index can serve the predicate.
        await ExecuteAsync(conn, "SET enable_seqscan = off");

        var traceId = spans[0].TraceIdHex;

        // The id predicate on its own: the (trace_id, span_id) index must serve it, so the text parameter
        // matches the text column with no cast that would disable the index.
        var indexed = await PlanAsync(conn, "SELECT s.id FROM spans s WHERE s.trace_id = @traceId", traceId);
        Assert.DoesNotContain("Seq Scan", indexed);
        Assert.Contains("idx_spans_trace_span", indexed);

        // The repository's real shape adds the tenant: still never a sequential scan.
        var withTenant = await PlanAsync(conn,
            "SELECT s.id FROM spans s WHERE s.tenant_id = @tenantId AND s.trace_id = @traceId", traceId);
        Assert.DoesNotContain("Seq Scan", withTenant);

        // Negative control: a non-sargable form of the same predicate has no index to use.
        var control = await PlanAsync(conn, "SELECT s.id FROM spans s WHERE (s.trace_id || '') = @traceId", traceId);
        Assert.Contains("Seq Scan", control);
    }

    /// <summary>
    /// Per-tenant metric retention (plans/tenant-retention.md, decision 9): the inner SELECT of the batched delete finds a tenant's
    /// points through <c>metric_id</c> and the <c>(metric_id, time_unix_nano, id)</c> index. With the time-only BRIN index gone, the
    /// same predicate without the tenant's metric ids has no leading column to seek on: the negative control shows no <c>metric_id</c> condition.
    /// </summary>
    [Fact]
    public async Task PerTenantMetricRetention_UsesTheMetricIdIndex_NotASequentialScan()
    {
        var metrics = new List<Keryhe.Telemetry.Core.Models.MetricModel>();
        for (var tenant = 1; tenant <= 3; tenant++)
            metrics.AddRange(SeededDataBuilder.MultiInstanceGauge(_fixture.TenantId + tenant, WindowStart, serviceName: $"plan-{Guid.NewGuid():N}", instanceCount: 4, pointsPerInstance: 300));
        using (var write = _fixture.Services.CreateScope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        await using var conn = new NpgsqlConnection(_fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await ExecuteAsync(conn, "ANALYZE metrics");
        await ExecuteAsync(conn, "ANALYZE gauge_data_points");
        await ExecuteAsync(conn, "SET enable_seqscan = off");

        // The statement PostgreSqlRetentionSweeper issues is DELETE ... WHERE ctid = ANY (ARRAY(<this select>)).
        async Task<string> Plan(string predicate)
        {
            await using var cmd = new NpgsqlCommand(
                $"EXPLAIN SELECT ctid FROM gauge_data_points WHERE {predicate} LIMIT 5000", conn);
            cmd.Parameters.AddWithValue("tenantId", _fixture.TenantId + 1);
            cmd.Parameters.AddWithValue("cutoff", SeededDataBuilder.ToUnixNano(WindowStart));
            var lines = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
            return string.Join('\n', lines);
        }

        var perTenant = await Plan("metric_id IN (SELECT id FROM metrics WHERE tenant_id = @tenantId) AND time_unix_nano < @cutoff");
        Assert.DoesNotContain("Seq Scan on gauge_data_points", perTenant);
        Assert.Contains("idx_gauge_metric_time", perTenant);

        // Seeks (metric_id = metrics.id) per tenant metric ...
        Assert.Contains("metric_id = metrics.id", perTenant);

        // ... where the time-only predicate has no leading column to seek on (the BRIN index it used is gone): it walks the whole index.
        var control = await Plan("time_unix_nano < @cutoff");
        Assert.DoesNotContain("metric_id =", control);
    }

    private async Task<string> PlanAsync(NpgsqlConnection conn, string sql, string traceId)
    {
        await using var cmd = new NpgsqlCommand("EXPLAIN " + sql, conn);
        if (sql.Contains("@tenantId")) cmd.Parameters.AddWithValue("tenantId", _fixture.TenantId);
        cmd.Parameters.AddWithValue("traceId", traceId);   // string => text, exactly what the repository's Dapper parameter is
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
