using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Scenarios;

namespace Keryhe.Telemetry.StressTests.Verification;

public static class CorrectnessStatus
{
    public const string Match = "Match", ExplainedByDrops = "ExplainedByDrops", ExplainedByAbandonedExports = "ExplainedByAbandonedExports", Mismatch = "Mismatch";
}

/// <summary>
/// One current-age (tenant, table) cell. <see cref="Expected"/> is what was accepted plus the re-delivered copies of tables that do not dedup
/// (log records and data points have no dedup key, so a re-delivery legitimately appears twice); re-deliveries that spans collapse
/// are counted in <see cref="CollapsedRedeliveries"/> and expected to be absent. <see cref="MaybeLanded"/> is the ledger's rows from exports the
/// client abandoned (deadline, cancel, run stop): a surplus up to that size is <see cref="CorrectnessStatus.ExplainedByAbandonedExports"/>.
/// </summary>
public sealed record CorrectnessRow(
    long TenantId, string Table, long Sent, long PersistedRedeliveries, long CollapsedRedeliveries,
    long Expected, long Actual, long Delta, string Status, long MaybeLanded = 0);

/// <param name="ActualMinusExpected">Sum of the deltas of the signal's cells. A shortfall no larger than <see cref="RecordsDropped"/> is explained by drops.</param>
public sealed record SignalDrops(string Signal, double RecordsDropped, long ActualMinusExpected);

public sealed record BackdatedRow(long TenantId, string Table, long Sent, long Remaining);

/// <summary>
/// Whether the retention sweep removed the backdated records. <see cref="Outcome"/> is <c>NoneSent</c>, <c>Removed</c>, <c>Remaining</c>
/// (a sweep started after every record was persisted and rows are still there), or <c>NotVerifiable</c> (no sweep started after quiescence within
/// the wait, so leftover rows prove nothing).
/// </summary>
public sealed record BackdatedCheck(
    string Outcome, DateTimeOffset? SweepStartedAt, double WaitedSeconds, IReadOnlyList<BackdatedRow> Rows, long RowsRemaining);

/// <summary>The ledger-versus-database comparison (stress-test plan, Phase 7). Mismatches are results, never exceptions.</summary>
public sealed record CorrectnessResult(
    DateTimeOffset At, long BackdatedCutoffNanos,
    IReadOnlyList<CorrectnessRow> Rows, IReadOnlyList<SignalDrops> Drops,
    int Mismatches, int ExplainedByDrops,
    long? PendingMergeDuplicates, BackdatedCheck Backdated, int ExplainedByAbandonedExports = 0);

/// <summary>Compares the sent ledger with the database counts. Pure.</summary>
public static class CorrectnessComparer
{
    public static (IReadOnlyList<CorrectnessRow> Rows, IReadOnlyList<SignalDrops> Drops) CompareCurrent(
        IReadOnlyList<LedgerCell> ledger, RowCounts counts, Func<string, double> recordsDropped)
    {
        var sent = ledger.Where(c => c.Age == RecordAge.Current).ToDictionary(c => (c.TenantId, c.Table));
        var actual = counts.Cells.Where(c => !c.Backdated).ToDictionary(c => (c.TenantId, c.Table), c => c.Rows);
        var keys = sent.Keys.Union(actual.Keys).OrderBy(k => k.TenantId).ThenBy(k => k.Table).ToList();

        var raw = new List<(long Tenant, string Table, LedgerCell? Cell, long Actual, long Expected)>();
        foreach (var key in keys)
        {
            sent.TryGetValue(key, out var cell);
            raw.Add((key.TenantId, key.Table, cell, actual.GetValueOrDefault(key), cell?.ExpectedRows ?? 0));
        }

        // A shortfall is only "explained by drops" when the signal as a whole lost no more than it reported dropping.
        string SignalOf(string table) => new CountedTable(table, "", false).Signal;
        var drops = raw.GroupBy(r => SignalOf(r.Table)).Select(g => new SignalDrops(g.Key, recordsDropped(g.Key), g.Sum(r => r.Actual - r.Expected))).ToList();
        var dropsBySignal = drops.ToDictionary(d => d.Signal);

        var rows = raw.Select(r =>
        {
            var delta = r.Actual - r.Expected;
            var d = dropsBySignal[SignalOf(r.Table)];
            var maybeLanded = r.Cell?.RowsMaybeLanded ?? 0;
            var status = delta == 0 ? CorrectnessStatus.Match
                : delta < 0 && d.RecordsDropped > 0 && d.ActualMinusExpected >= -d.RecordsDropped ? CorrectnessStatus.ExplainedByDrops
                : delta > 0 && delta <= maybeLanded ? CorrectnessStatus.ExplainedByAbandonedExports
                : CorrectnessStatus.Mismatch;
            return new CorrectnessRow(r.Tenant, r.Table, r.Cell?.Rows ?? 0, r.Cell?.DuplicateRowsPersisted ?? 0, r.Cell?.DuplicateRowsCollapsed ?? 0,
                r.Expected, r.Actual, delta, status, maybeLanded);
        }).ToList();
        return (rows, drops);
    }

    public static BackdatedCheck CompareBackdated(
        IReadOnlyList<LedgerCell> ledger, RowCounts counts, DateTimeOffset? sweepStartedAfterQuiesce, double waitedSeconds)
    {
        var sent = ledger.Where(c => c.Age == RecordAge.Backdated && c.Rows > 0).ToList();
        var remaining = counts.Cells.Where(c => c.Backdated).ToDictionary(c => (c.TenantId, c.Table), c => c.Rows);
        var keys = sent.Select(c => (c.TenantId, c.Table)).Union(remaining.Keys).OrderBy(k => k.TenantId).ThenBy(k => k.Table).ToList();
        var rows = keys.Select(k => new BackdatedRow(k.TenantId, k.Table, sent.FirstOrDefault(c => (c.TenantId, c.Table) == k)?.Rows ?? 0, remaining.GetValueOrDefault(k))).ToList();
        var left = rows.Sum(r => r.Remaining);

        var outcome = sent.Count == 0 && left == 0 ? "NoneSent"
            : sweepStartedAfterQuiesce is null ? "NotVerifiable"
            : left == 0 ? "Removed" : "Remaining";
        return new BackdatedCheck(outcome, sweepStartedAfterQuiesce, waitedSeconds, rows, left);
    }

    public static CorrectnessResult Build(
        DateTimeOffset at, long cutoffNanos, IReadOnlyList<LedgerCell> ledger, RowCounts counts,
        Func<string, double> recordsDropped, DateTimeOffset? sweepStartedAfterQuiesce, double waitedSeconds)
    {
        var (rows, drops) = CompareCurrent(ledger, counts, recordsDropped);
        long? pending = counts.RawSpanRows is { } raw
            ? raw - counts.Cells.Where(c => !c.Backdated && c.Table == "spans").Sum(c => c.Rows) - counts.Cells.Where(c => c.Backdated && c.Table == "spans").Sum(c => c.Rows)
            : null;
        return new CorrectnessResult(at, cutoffNanos, rows, drops,
            rows.Count(r => r.Status == CorrectnessStatus.Mismatch), rows.Count(r => r.Status == CorrectnessStatus.ExplainedByDrops),
            pending, CompareBackdated(ledger, counts, sweepStartedAfterQuiesce, waitedSeconds),
            rows.Count(r => r.Status == CorrectnessStatus.ExplainedByAbandonedExports));
    }
}

/// <summary>Runs the check against a live scenario, after the quiesce step and before the hosts are stopped.</summary>
public static class CorrectnessRunner
{
    /// <summary>Well past "current" (minutes old) and well short of the backdated age (200+ days), so neither is ambiguous.</summary>
    public static readonly TimeSpan BackdatedCutoffAge = TimeSpan.FromDays(30);

    public static async Task<CorrectnessResult> RunAsync(
        DatabaseObserverSession database, IReadOnlyList<LedgerCell> ledger, HostSet hosts, DateTimeOffset quiesceEnd,
        ScenarioProfile profile, Action<string> log, CancellationToken ct)
    {
        // The retention sweep is the only thing that removes backdated rows, and there is no trigger for it (decision 11), so wait for
        // the next natural sweep that STARTED after everything was persisted: only then do leftover backdated rows mean something.
        DateTimeOffset? sweepStart = null;
        var waited = 0.0;
        var hasBackdated = ledger.Any(c => c.Age == RecordAge.Backdated && c.Rows > 0);
        if (hasBackdated)
        {
            var limit = TimeSpan.FromSeconds(Math.Min(profile.RetentionIntervalSeconds * 2 + 60, profile.BackdatedCheckMaxWaitSeconds));
            var began = DateTimeOffset.UtcNow;
            log($"correctness: waiting up to {limit.TotalSeconds:F0}s for a retention sweep that starts after quiescence");
            while (true)
            {
                sweepStart = SweepStartedAfter(hosts, quiesceEnd);
                if (sweepStart is not null || DateTimeOffset.UtcNow - began >= limit) break;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            waited = (DateTimeOffset.UtcNow - began).TotalSeconds;
        }

        var cutoff = ToUnixNanos(DateTimeOffset.UtcNow - BackdatedCutoffAge);
        var counts = await database.CountRowsAsync(cutoff, ct);

        // ClickHouse applies its deletes as asynchronous mutations, so leftovers get a little time to disappear before they are reported.
        var retryUntil = DateTimeOffset.UtcNow.AddSeconds(60);
        while (sweepStart is not null && counts.Cells.Any(c => c.Backdated && c.Rows > 0) && DateTimeOffset.UtcNow < retryUntil)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            counts = await database.CountRowsAsync(cutoff, ct);
        }

        return CorrectnessComparer.Build(DateTimeOffset.UtcNow, cutoff, ledger, counts, signal => RecordsDropped(hosts, signal), sweepStart, waited);
    }

    /// <summary>The start time (completion minus elapsed) of the first sweep that began after <paramref name="after"/>, on any host.</summary>
    public static DateTimeOffset? SweepStartedAfter(HostSet hosts, DateTimeOffset after) =>
        hosts.Hosts.SelectMany(h => h.Scanner.Summarize().RetentionSweeps)
            .Select(s => s.At - TimeSpan.FromMilliseconds(s.ElapsedMs))
            .Where(started => started > after).OrderBy(t => t).Cast<DateTimeOffset?>().FirstOrDefault();

    /// <summary>Records dropped for a signal: the EventPipe counter's total, or the logged dropped batches if the counter was never read.</summary>
    public static double RecordsDropped(HostSet hosts, string signal)
    {
        var counter = hosts.Hosts.Where(h => h.Metrics is not null)
            .Sum(h => h.Metrics!.Store.Total("keryhe.telemetry.ingestion.records_dropped", "signal", signal));
        var logged = hosts.Hosts.Sum(h => h.Scanner.Summarize().BatchesDropped.Where(b => b.Signal == signal).Sum(b => (double)b.Count));
        return Math.Max(counter, logged);
    }

    private static long ToUnixNanos(DateTimeOffset t) => (t.ToUnixTimeMilliseconds()) * 1_000_000L;
}
