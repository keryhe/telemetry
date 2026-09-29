using System.Collections.Concurrent;

namespace Keryhe.Telemetry.StressTests.Load;

public enum RecordAge { Current, Backdated }

/// <summary>
/// Rows one export contributes to one table. <see cref="Redelivery"/> marks the second, identical
/// send of an export; <see cref="Dedups"/> says whether the table collapses such a duplicate
/// (spans are unique on (trace_id, span_id); log records and data points have no dedup key, so a
/// re-delivered one is expected to appear twice).
/// </summary>
public readonly record struct LedgerEntry(string Table, RecordAge Age, long Rows, bool Redelivery, bool Dedups);

/// <summary>What one (tenant, table, age) cell of the ledger holds.</summary>
/// <param name="RowsMaybeLanded">
/// Rows of exports the client gave up on (deadline exceeded, cancelled, or abandoned when the run stopped) that the server may still have
/// enqueued and persisted: an abandoned export is not a rejected one. Counts only rows that would add to the table (a re-delivered span collapses).
/// A surplus in the database no larger than this is explained, not a mismatch.
/// </param>
public sealed record LedgerCell(
    long TenantId, string Table, RecordAge Age,
    long Rows, long DuplicateRowsCollapsed, long DuplicateRowsPersisted,
    long RowsRejected, long RowsFailed, long RowsMaybeLanded = 0)
{
    /// <summary>Rows the database should hold for this cell once ingestion is quiet and nothing was dropped.</summary>
    public long ExpectedRows => Rows + DuplicateRowsPersisted;
}

/// <summary>
/// The sent ledger (stress-test plan, Phase 2): records the server ACCEPTED, per tenant, table and
/// age, which Phase 7's correctness check compares with the database. An export the server rejected
/// (partial success with rejected records) or that failed at the transport level is counted
/// separately, never as accepted.
/// </summary>
public sealed class SentLedger
{
    private sealed class Cell
    {
        public long Rows, CollapsedDuplicates, PersistedDuplicates, Rejected, Failed, MaybeLanded;
    }

    private readonly ConcurrentDictionary<(long Tenant, string Table, RecordAge Age), Cell> _cells = new();

    public void RecordAccepted(long tenantId, IEnumerable<LedgerEntry> entries)
    {
        foreach (var e in entries)
        {
            var cell = CellFor(tenantId, e);
            if (!e.Redelivery) Interlocked.Add(ref cell.Rows, e.Rows);
            else if (e.Dedups) Interlocked.Add(ref cell.CollapsedDuplicates, e.Rows);
            else Interlocked.Add(ref cell.PersistedDuplicates, e.Rows);
        }
    }

    public void RecordRejected(long tenantId, IEnumerable<LedgerEntry> entries)
    {
        foreach (var e in entries) Interlocked.Add(ref CellFor(tenantId, e).Rejected, e.Rows);
    }

    public void RecordFailed(long tenantId, IEnumerable<LedgerEntry> entries)
    {
        foreach (var e in entries) Interlocked.Add(ref CellFor(tenantId, e).Failed, e.Rows);
    }

    /// <summary>Records an export the client abandoned before hearing back, which the server may nevertheless have accepted.</summary>
    public void RecordMaybeLanded(long tenantId, IEnumerable<LedgerEntry> entries)
    {
        foreach (var e in entries.Where(e => !(e.Redelivery && e.Dedups)))
            Interlocked.Add(ref CellFor(tenantId, e).MaybeLanded, e.Rows);
    }

    private Cell CellFor(long tenantId, LedgerEntry e) => _cells.GetOrAdd((tenantId, e.Table, e.Age), _ => new Cell());

    public IReadOnlyList<LedgerCell> Snapshot() =>
        _cells.OrderBy(c => c.Key.Tenant).ThenBy(c => c.Key.Table).ThenBy(c => c.Key.Age)
            .Select(c => new LedgerCell(c.Key.Tenant, c.Key.Table, c.Key.Age,
                Volatile.Read(ref c.Value.Rows), Volatile.Read(ref c.Value.CollapsedDuplicates),
                Volatile.Read(ref c.Value.PersistedDuplicates), Volatile.Read(ref c.Value.Rejected),
                Volatile.Read(ref c.Value.Failed), Volatile.Read(ref c.Value.MaybeLanded)))
            .ToList();
}
