using Keryhe.Telemetry.StressTests.Observers.Containers;
using Keryhe.Telemetry.TestInfrastructure.Containers;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>
/// Everything Phase 4 observes for one scenario, started and stopped together: the 1s lock sampler, the
/// statement-stats collector, the container stats stream, and (at the end) table sizes. The scenario runner
/// starts it before warm-up, calls <see cref="ResetStatementStatsAsync"/> at the start of the measured
/// window so the slowest-SQL ranking excludes warm-up, and stops it after quiescence. Every sample is
/// timestamped, so the report lines it up with the load and browser timelines.
/// </summary>
public sealed class DatabaseObserverSession : IAsyncDisposable
{
    private readonly DatabaseObserverBase _observer;
    private readonly LockSampler _locks;
    private readonly ContainerStatsSampler _stats;
    private IReadOnlyList<ServerSetting> _settings = [];

    private DatabaseObserverSession(DatabaseObserverBase observer, string containerId, TimeSpan interval)
    {
        _observer = observer;
        _locks = new LockSampler(observer, interval);
        _stats = new ContainerStatsSampler(containerId);
    }

    public string Provider => _observer.Provider;

    public static async Task<DatabaseObserverSession> StartAsync(
        string provider, ProviderContainer container, TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        var session = new DatabaseObserverSession(DatabaseObserverFactory.Create(provider, container), container.ContainerId, interval ?? TimeSpan.FromSeconds(1));
        await session._observer.BeginAsync(cancellationToken);
        session._settings = await session._observer.ReadSettingsAsync(cancellationToken);
        await session._observer.ResetAsync(cancellationToken);
        session._stats.Start();
        session._locks.Start();
        return session;
    }

    /// <summary>Counts the rows in every telemetry table per tenant and age (the correctness check's database side).</summary>
    public Task<RowCounts> CountRowsAsync(long backdatedCutoffNanos, CancellationToken cancellationToken = default) =>
        _observer.CountRowsAsync(backdatedCutoffNanos, cancellationToken);

    /// <summary>Only the summary rollup tables' counts (plans/summary-rollups.md): cheap enough to poll while the rollup catches up.</summary>
    public Task<RowCounts> CountRollupRowsAsync(long backdatedCutoffNanos, CancellationToken cancellationToken = default) =>
        _observer.CountRollupRowsAsync(backdatedCutoffNanos, cancellationToken);

    /// <summary>The effective server settings read when the session started.</summary>
    public IReadOnlyList<ServerSetting> Settings => _settings;

    /// <summary>Restarts the statement statistics window (and the diagnostics' counter baselines), normally at the start of the measured window.</summary>
    public Task ResetStatementStatsAsync(CancellationToken cancellationToken = default) => _observer.ResetAsync(cancellationToken);

    public async Task<DatabaseObservation> StopAsync(int topStatements = 20, CancellationToken cancellationToken = default)
    {
        var samples = await _locks.StopAsync();
        var containerStats = await _stats.StopAsync();
        var summary = await _observer.EndAsync(cancellationToken);
        var statements = await _observer.SnapshotAsync(topStatements, cancellationToken);
        var tables = await _observer.ReadAsync(cancellationToken);
        var diagnostics = await _observer.ReadDiagnosticsAsync(cancellationToken);
        return new DatabaseObservation(Provider, samples, summary, statements, tables, containerStats, _settings, diagnostics);
    }

    public async ValueTask DisposeAsync()
    {
        await _locks.DisposeAsync();
        await _stats.DisposeAsync();
    }
}
