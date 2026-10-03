using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Trace detail on a hypertable (trace-list-detail-performance plan, Phase 6a): without a time bound the by-trace-id read
/// probes every chunk; with the trace-time hint's bounds constraint exclusion leaves only the chunks around it. The query here
/// has the shape of <c>TraceReadRepositoryBase.LoadTraceSpansAsync</c>'s WHERE clause (tenant, trace id, start-time bounds).
/// </summary>
[Collection(ProviderNames.Timescale)]
[Trait("Provider", ProviderNames.Timescale)]
public sealed class TimescaleTraceHintPlanTests(TimescaleFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime Start = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HintedBounds_ExcludeChunks_ThatTheUnboundedReadProbes()
    {
        // Twelve days of spans, one batch a day: the hypertable has 6-hour chunks, so this is at least twelve chunks.
        using (var write = fixture.Services.CreateScope())
        {
            var writer = write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            for (var day = 0; day < 12; day++)
                await writer.FlushTracesAsync(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, Start.AddDays(day), traceCount: 200, seedOffset: day * 10_000));
        }

        var traceId = SeededDataBuilder.BasicTraceWindow(fixture.TenantId, Start.AddDays(6), traceCount: 1, seedOffset: 6 * 10_000)[0].TraceIdHex;
        var hint = SeededDataBuilder.ToUnixNano(Start.AddDays(6));
        // The trace's own extent plus the one-minute margin, as the repository builds it (a trace here is a few hundred ms long).
        const long margin = 60_000_000_000L, extent = 1_000_000_000L;

        await using var conn = new NpgsqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using (var analyze = new NpgsqlCommand("ANALYZE spans", conn)) await analyze.ExecuteNonQueryAsync();

        async Task<int> ChunksScannedAsync(string extraWhere)
        {
            await using var cmd = new NpgsqlCommand(
                $"EXPLAIN SELECT s.id FROM spans s WHERE s.tenant_id = @tenantId AND s.trace_id = @traceId{extraWhere}", conn);
            cmd.Parameters.AddWithValue("tenantId", fixture.TenantId);
            cmd.Parameters.AddWithValue("traceId", traceId);
            cmd.Parameters.AddWithValue("lo", hint - margin);
            cmd.Parameters.AddWithValue("hi", hint + extent + margin);
            var plan = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));
            // One scan node per chunk the plan visits (each chunk is a table named _hyper_<id>_<n>_chunk).
            return plan.Count(line => line.Contains("_hyper_", StringComparison.Ordinal) && (line.Contains("Scan", StringComparison.Ordinal)));
        }

        var unbounded = await ChunksScannedAsync("");
        var bounded = await ChunksScannedAsync(" AND s.start_time_unix_nano >= @lo AND s.start_time_unix_nano <= @hi");

        Assert.True(unbounded >= 12, $"the unbounded read should probe every chunk, saw {unbounded}");
        Assert.True(bounded <= 6, $"the hinted read should touch only the chunks near the hint, saw {bounded}");
        Assert.True(bounded < unbounded);
    }
}
