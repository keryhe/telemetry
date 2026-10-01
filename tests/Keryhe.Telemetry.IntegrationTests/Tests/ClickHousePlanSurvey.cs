using System.Text;
using System.Text.RegularExpressions;
using ClickHouse.Client.ADO;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHousePlanSurvey(ClickHouseFixture fixture) : PlanSurveyBase(fixture)
{
    protected override string Limit(int n) => $"LIMIT {n}";

    protected override Task PrepareAsync() => Task.CompletedTask;

    protected override async Task<string> ExplainAsync(string sql, Dictionary<string, object> parameters)
    {
        await using var conn = new ClickHouseConnection(Fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN indexes = 1 " + sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }
}
