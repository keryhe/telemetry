using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Verification;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>The correctness comparison (stress-test plan, Phase 7): what counts as a match, a shortfall drops explain, a mismatch, and a verifiable retention result.</summary>
public class CorrectnessTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static LedgerCell Cell(long tenant, string table, long rows, RecordAge age = RecordAge.Current, long collapsed = 0, long persisted = 0) =>
        new(tenant, table, age, rows, collapsed, persisted, 0, 0);

    private static RowCountCell Db(long tenant, string table, long rows, bool backdated = false) => new(tenant, table, backdated, rows);

    private static CorrectnessResult Run(LedgerCell[] ledger, RowCountCell[] db, double dropped = 0, DateTimeOffset? sweep = null, long? rawSpans = null) =>
        CorrectnessComparer.Build(At, 0, ledger, new RowCounts(db, rawSpans), _ => dropped, sweep, 0);

    [Fact]
    public void Exact_counts_match_and_persisted_redeliveries_are_expected_twice()
    {
        var result = Run(
            [Cell(1, "log_records", 100, persisted: 10), Cell(1, "spans", 50, collapsed: 5)],
            [Db(1, "log_records", 110), Db(1, "spans", 50)]);
        Assert.All(result.Rows, r => Assert.Equal(CorrectnessStatus.Match, r.Status));
        Assert.Equal(0, result.Mismatches);
        var logs = result.Rows.Single(r => r.Table == "log_records");
        Assert.Equal((100L, 10L, 110L), (logs.Sent, logs.PersistedRedeliveries, logs.Expected));
        Assert.Equal(5, result.Rows.Single(r => r.Table == "spans").CollapsedRedeliveries);
    }

    [Fact]
    public void A_collapsed_redelivery_that_persisted_shows_as_a_surplus_mismatch()
    {
        var result = Run([Cell(1, "spans", 50, collapsed: 5)], [Db(1, "spans", 55)]);
        var row = Assert.Single(result.Rows);
        Assert.Equal((CorrectnessStatus.Mismatch, 5L), (row.Status, row.Delta));
    }

    [Fact]
    public void A_shortfall_is_explained_only_up_to_the_records_dropped_for_that_signal()
    {
        LedgerCell[] ledger = [Cell(1, "log_records", 1000), Cell(2, "log_records", 1000), Cell(1, "spans", 500)];
        RowCountCell[] db = [Db(1, "log_records", 960), Db(2, "log_records", 1000), Db(1, "spans", 495)];

        var result = CorrectnessComparer.Build(At, 0, ledger, new RowCounts(db, null), s => s == "logs" ? 40 : 0, null, 0);
        Assert.Equal(CorrectnessStatus.ExplainedByDrops, result.Rows.Single(r => r.Table == "log_records" && r.TenantId == 1).Status);
        Assert.Equal(CorrectnessStatus.Match, result.Rows.Single(r => r.TenantId == 2).Status);
        Assert.Equal(CorrectnessStatus.Mismatch, result.Rows.Single(r => r.Table == "spans").Status); // traces dropped nothing
        Assert.Equal((1, 1), (result.Mismatches, result.ExplainedByDrops));

        var lostMore = CorrectnessComparer.Build(At, 0, ledger, new RowCounts(db, null), s => s == "logs" ? 10 : 0, null, 0);
        Assert.Equal(CorrectnessStatus.Mismatch, lostMore.Rows.Single(r => r.Table == "log_records" && r.TenantId == 1).Status);
    }

    [Fact]
    public void Rows_in_the_database_the_ledger_never_sent_are_mismatches_and_backdated_rows_are_not_counted_as_current()
    {
        var result = Run([Cell(1, "spans", 10)], [Db(1, "spans", 10), Db(1, "spans", 4, backdated: true), Db(9, "log_records", 3)]);
        Assert.Equal(CorrectnessStatus.Mismatch, result.Rows.Single(r => r.TenantId == 9).Status);
        Assert.Equal(CorrectnessStatus.Match, result.Rows.Single(r => r.TenantId == 1).Status);
    }

    [Fact]
    public void Data_point_tables_roll_up_to_the_metrics_signal()
    {
        LedgerCell[] ledger = [Cell(1, "gauge_data_points", 100), Cell(1, "histogram_data_points", 100)];
        RowCountCell[] db = [Db(1, "gauge_data_points", 95), Db(1, "histogram_data_points", 100)];
        var result = CorrectnessComparer.Build(At, 0, ledger, new RowCounts(db, null), s => s == "metrics" ? 5 : 0, null, 0);
        Assert.Equal(0, result.Mismatches);
        Assert.Equal(("metrics", 5d, -5L), (result.Drops.Single().Signal, result.Drops.Single().RecordsDropped, result.Drops.Single().ActualMinusExpected));
    }

    [Theory]
    [InlineData(true, 0, "Removed")]
    [InlineData(true, 3, "Remaining")]
    [InlineData(false, 3, "NotVerifiable")]
    [InlineData(false, 0, "NotVerifiable")]
    public void Backdated_outcome_depends_on_whether_a_sweep_started_after_quiescence(bool sweepSeen, long remaining, string outcome)
    {
        var db = remaining > 0 ? new[] { Db(1, "spans", remaining, backdated: true) } : [];
        var check = Run([Cell(1, "spans", 20, RecordAge.Backdated)], db, sweep: sweepSeen ? At : null).Backdated;
        Assert.Equal(outcome, check.Outcome);
        Assert.Equal(remaining, check.RowsRemaining);
        Assert.Equal(20, Assert.Single(check.Rows).Sent);
    }

    [Fact]
    public void No_backdated_records_sent_and_none_found_is_reported_as_such()
    {
        Assert.Equal("NoneSent", Run([Cell(1, "spans", 20)], [Db(1, "spans", 20)]).Backdated.Outcome);
    }

    [Fact]
    public void ClickHouse_pending_merge_duplicates_are_raw_spans_minus_deduplicated_spans()
    {
        var result = Run([Cell(1, "spans", 50)], [Db(1, "spans", 50), Db(1, "spans", 6, backdated: true)], rawSpans: 63);
        Assert.Equal(7, result.PendingMergeDuplicates);
        Assert.Null(Run([Cell(1, "spans", 50)], [Db(1, "spans", 50)]).PendingMergeDuplicates);
    }
}
