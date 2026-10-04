using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Appends the summary rollups (plans/summary-rollups.md): every
/// <see cref="RollupOptions.FlushIntervalSeconds"/> it drains the minutes of
/// <see cref="RollupAccumulator"/> that have closed and appends them through
/// <see cref="IRollupStore"/>, sorted by key and in batches under
/// <see cref="RollupOptions.MaxBatchSize"/> (the <see cref="MetricTouchWorker"/> reasons: one lock
/// order across collectors, no lock escalation on SQL Server).
///
/// A failed append keeps its rows in memory and retries next interval, up to
/// <see cref="RollupOptions.MaxBufferedRows"/>; beyond that the oldest are dropped and counted on
/// <c>rollup_rows_dropped</c>. Registered BEFORE <see cref="TelemetryIngestionWorker"/>, so the host
/// stops it AFTER the ingestion drain, and <see cref="StopAsync"/> then writes everything left
/// (open minutes included) within the host's shutdown deadline. What is lost to a crash or a missed
/// deadline is the slice held in memory; the stored spans and logs are unaffected.
/// </summary>
public sealed class RollupWorker(
    IServiceScopeFactory scopeFactory,
    RollupAccumulator accumulator,
    IOptions<RollupOptions> options,
    IngestionMetrics metrics,
    ILogger<RollupWorker> logger) : BackgroundService
{
    private readonly RollupOptions _options = options.Value;
    private readonly List<RequestRollupRow> _pendingRequests = [];
    private readonly List<LogRollupRow> _pendingLogs = [];

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Settled here, not in ExecuteAsync, so it is done before the ingestion worker (registered
        // after this one, so started after it) begins feeding the accumulator.
        using (var scope = scopeFactory.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IRollupStore>() is not { } store || store.FedByViews)
                accumulator.Enabled = false;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.FlushIntervalSeconds));
        logger.LogInformation("Rollup worker started (interval: {Interval}s, accumulator {State}).",
            interval.TotalSeconds, accumulator.Enabled ? "enabled" : "disabled, the database maintains the rollup");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (accumulator.Enabled)
            {
                var cutoff = ToUnixNano(accumulator.Clock.GetUtcNow()) - _options.CloseGraceSeconds * 1_000_000_000L;
                var (requests, logs) = accumulator.DrainClosed(cutoff);
                await FlushAsync(requests, logs, stoppingToken);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (!accumulator.Enabled) return;
        var (requests, logs) = accumulator.DrainAll();
        await FlushAsync(requests, logs, cancellationToken);

        var lost = _pendingRequests.Count + _pendingLogs.Count;
        if (lost > 0)
        {
            metrics.RecordRollupRowsDropped("request", _pendingRequests.Count);
            metrics.RecordRollupRowsDropped("log", _pendingLogs.Count);
            logger.LogWarning("{Count} rollup rows were not written before shutdown", lost);
        }
    }

    public async Task FlushAsync(List<RequestRollupRow> requests, List<LogRollupRow> logs, CancellationToken ct)
    {
        _pendingRequests.AddRange(requests);
        _pendingLogs.AddRange(logs);
        if (_pendingRequests.Count == 0 && _pendingLogs.Count == 0) return;

        var started = Stopwatch.GetTimestamp();
        var outcome = "ok";
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
            var batch = Math.Max(1, _options.MaxBatchSize);

            while (_pendingRequests.Count > 0)
            {
                var chunk = _pendingRequests.GetRange(0, Math.Min(batch, _pendingRequests.Count));
                await store.AppendRequestsAsync(chunk, ct);
                _pendingRequests.RemoveRange(0, chunk.Count);
                metrics.RecordRollupRowsWritten("request", chunk.Count);
            }
            while (_pendingLogs.Count > 0)
            {
                var chunk = _pendingLogs.GetRange(0, Math.Min(batch, _pendingLogs.Count));
                await store.AppendLogsAsync(chunk, ct);
                _pendingLogs.RemoveRange(0, chunk.Count);
                metrics.RecordRollupRowsWritten("log", chunk.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "failed";
        }
        catch (Exception ex)
        {
            outcome = "failed";
            logger.LogError(ex, "Error appending rollup rows -- {Count} rows held for the next interval",
                _pendingRequests.Count + _pendingLogs.Count);
            TrimBuffer();
        }
        finally
        {
            metrics.RecordRollupFlushDuration(Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome);
        }
    }

    private void TrimBuffer()
    {
        var over = _pendingRequests.Count + _pendingLogs.Count - _options.MaxBufferedRows;
        if (over <= 0) return;

        // Oldest first: the lists are in drain order, so the front holds the oldest minutes.
        var fromRequests = Math.Min(over, _pendingRequests.Count);
        _pendingRequests.RemoveRange(0, fromRequests);
        var fromLogs = Math.Min(over - fromRequests, _pendingLogs.Count);
        _pendingLogs.RemoveRange(0, fromLogs);
        metrics.RecordRollupRowsDropped("request", fromRequests);
        metrics.RecordRollupRowsDropped("log", fromLogs);
        logger.LogWarning("Rollup buffer full -- dropped {Count} oldest rows", fromRequests + fromLogs);
    }

    private static long ToUnixNano(DateTimeOffset t) => (t.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
}
