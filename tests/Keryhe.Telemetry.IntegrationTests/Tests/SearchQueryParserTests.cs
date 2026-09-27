using Keryhe.Telemetry.Core.Data.Read;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="SearchQueryParser"/> — no database dependency, so these
/// run without Docker (list-pages-server-side plan, Phase 1). Covers the grammar edge cases called
/// out in the plan: the <c>=</c>-vs-<c>:</c> precedence rule, 32-hex-char trace-id detection,
/// quote-trimming and negation.
/// </summary>
public class SearchQueryParserTests
{
    [Fact]
    public void Empty_Input_Returns_No_Terms()
    {
        var result = SearchQueryParser.Parse("");
        Assert.False(result.IsTraceIdSearch);
        Assert.Empty(result.Terms);
    }

    [Fact]
    public void Whitespace_Only_Returns_No_Terms()
    {
        var result = SearchQueryParser.Parse("   ");
        Assert.Empty(result.Terms);
    }

    [Fact]
    public void ExactlyThirtyTwoHexChars_Is_TraceIdSearch()
    {
        var traceId = new string('a', 32);
        var result = SearchQueryParser.Parse(traceId);
        Assert.True(result.IsTraceIdSearch);
        Assert.Equal(traceId, result.TraceId);
        Assert.Empty(result.Terms);
    }

    [Fact]
    public void ThirtyTwoHexChars_With_Colon_Is_Not_TraceIdSearch()
    {
        var almostTraceId = new string('a', 31) + ":";
        var result = SearchQueryParser.Parse(almostTraceId);
        Assert.False(result.IsTraceIdSearch);
    }

    [Fact]
    public void ThirtyOneHexChars_Is_Not_TraceIdSearch()
    {
        var result = SearchQueryParser.Parse(new string('a', 31));
        Assert.False(result.IsTraceIdSearch);
    }

    [Fact]
    public void NonHexThirtyTwoChars_Is_Not_TraceIdSearch()
    {
        var result = SearchQueryParser.Parse(new string('g', 32));
        Assert.False(result.IsTraceIdSearch);
    }

    [Fact]
    public void PlainWord_Is_FreeText()
    {
        var result = SearchQueryParser.Parse("timeout");
        var term = Assert.Single(result.Terms);
        Assert.False(term.IsAttributeFilter);
        Assert.Equal("timeout", term.FreeText);
        Assert.False(term.Negate);
    }

    [Fact]
    public void Colon_Produces_Contains_AttributeFilter()
    {
        var result = SearchQueryParser.Parse("service.name:checkout");
        var term = Assert.Single(result.Terms);
        Assert.True(term.IsAttributeFilter);
        Assert.False(term.IsExactMatch);
        Assert.Equal("service.name", term.Key);
        Assert.Equal("checkout", term.Value);
    }

    [Fact]
    public void Equals_Produces_Exact_AttributeFilter()
    {
        var result = SearchQueryParser.Parse("http.status_code=500");
        var term = Assert.Single(result.Terms);
        Assert.True(term.IsAttributeFilter);
        Assert.True(term.IsExactMatch);
        Assert.Equal("http.status_code", term.Key);
        Assert.Equal("500", term.Value);
    }

    /// <summary>
    /// The deliberate divergence from Query.cs's TagFilter.Parse: `eq >= 0` always wins when
    /// present, regardless of which delimiter appears first in the raw string.
    /// </summary>
    [Fact]
    public void Equals_Wins_Over_Colon_Even_When_Colon_Comes_First()
    {
        var result = SearchQueryParser.Parse("a:b=c");
        var term = Assert.Single(result.Terms);
        Assert.True(term.IsAttributeFilter);
        Assert.True(term.IsExactMatch);
        Assert.Equal("a:b", term.Key);
        Assert.Equal("c", term.Value);
    }

    [Fact]
    public void LeadingDash_Sets_Negate_And_Strips_Prefix()
    {
        var result = SearchQueryParser.Parse("-service.name:checkout");
        var term = Assert.Single(result.Terms);
        Assert.True(term.Negate);
        Assert.Equal("service.name", term.Key);
        Assert.Equal("checkout", term.Value);
    }

    [Fact]
    public void LeadingDash_On_FreeText_Sets_Negate()
    {
        var result = SearchQueryParser.Parse("-timeout");
        var term = Assert.Single(result.Terms);
        Assert.True(term.Negate);
        Assert.Equal("timeout", term.FreeText);
    }

    [Theory]
    [InlineData("key:\"quoted value\"", "quoted value")]
    [InlineData("key:'single quoted'", "single quoted")]
    [InlineData("key:\"\"\"triple\"\"\"", "triple")]
    public void Value_Has_Surrounding_Quotes_Trimmed(string input, string expectedValue)
    {
        var result = SearchQueryParser.Parse(input);
        var term = Assert.Single(result.Terms);
        Assert.Equal(expectedValue, term.Value);
    }

    [Fact]
    public void FreeText_Has_Surrounding_Quotes_Trimmed()
    {
        var result = SearchQueryParser.Parse("\"exact phrase\"");
        var term = Assert.Single(result.Terms);
        Assert.Equal("exact phrase", term.FreeText);
    }

    [Fact]
    public void Multiple_Terms_Split_On_Literal_AND()
    {
        var result = SearchQueryParser.Parse("service.name:checkout AND timeout AND -status:ok");
        Assert.Equal(3, result.Terms.Count);
        Assert.Equal("checkout", result.Terms[0].Value);
        Assert.Equal("timeout", result.Terms[1].FreeText);
        Assert.True(result.Terms[2].Negate);
        Assert.Equal("status", result.Terms[2].Key);
    }

    [Fact]
    public void Lowercase_and_Is_Not_A_Splitter()
    {
        // Only the literal, uppercase, space-padded " AND " splits terms.
        var result = SearchQueryParser.Parse("cats and dogs");
        var term = Assert.Single(result.Terms);
        Assert.Equal("cats and dogs", term.FreeText);
    }

    [Fact]
    public void Keys_And_Values_Are_Trimmed()
    {
        var result = SearchQueryParser.Parse("  service.name : checkout  ");
        var term = Assert.Single(result.Terms);
        Assert.Equal("service.name", term.Key);
        Assert.Equal("checkout", term.Value);
    }
}
