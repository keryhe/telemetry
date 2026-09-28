namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>Samples a provider's lock waits and pressure signals while a scenario runs.</summary>
public interface ILockObserver
{
    string Provider { get; }

    /// <summary>Captures the baselines that <see cref="EndAsync"/> computes deltas against (cumulative deadlock and lock-wait counters, the container-log start).</summary>
    Task BeginAsync(CancellationToken cancellationToken);

    /// <summary>Takes one sample. Called about once a second.</summary>
    Task<LockSample> SampleAsync(CancellationToken cancellationToken);

    /// <summary>Computes the run's deadlocks and counter deltas and gathers their details.</summary>
    Task<LockSummary> EndAsync(CancellationToken cancellationToken);
}

/// <summary>Collects the provider's own statement statistics: reset at the start of the measured window, snapshot at the end.</summary>
public interface IStatementStatsCollector
{
    Task ResetAsync(CancellationToken cancellationToken);

    Task<StatementStatsSnapshot> SnapshotAsync(int top, CancellationToken cancellationToken);
}

/// <summary>Row count and size for every table, read once at the end of a run for context in the report.</summary>
public interface ITableStatsReader
{
    Task<IReadOnlyList<TableStat>> ReadAsync(CancellationToken cancellationToken);
}
