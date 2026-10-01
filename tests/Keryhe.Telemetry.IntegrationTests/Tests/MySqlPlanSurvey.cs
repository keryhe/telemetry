using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlPlanSurvey(MySqlFixture fixture) : PlanSurveyBase(fixture)
{
    protected override string Limit(int n) => $"LIMIT {n}";

    protected override async Task PrepareAsync()
    {
        await using var conn = new MySqlConnection(Fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        foreach (var t in new[] { "spans", "log_records" })
        {
            await using var cmd = new MySqlCommand($"ANALYZE TABLE {t}", conn);
            await using var r = await cmd.ExecuteReaderAsync();
        }
    }

    protected override async Task<string> ExplainAsync(string sql, Dictionary<string, object> parameters)
    {
        await using var conn = new MySqlConnection(Fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("EXPLAIN ANALYZE " + sql, conn) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue("@" + name, value);
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }
}
