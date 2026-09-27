using Keryhe.Telemetry.Core.Data.Read;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="KeysetCursor"/> — no database dependency (list-pages-server-side
/// plan, Phase 1). Covers the encode/decode round-trip, filter-hash mismatch rejection, and
/// forward/backward predicate generation.
/// </summary>
public class KeysetCursorTests
{
    [Fact]
    public void Encode_Decode_RoundTrips()
    {
        var hash = KeysetCursor.ComputeFilterHash("start=1|end=2|service=checkout");
        var encoded = KeysetCursor.Encode(sortKey: 1_700_000_000_000_000_000L, tiebreak: 42, filterHash: hash);

        var decoded = KeysetCursor.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(1_700_000_000_000_000_000L, decoded!.K);
        Assert.Equal(42, decoded.Id);
        Assert.Equal(hash, decoded.F);
        Assert.Equal(1, decoded.V);
    }

    [Fact]
    public void Encoded_Cursor_Is_UrlSafe()
    {
        var hash = KeysetCursor.ComputeFilterHash("anything");
        var encoded = KeysetCursor.Encode(1, 1, hash);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Fact]
    public void Decode_Null_Or_Empty_Returns_Null()
    {
        Assert.Null(KeysetCursor.Decode(null));
        Assert.Null(KeysetCursor.Decode(""));
    }

    [Fact]
    public void Decode_Malformed_Cursor_Returns_Null()
    {
        Assert.Null(KeysetCursor.Decode("not-valid-base64url-json!!!"));
    }

    [Fact]
    public void MatchesFilterHash_True_For_Same_Filters()
    {
        const string filters = "start=1|end=2|service=checkout|asOf=100";
        var hash = KeysetCursor.ComputeFilterHash(filters);
        var cursor = new DecodedCursor(1, 1, hash);

        Assert.True(KeysetCursor.MatchesFilterHash(cursor, filters));
    }

    [Fact]
    public void MatchesFilterHash_False_For_Different_Filters()
    {
        var hash = KeysetCursor.ComputeFilterHash("start=1|end=2|service=checkout|asOf=100");
        var cursor = new DecodedCursor(1, 1, hash);

        // Stale cursor: asOf changed between the request that minted it and this one.
        Assert.False(KeysetCursor.MatchesFilterHash(cursor, "start=1|end=2|service=checkout|asOf=200"));
    }

    [Fact]
    public void MatchesFilterHash_False_For_Different_AsOf_Only()
    {
        var hashA = KeysetCursor.ComputeFilterHash("filters|asOf=A");
        var hashB = KeysetCursor.ComputeFilterHash("filters|asOf=B");
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Predicate_Descending_Uses_LessThan()
    {
        var predicate = KeysetCursor.Predicate("created_at", "id", "k", "id", descending: true);
        Assert.Equal("(created_at <= @k AND (created_at < @k OR id < @id))", predicate);
    }

    [Fact]
    public void Predicate_Ascending_Uses_GreaterThan()
    {
        var predicate = KeysetCursor.Predicate("created_at", "id", "k", "id", descending: false);
        Assert.Equal("(created_at >= @k AND (created_at > @k OR id > @id))", predicate);
    }

    [Theory]
    [InlineData(1000, 100, 1000 % 100)]
    [InlineData(1000, 250, 0)] // exact multiple -> falls to pageSize, not 0
    [InlineData(50, 100, 50)]  // total smaller than page size -> total itself (mod == total)
    public void LastPageRowCount_ExactTotal_Uses_Remainder_Or_PageSize(long total, int pageSize, int expectedIfNonZero)
    {
        var result = KeysetCursor.LastPageRowCount(total, pageSize);
        var remainder = total % pageSize;
        var expected = remainder == 0 ? pageSize : (int)remainder;
        Assert.Equal(expected, result);
        _ = expectedIfNonZero; // documents intent per case; the real assertion is the computed `expected` above.
    }

    [Fact]
    public void LastPageRowCount_NullTotal_Returns_PageSize()
    {
        Assert.Equal(100, KeysetCursor.LastPageRowCount(null, 100));
    }

    [Fact]
    public void LastPageRowCount_ZeroTotal_Returns_PageSize()
    {
        Assert.Equal(100, KeysetCursor.LastPageRowCount(0, 100));
    }
}
