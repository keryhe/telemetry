using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerPlanSurvey(SqlServerFixture fixture) : PlanSurveyBase(fixture)
{
    protected override string Limit(int n) => $"OFFSET 0 ROWS FETCH NEXT {n} ROWS ONLY";

    protected override Task PrepareAsync() => Task.CompletedTask;

    protected override async Task<string> ExplainAsync(string sql, Dictionary<string, object> parameters)
    {
        await using var conn = new SqlConnection(Fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using (var on = new SqlCommand("SET STATISTICS XML ON", conn)) await on.ExecuteNonQueryAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            // Match the repositories: ids as sized ANSI, everything else typed by its CLR type.
            if (name == "traceId") cmd.Parameters.Add("@traceId", System.Data.SqlDbType.VarChar, 32).Value = value;
            else cmd.Parameters.AddWithValue("@" + name, value);
        }
        string? plan = null;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server 2005 XML Showplan", StringComparison.Ordinal))
                    while (await reader.ReadAsync()) plan = reader.GetString(0);
                else
                    while (await reader.ReadAsync()) { }
            } while (await reader.NextResultAsync());
        }
        if (plan == null) return "(no plan returned)";
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(plan, "<RelOp [^>]*PhysicalOp=\"([^\"]+)\"[^>]*ActualRows=\"?[^>]*>|<RelOp [^>]*PhysicalOp=\"([^\"]+)\"", RegexOptions.Singleline))
            sb.AppendLine(m.Value.Length > 160 ? m.Value[..160] : m.Value);
        foreach (Match m in Regex.Matches(plan, "<Object [^>]*Index=\"([^\"]+)\"[^>]*/>")) sb.AppendLine("index: " + m.Groups[1].Value + " (" + Regex.Match(m.Value, "Table=\"([^\"]+)\"").Groups[1].Value + ")");
        if (plan.Contains("CONVERT_IMPLICIT")) sb.AppendLine("!! CONVERT_IMPLICIT in plan");
        foreach (Match m in Regex.Matches(plan, "ActualRows=\"(\\d+)\"")) sb.Append("");
        return sb.ToString();
    }
}
