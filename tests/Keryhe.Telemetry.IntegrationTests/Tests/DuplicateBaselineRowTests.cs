using System.Data.Common;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data.Read;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic tests for <see cref="DapperReadRepository.ToDictionaryNewest"/> — no database dependency.
///
/// The five data-point tables are plain append targets since schema 3.0.0 (decision 7: no unique key, no foreign
/// keys), so a re-delivered point is stored twice and any "one row per stream" result set is only as unique as the
/// query that derived it. The cumulative-delta loaders key their pre-window baseline row on
/// <c>metric_id + attributes_json</c>, which nothing in the schema enforces as unique; a plain <c>ToDictionary</c>
/// there threw <see cref="ArgumentException"/> and the API answered <c>metrics/series</c> with a 500 (seen 10 times
/// on MySQL in the 3.0.1 stress ramp, from <c>ComputeCumulativeDeltas</c> and
/// <c>ComputeHistogramCumulativeDeltas</c>). This is the guard that replaced it: keep the newest observation
/// instead of throwing.
/// </summary>
public class DuplicateBaselineRowTests
{
    /// <summary>A pre-window baseline row, in the shape the cumulative loaders read.</summary>
    private sealed record BaselineRow(long MetricId, string? AttributesJson, long TimeUnixNano, long Value);

    private static string StreamKey(BaselineRow r) => $"{r.MetricId}\u0001{r.AttributesJson}";

    [Fact]
    public void RepeatedStream_DoesNotThrow_AndKeepsTheNewestRow()
    {
        // The same stream twice: a re-delivered older observation alongside the newest one.
        var rows = new List<BaselineRow>
        {
            new(7, """{"region": "r0", "instance": "i0"}""", TimeUnixNano: 4_000, Value: 80),
            new(7, """{"region": "r0", "instance": "i0"}""", TimeUnixNano: 5_000, Value: 100),
        };

        var byStream = ToDictionaryNewestProbe.Build(rows, StreamKey, r => r.TimeUnixNano);

        var baseline = Assert.Single(byStream).Value;
        Assert.Equal(5_000, baseline.TimeUnixNano);
        Assert.Equal(100, baseline.Value);
    }

    [Fact]
    public void NewestWins_RegardlessOfRowOrder()
    {
        var newestFirst = new List<BaselineRow>
        {
            new(7, "{}", TimeUnixNano: 5_000, Value: 100),
            new(7, "{}", TimeUnixNano: 4_000, Value: 80),
        };

        var byStream = ToDictionaryNewestProbe.Build(newestFirst, StreamKey, r => r.TimeUnixNano);

        Assert.Equal(100, Assert.Single(byStream).Value.Value);
    }

    [Fact]
    public void ExactDuplicate_KeepsOneRow()
    {
        // The literal re-delivery case: identical point stored twice, same timestamp. Either row will do.
        var rows = new List<BaselineRow>
        {
            new(7, "{}", TimeUnixNano: 5_000, Value: 100),
            new(7, "{}", TimeUnixNano: 5_000, Value: 100),
        };

        var byStream = ToDictionaryNewestProbe.Build(rows, StreamKey, r => r.TimeUnixNano);

        Assert.Equal(100, Assert.Single(byStream).Value.Value);
    }

    [Fact]
    public void DistinctStreams_AreAllKept()
    {
        var rows = new List<BaselineRow>
        {
            new(7, """{"instance": "i0"}""", TimeUnixNano: 5_000, Value: 100),
            new(7, """{"instance": "i1"}""", TimeUnixNano: 5_000, Value: 200),
            new(9, """{"instance": "i0"}""", TimeUnixNano: 5_000, Value: 300),
            new(9, null, TimeUnixNano: 5_000, Value: 400), // a point with no attributes at all
        };

        var byStream = ToDictionaryNewestProbe.Build(rows, StreamKey, r => r.TimeUnixNano);

        Assert.Equal(4, byStream.Count);
    }

    [Fact]
    public void EmptyInput_GivesAnEmptyMap()
    {
        Assert.Empty(ToDictionaryNewestProbe.Build(new List<BaselineRow>(), StreamKey, r => r.TimeUnixNano));
    }

    /// <summary>
    /// Reaches the <c>protected static</c> helper the read repositories use. Deriving is the whole point — the
    /// guard belongs next to <c>ToDictionaryFirst</c> on the shared base, not duplicated per repository.
    /// </summary>
    private sealed class ToDictionaryNewestProbe(ITenantContext tenantContext) : DapperReadRepository(tenantContext)
    {
        protected override Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The helper under test never opens a connection.");

        internal static Dictionary<TKey, TSource> Build<TSource, TKey>(
            IEnumerable<TSource> source, Func<TSource, TKey> key, Func<TSource, long> observedAt) where TKey : notnull =>
            ToDictionaryNewest(source, key, observedAt);
    }
}
