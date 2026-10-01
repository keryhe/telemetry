using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// PostgreSQL and Timescale (schema-simplification plan, Tests section): trace/span ids are
/// <c>text</c> columns, so Npgsql's default <c>text</c> parameter matches and the trace-detail lookup
/// is an index scan -- never a sequential scan of <c>spans</c>. Negative-controlled: a non-sargable
/// form of the same predicate must show a sequential scan, proving the check can fail.
/// </summary>
public abstract class PostgresFamilyPlanTestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected PostgresFamilyPlanTestsBase(ProviderFixture fixture) => _fixture = fixture;

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
