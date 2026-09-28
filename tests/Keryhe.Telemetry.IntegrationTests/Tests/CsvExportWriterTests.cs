using System.Text;
using Keryhe.Telemetry.Api.Export;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="CsvFormulaGuard"/>/<see cref="CsvRowWriter"/>
/// (list-pages-server-side plan, Phase 8) — the CSV formula-injection guard the plan calls out by
/// name: a cell starting with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> must be prefixed with a
/// leading <c>'</c> before it reaches the response body, covering all four characters explicitly
/// per the plan's Verification item 12.
/// </summary>
public class CsvExportWriterTests
{
    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1234567890", "'+1234567890")]
    [InlineData("-1234567890", "'-1234567890")]
    [InlineData("@cmd|'/c calc'!A1", "'@cmd|'/c calc'!A1")]
    public void Escape_Prefixes_Dangerous_Leading_Characters(string input, string expected)
    {
        Assert.Equal(expected, CsvFormulaGuard.Escape(input));
    }

    [Theory]
    [InlineData("normal text")]
    [InlineData("500")]
    [InlineData("checkout-api")] // an internal hyphen, not a LEADING one, must not be escaped
    [InlineData("")]
    public void Escape_Leaves_Safe_Values_Unchanged(string input)
    {
        Assert.Equal(input, CsvFormulaGuard.Escape(input));
    }

    [Fact]
    public void Escape_Null_Returns_Empty()
    {
        Assert.Equal("", CsvFormulaGuard.Escape(null));
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@import")]
    public async Task CsvRowWriter_WritesGuardedCell_ForEachDangerousLeadingCharacter(string dangerousValue)
    {
        await using var stream = new MemoryStream();
        await using (var writer = new CsvRowWriter(stream))
        {
            await writer.WriteRowAsync(["safe", dangerousValue]);
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        var cells = text.TrimEnd('\n').Split(',');
        Assert.Equal("safe", cells[0]);
        Assert.StartsWith("'", cells[1]);
        Assert.EndsWith(dangerousValue, cells[1]);
    }

    [Fact]
    public async Task CsvRowWriter_QuotesCellsContainingComma()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new CsvRowWriter(stream))
        {
            await writer.WriteRowAsync(["a,b", "plain"]);
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("\"a,b\",plain", text);
    }

    [Fact]
    public async Task CsvRowWriter_DoublesInternalQuotes()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new CsvRowWriter(stream))
        {
            await writer.WriteRowAsync(["say \"hi\""]);
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("\"say \"\"hi\"\"\"", text);
    }
}
