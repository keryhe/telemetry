using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// SQL Server specifics of schema 3.0.0 (schema-simplification plan, Tests section): the database
/// runs <c>READ_COMMITTED_SNAPSHOT</c>, so a reader never blocks behind an open ingest transaction;
/// and trace/span ids are bound as sized ANSI parameters, so an id lookup is an index seek with no
/// implicit conversion of the column (with a negative control proving the check can fail).
/// </summary>
[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerSchemaTests(SqlServerFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReadCommittedSnapshot_IsOn()
    {
        await using var conn = new SqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()", conn);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Reader_DoesNotBlock_BehindAnOpenIngestTransaction()
    {
        // Some committed rows, so the read has something to return.
        using (var write = fixture.Services.CreateScope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>()
                .FlushTracesAsync(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, WindowStart, traceCount: 5));

        // An ingest transaction that has inserted a span in the same window and has not committed.
        await using var ingest = new SqlConnection(fixture.DatabaseConnectionString);
        await ingest.OpenAsync();
        await using var tx = (SqlTransaction)await ingest.BeginTransactionAsync();
        await using (var insert = new SqlCommand("""
            INSERT INTO spans (tenant_id, trace_id, span_id, resource_id, scope_id, name, start_time_unix_nano, end_time_unix_nano)
            VALUES (@tenant, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbb', 1, 1, 'uncommitted', @start, @end)
            """, ingest, tx))
        {
            insert.Parameters.AddWithValue("@tenant", fixture.TenantId);
            insert.Parameters.AddWithValue("@start", SeededDataBuilder.ToUnixNano(WindowStart.AddSeconds(2)));
            insert.Parameters.AddWithValue("@end", SeededDataBuilder.ToUnixNano(WindowStart.AddSeconds(3)));
            await insert.ExecuteNonQueryAsync();
        }

        // The page query reads the same table and range. Under plain READ COMMITTED it would wait for
        // the open transaction's exclusive lock; under row versioning it returns the committed rows.
        using var read = fixture.Services.CreateScope();
        var page = read.ServiceProvider.GetRequiredService<ITraceReadRepository>().GetTracePageAsync(new TraceQuery
        {
            Start = WindowStart.AddMinutes(-1), End = WindowStart.AddHours(1), Size = 100, Mode = "all", AsOf = DateTime.UtcNow.AddMinutes(5)
        });

        var finished = await Task.WhenAny(page, Task.Delay(TimeSpan.FromSeconds(15)));
        await tx.RollbackAsync();
        Assert.Same(page, finished);
        Assert.Equal(5, (await page).Items.Count);   // the uncommitted trace is not visible
    }

    [Fact]
    public async Task TraceIdLookup_UsesASizedAnsiParameter_WithNoImplicitConversion()
    {
        using (var write = fixture.Services.CreateScope())
            await write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>()
                .FlushTracesAsync(SeededDataBuilder.BasicTraceWindow(fixture.TenantId, WindowStart, traceCount: 50));

        var traceId = new string('a', 32);

        // The plan of "tenant + trace id lookup" for a variable of the given type: the same comparison of
        // a typed parameter against the varchar(32) column that the repository's query makes.
        async Task<string> PlanAsync(string declaredType)
        {
            await using var conn = new SqlConnection(fixture.DatabaseConnectionString);
            await conn.OpenAsync();
            await using (var on = new SqlCommand("SET SHOWPLAN_XML ON", conn)) await on.ExecuteNonQueryAsync();
            await using var cmd = new SqlCommand($"""
                DECLARE @tenantId bigint = {fixture.TenantId};
                DECLARE @traceId {declaredType} = '{traceId}';
                SELECT s.id FROM spans s WHERE s.tenant_id = @tenantId AND s.trace_id = @traceId
                """, conn);
            var plans = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) plans.Add(reader.GetString(0)); } while (await reader.NextResultAsync());
            return string.Join('\n', plans);
        }

        // The conversion that hurts is one applied to the COLUMN: it makes the predicate non-sargable.
        // (Converting the parameter -- here only to the column's binary collation -- is harmless.)
        static bool ConvertsTheColumn(string plan) =>
            System.Text.RegularExpressions.Regex.IsMatch(plan, @"CONVERT_IMPLICIT\(.{0,120}?\[trace_id\]");

        // What the repository sends (Dapper DbString IsAnsi, Length 32): varchar(32), the column's own type.
        var ansi = await PlanAsync("varchar(32)");
        Assert.Contains("PhysicalOp", ansi);                 // a plan really came back
        Assert.False(ConvertsTheColumn(ansi), "an ANSI varchar(32) parameter must not convert the trace_id column");
        Assert.Contains("Index Seek", ansi);

        // Negative control: Dapper's default (nvarchar) makes SQL Server convert the COLUMN, so it cannot seek.
        var unicode = await PlanAsync("nvarchar(4000)");
        Assert.True(ConvertsTheColumn(unicode), "the nvarchar control must show the column being converted");
    }
}
