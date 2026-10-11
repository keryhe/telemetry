using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Runs <see cref="ParquetCompactor"/> on the API host (README U3, phase 4 decision 1): wakes every <c>IntervalSeconds</c>, and only
/// while it holds the <see cref="ParquetCompactionLease"/> runs a pass (every partition once a <c>FullScanMinutes</c>, the recent days
/// otherwise). The lease is renewed during a long pass; losing it cancels the pass. <c>Telemetry:Parquet:Compaction:Enabled=false</c>
/// turns the worker off.
/// </summary>
public sealed class ParquetCompactionWorker(
    ParquetCompactor compactor, ParquetCompactionLease lease, IOptions<ParquetOptions> options, TimeProvider time,
    ILogger<ParquetCompactionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Compaction;
        if (!o.Enabled)
        {
            logger.LogInformation("Parquet compaction is disabled (Telemetry:Parquet:Compaction:Enabled=false)");
            return;
        }

        var interval = TimeSpan.FromSeconds(o.IntervalSeconds);
        DateTimeOffset? lastFullScan = null;
        var held = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var acquired = await lease.TryAcquireOrRenewAsync(stoppingToken);
                if (acquired != held)
                    logger.LogInformation(acquired ? "This instance now runs Parquet compaction" : "Another instance holds the Parquet compaction lease");
                held = acquired;
                if (acquired)
                {
                    var now = time.GetUtcNow();
                    var full = lastFullScan is null || now - lastFullScan >= TimeSpan.FromMinutes(o.FullScanMinutes);
                    await RunWithLeaseAsync(full, interval, stoppingToken);
                    if (full) lastFullScan = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Parquet compaction pass failed; trying again in {Interval}", interval);
            }

            try { await Task.Delay(interval, time, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>A pass with the lease renewed in the background; if another instance takes it over, the pass is cancelled.</summary>
    private async Task RunWithLeaseAsync(bool fullScan, TimeSpan interval, CancellationToken stoppingToken)
    {
        using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renew = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval, time);
                while (await timer.WaitForNextTickAsync(pass.Token))
                {
                    if (!await lease.TryAcquireOrRenewAsync(pass.Token))
                    {
                        logger.LogWarning("Lost the Parquet compaction lease during a pass; stopping the pass");
                        await pass.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* the pass ended */ }
            catch (Exception ex) { logger.LogWarning(ex, "Renewing the Parquet compaction lease failed"); }
        }, CancellationToken.None);

        try
        {
            var result = await compactor.RunPassAsync(fullScan, pass.Token);
            if (result.Minor + result.Major + result.Day + result.FilesDeleted + result.Failures > 0)
                logger.LogInformation(
                    "Parquet compaction pass: {Minor} minor, {Major} major, {Day} day merges, {Lost} lost races, {Deleted} files deleted, {Failures} failures",
                    result.Minor, result.Major, result.Day, result.Lost, result.FilesDeleted, result.Failures);
        }
        catch (OperationCanceledException) when (pass.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { /* lease lost */ }
        finally
        {
            await pass.CancelAsync();
            await renew;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await lease.ReleaseAsync(cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not release the Parquet compaction lease"); }
    }
}
