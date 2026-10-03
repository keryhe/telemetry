using Keryhe.Telemetry.StressTests.Load;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>The history seed step's data (stress harness, trace-list-detail-performance plan Phase 0): no database or Docker.</summary>
public sealed class HistorySeedTests
{
    private static readonly LoadTenant[] Tenants =
    [
        new(1, "t1", "key-1"), new(2, "t2", "key-2"),
    ];

    private static (TraceShaper Shaper, Topology Topology, LoadProfile Profile) Build()
    {
        var profile = new LoadProfile { Seed = 5 };
        profile.Transport.RecordsPerExport = 200;
        var topology = new Topology(Tenants, profile);
        return (new TraceShaper(profile, topology, new Random(11)), topology, profile);
    }

    private static string Hex(Google.Protobuf.ByteString id) => Convert.ToHexString(id.ToByteArray()).ToLowerInvariant();

    [Fact]
    public void NextAt_StampsTracesAtTheGivenTime_AndLedgersThemAsCurrent()
    {
        var (shaper, _, _) = Build();
        var baseNanos = 1_700_000_000_000_000_000L;
        var remembered = new List<SeededTrace>();

        var payload = shaper.NextAt(baseNanos, remembered);

        var spans = payload.Request.ResourceSpans.SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans).ToList();
        Assert.True(spans.Count >= 200, "an export carries at least RecordsPerExport spans");
        Assert.Equal(spans.Count, payload.Records);
        Assert.All(spans, s => Assert.InRange((long)s.StartTimeUnixNano, baseNanos, baseNanos + 10_000_000_000L));

        // Ledgered as current spans, so the correctness check still balances against the rows the seed writes.
        var entry = Assert.Single(payload.Entries);
        Assert.Equal("spans", entry.Table);
        Assert.Equal(RecordAge.Current, entry.Age);
        Assert.Equal(spans.Count, entry.Rows);

        // One remembered trace per export, and it is a real trace in the export.
        var one = Assert.Single(remembered);
        Assert.Equal("history", one.Kind);
        Assert.Contains(spans, s => Hex(s.TraceId) == one.TraceIdHex);
        Assert.Equal(Tenants[one.TenantIndex].Id, one.TenantId);
    }

    [Fact]
    public void BuildLargeTrace_IsOneTraceOfExactlyTheRequestedSize_SplitIntoBoundedExports()
    {
        var (shaper, _, _) = Build();

        var (chunks, trace) = shaper.BuildLargeTrace(tenantIndex: 0, baseNanos: 1_700_000_000_000_000_000L, spanCount: 2_300, chunkSpans: 500);

        Assert.Equal(2_300, trace.Spans);
        Assert.Equal("large", trace.Kind);
        Assert.Equal(5, chunks.Count); // 4 x 500 + 300
        Assert.All(chunks, c => Assert.True(c.Records <= 500));
        Assert.Equal(2_300, chunks.Sum(c => c.Records));
        Assert.All(chunks, c => Assert.Equal(0, c.TenantIndex));

        var spans = chunks.SelectMany(c => c.Request.ResourceSpans).SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans).ToList();
        Assert.Equal(2_300, spans.Count);
        Assert.Single(spans.Select(s => Hex(s.TraceId)).Distinct()); // one trace
        Assert.Equal(trace.TraceIdHex, Hex(spans[0].TraceId));
        Assert.Equal(2_300, spans.Select(s => Hex(s.SpanId)).Distinct().Count()); // every span id unique
        Assert.Single(spans, s => s.ParentSpanId.IsEmpty); // one root

        // The ledger counts every span exactly once across the chunks.
        Assert.Equal(2_300, chunks.SelectMany(c => c.Entries).Sum(e => e.Rows));
    }

    [Fact]
    public void Seed_ParametersAreValidatedAndTagTheProfileName()
    {
        var profile = new Keryhe.Telemetry.StressTests.Scenarios.ScenarioProfile { Name = "smoke" };
        profile.ApplySeed(7, 5_000);
        Assert.Equal(7, profile.SeedDays);
        Assert.Equal(5_000, profile.SeedSpansPerDay);
        Assert.Equal("smoke-seed7d", profile.Name);

        Assert.Throws<ArgumentException>(() => new Keryhe.Telemetry.StressTests.Scenarios.ScenarioProfile().ApplySeed(0, null));
        Assert.Throws<ArgumentException>(() => new Keryhe.Telemetry.StressTests.Scenarios.ScenarioProfile().ApplySeed(61, null));
        Assert.Throws<ArgumentException>(() => new Keryhe.Telemetry.StressTests.Scenarios.ScenarioProfile().ApplySeed(3, 0));
    }
}
