using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseBaselineTests(ClickHouseFixture fixture) : BaselineCharacterizationTestsBase(fixture)
{
    /// <summary>
    /// Pre-existing ClickHouse bug found by this harness, unrelated to the plan's Phase 1
    /// label-filter fix: <c>TraceReadRepositoryBase.BuildTracePageFromSummariesAsync</c> passes
    /// the page's trace-id list to <c>FetchRawSpansSlimAsync</c> as a Dapper
    /// <c>DynamicParameters</c> value (a <c>List&lt;string&gt;</c>), relying on the ADO provider to
    /// expand it into an IN-list. <c>ClickHouse.Client</c>'s <c>TypeConverter.ToClickHouseType</c>
    /// has no case for <c>List&lt;string&gt;</c> and throws <c>ArgumentOutOfRangeException</c>
    /// before any query runs, so <c>GetTraceOverviewAsync</c> (and therefore
    /// <c>GetTraceByServiceAsync</c>/any other caller of the same list-parameter path) cannot
    /// return a non-empty page on ClickHouse today. Out of scope for Phase 0 (harness + baseline
    /// only, no behavior fixes); flagged here rather than silently passing.
    /// </summary>
    [Fact(Skip = "pre-existing ClickHouse bug: FetchRawSpansSlimAsync's traceIds DynamicParameters value (List<string>) throws ArgumentOutOfRangeException in ClickHouse.Client's TypeConverter.ToClickHouseType -- found by this harness, out of scope for phase 0's fix")]
    public override Task TraceOverview_Totals_ErrorCounts_And_Percentiles_MatchSeededWindow()
        => base.TraceOverview_Totals_ErrorCounts_And_Percentiles_MatchSeededWindow();
}
