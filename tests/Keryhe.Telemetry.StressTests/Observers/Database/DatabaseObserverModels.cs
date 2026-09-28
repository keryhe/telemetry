namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>One statement blocked on a lock, with what it waits for and who holds it. Ids are the provider's session/backend/thread ids.</summary>
public sealed record LockWait(
    string? BlockedId, string? BlockingId, string? BlockedQuery, string? BlockingQuery,
    string? Resource, string? WaitType, double WaitMs);

/// <summary>A statement that has been running for a long time (ClickHouse's stand-in for a blocking chain).</summary>
public sealed record LongQuery(string Query, double ElapsedMs);

/// <summary>A sampled numeric reading. <see cref="Label"/> splits one gauge by table, lock mode, hypertable and so on.</summary>
public sealed record Gauge(string Name, string? Label, double Value);

/// <summary>One timestamped sample of a provider's lock/pressure state. A failed sample carries <see cref="Error"/> and no data; sampling never throws.</summary>
public sealed record LockSample(
    DateTimeOffset At, IReadOnlyList<LockWait> Waits, IReadOnlyList<Gauge> Gauges,
    IReadOnlyList<LongQuery> LongRunning, string? Error = null);

/// <summary>A pass/fail statement about the run that the report lists explicitly (for example "SqlServer API reads run under SNAPSHOT").</summary>
public sealed record ObserverCheck(string Name, bool Passed, string Detail);

/// <summary>
/// What the lock observer learned over the whole run: deadlock count, counter deltas since
/// <see cref="ILockObserver.BeginAsync"/>, deadlock details (server log excerpts) and artifacts
/// (name to content, e.g. SQL Server deadlock graph XML) for the runner to write to disk.
/// </summary>
public sealed record LockSummary(
    long Deadlocks,
    IReadOnlyDictionary<string, double> CounterDeltas,
    IReadOnlyList<string> DeadlockDetails,
    IReadOnlyList<string> LockWaitLogLines,
    IReadOnlyDictionary<string, string> Artifacts,
    IReadOnlyList<ObserverCheck> Checks);

public sealed record StatementStat(string Query, long Calls, double TotalMs, double MeanMs, double MaxMs, long Rows);

/// <summary>The slowest statements since the last reset, ranked by total time and by mean time. <see cref="Source"/> names the provider view they came from.</summary>
public sealed record StatementStatsSnapshot(string Source, IReadOnlyList<StatementStat> ByTotal, IReadOnlyList<StatementStat> ByMean);

/// <summary>A table's row count and on-disk size. <see cref="RowsApproximate"/> is set where the provider only offers an estimate.</summary>
public sealed record TableStat(string Table, long? Rows, bool RowsApproximate, long? Bytes);

public sealed record ContainerStatsSample(
    DateTimeOffset At, double CpuCores, long MemoryBytes, long MemoryLimitBytes,
    long BlockReadBytes, long BlockWriteBytes, long NetRxBytes, long NetTxBytes);

/// <summary>Everything the database observers gathered over one run; serializable as-is into the run's JSON result.</summary>
public sealed record DatabaseObservation(
    string Provider,
    IReadOnlyList<LockSample> LockSamples,
    LockSummary Locks,
    StatementStatsSnapshot Statements,
    IReadOnlyList<TableStat> Tables,
    IReadOnlyList<ContainerStatsSample> ContainerStats);
