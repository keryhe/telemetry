using MySqlConnector;
using Microsoft.Extensions.DependencyInjection;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlRetentionTests(MySqlFixture fixture) : RetentionTestsBase(fixture)
{
    private readonly MySqlFixture _db = fixture;

    protected override async Task<long> CountRowsAsync(string table, string timeColumn, long fromNano, long toNano)
    {
        await using var conn = new MySqlConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(
            table == "metrics" ? "SELECT COUNT(*) FROM metrics" : $"SELECT COUNT(*) FROM {table} WHERE {timeColumn} >= @from AND {timeColumn} < @to", conn);
        cmd.Parameters.AddWithValue("@from", fromNano);
        cmd.Parameters.AddWithValue("@to", toNano);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    /// <summary>
    /// Per-tenant metric retention (plans/tenant-retention.md, decision 9): the batched delete reaches a tenant's data points through
    /// <c>metric_id</c> and the primary key <c>(metric_id, time_unix_nano, id)</c>, not a scan of the table. (MySQL plans the
    /// <c>IN (subquery)</c> form as a scan, so <see cref="Keryhe.Telemetry.MySql.Services.MySqlRetentionSweeper"/> deletes by metric id list.) Negative control: the
    /// time-only predicate (its index is gone) scans (<c>type = ALL</c>).
    /// </summary>
    [Fact]
    public async Task PerTenantMetricRetentionDelete_UsesTheKey_AndTheTimeOnlyControlScans()
    {
        var metrics = new List<Keryhe.Telemetry.Core.Models.MetricModel>();
        for (var tenant = 1; tenant <= 3; tenant++)
            metrics.AddRange(Seeding.SeededDataBuilder.MultiInstanceGauge(_db.TenantId + tenant, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                serviceName: $"plan-{Guid.NewGuid():N}", instanceCount: 4, pointsPerInstance: 300));
        using (var write = _db.Services.CreateScope())
            await write.ServiceProvider.GetRequiredService<Keryhe.Telemetry.Core.ITelemetryBulkWriter>().FlushMetricsAsync(metrics);

        await using var conn = new MySqlConnection(_db.DatabaseConnectionString);
        await conn.OpenAsync();
        async Task<List<(string Table, string Type)>> Plan(string predicate)
        {
            await using var analyze = new MySqlCommand("ANALYZE TABLE metrics, gauge_data_points", conn);
            await analyze.ExecuteNonQueryAsync();
            await using var cmd = new MySqlCommand(
                $"EXPLAIN DELETE FROM gauge_data_points WHERE {predicate} LIMIT 5000", conn);
            cmd.Parameters.AddWithValue("@tenantId", _db.TenantId + 1);
            cmd.Parameters.AddWithValue("@cutoff", 1L);
            var rows = new List<(string, string)>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader["table"]?.ToString() ?? "", reader["type"]?.ToString() ?? ""));
            return rows;
        }

        // The sweeper reads the tenant's metric ids first and deletes by id list: a range on the primary key.
        var ids = new List<long>();
        await using (var idCmd = new MySqlCommand("SELECT id FROM metrics WHERE tenant_id = @tenantId", conn))
        {
            idCmd.Parameters.AddWithValue("@tenantId", _db.TenantId + 1);
            await using var idReader = await idCmd.ExecuteReaderAsync();
            while (await idReader.ReadAsync()) ids.Add(idReader.GetInt64(0));
        }
        Assert.NotEmpty(ids);
        var perTenant = await Plan($"metric_id IN ({string.Join(',', ids)}) AND time_unix_nano < @cutoff");
        Assert.Contains(perTenant, r => r.Table == "gauge_data_points" && r.Type is "range" or "ref" or "eq_ref");

        var control = await Plan("time_unix_nano < @cutoff");
        Assert.Contains(control, r => r.Table == "gauge_data_points" && r.Type is "ALL" or "index");
    }
}
