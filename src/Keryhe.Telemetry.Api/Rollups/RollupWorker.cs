using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Api.Rollups;

/// <summary>
/// Periodic background worker driving the log summary rollups (list-pages-server-side plan, Phase
/// 2, decisions 37-38). Structurally mirrors <c>RetentionWorker</c>: a fresh DI scope per cycle,
/// an <c>Enabled</c> flag, an interval-driven <see cref="Task.Delay"/> loop, and a per-cycle
/// try/catch that logs and continues rather than crashing.
///
/// Written so a later phase can add a "traces" signal alongside "logs" without restructuring —
/// <see cref="RunCycleAsync"/> is the per-signal entry point and currently calls only
/// <see cref="RunLogsSignalAsync"/>.
///
/// <see cref="RunCycleAsync"/> is public and takes an <see cref="IServiceProvider"/> + a caller-
/// supplied "now" specifically so it can be invoked directly and repeatedly from a test without
/// waiting on real wall-clock timing or a hosted-service lifetime.
/// </summary>
public sealed class RollupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<RollupOptions> options,
    ILogger<RollupWorker> logger) : BackgroundService
{
    private readonly RollupOptions _options = options.Value;

    private static readonly string Owner = $"{Environment.MachineName}:{Environment.ProcessId}";

    private const long NanosPerMinute = 60_000_000_000L;
    private const long NanosPerHour = 3_600_000_000_000L;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Rollup worker disabled via configuration.");
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        logger.LogInformation("Rollup worker started (interval: {Interval}s).", _options.IntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await RunCycleAsync(scope.ServiceProvider, DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error during rollup cycle.");
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

        logger.LogInformation("Rollup worker stopped.");
    }

    public async Task RunCycleAsync(IServiceProvider services, DateTime now, CancellationToken ct = default)
    {
        var repository = services.GetRequiredService<IRollupRepository>();
        await RunLogsSignalAsync(repository, now, ct);
    }

    private async Task RunLogsSignalAsync(IRollupRepository repo, DateTime now, CancellationToken ct)
    {
        var minuteRolledUntil = await RollLogMinutesAsync(repo, now, ct);
        await RollLogHoursAsync(repo, now, minuteRolledUntil, ct);
    }

    /// <summary>
    /// Rolls every closed minute up to <c>now - SettleSeconds</c>, then re-rolls each minute once
    /// it reaches <c>RepassMinutes</c> old. Returns the granularity's current
    /// <c>repassed_until_unix_nano</c> (the boundary <see cref="RollLogHoursAsync"/> needs) — or,
    /// when this instance didn't win the lease, whatever the last successful roll recorded.
    /// </summary>
    private async Task<long> RollLogMinutesAsync(IRollupRepository repo, DateTime now, CancellationToken ct)
    {
        const string granularity = "minute";
        var state = await repo.GetStateAsync("logs", granularity, ct) ?? new RollupStateInfo();

        if (!await repo.TryClaimLeaseAsync("logs", granularity, Owner, TimeSpan.FromSeconds(_options.LeaseSeconds), ct))
            return state.RepassedUntilUnixNano;

        if (state.CoverageStartUnixNano is null)
        {
            var backfillStart = AlignDown(TimeConversion.DateTimeToUnixNano(now.AddHours(-_options.BackfillHours)), NanosPerMinute);
            await repo.SetCoverageStartAsync("logs", granularity, backfillStart, ct);
            state.CoverageStartUnixNano = backfillStart;
        }

        var coverageStart = state.CoverageStartUnixNano.Value;
        var rolledUntil = Math.Max(state.RolledUntilUnixNano, coverageStart);

        var settleBoundary = AlignDown(TimeConversion.DateTimeToUnixNano(now.AddSeconds(-_options.SettleSeconds)), NanosPerMinute);
        if (settleBoundary > rolledUntil)
        {
            await repo.RollLogMinutesAsync(rolledUntil, settleBoundary, ct);
            rolledUntil = settleBoundary;
            await repo.AdvanceRolledUntilAsync("logs", granularity, rolledUntil, ct);
        }

        var repassedUntil = Math.Max(state.RepassedUntilUnixNano, coverageStart);
        var repassBoundary = AlignDown(TimeConversion.DateTimeToUnixNano(now.AddMinutes(-_options.RepassMinutes)), NanosPerMinute);
        var repassTo = Math.Min(repassBoundary, rolledUntil);
        if (repassTo > repassedUntil)
        {
            await repo.RollLogMinutesAsync(repassedUntil, repassTo, ct);
            repassedUntil = repassTo;
            await repo.AdvanceRepassedUntilAsync("logs", granularity, repassedUntil, ct);
        }

        return repassedUntil;
    }

    /// <summary>
    /// Rolls each closed hour from its minute rows, once the hour's last minute has been
    /// re-rolled — <paramref name="minuteRepassedUntil"/> is exactly that boundary.
    /// </summary>
    private async Task RollLogHoursAsync(IRollupRepository repo, DateTime now, long minuteRepassedUntil, CancellationToken ct)
    {
        const string granularity = "hour";
        if (!await repo.TryClaimLeaseAsync("logs", granularity, Owner, TimeSpan.FromSeconds(_options.LeaseSeconds), ct))
            return;

        var state = await repo.GetStateAsync("logs", granularity, ct) ?? new RollupStateInfo();

        if (state.CoverageStartUnixNano is null)
        {
            var backfillStart = AlignDown(TimeConversion.DateTimeToUnixNano(now.AddHours(-_options.BackfillHours)), NanosPerHour);
            await repo.SetCoverageStartAsync("logs", granularity, backfillStart, ct);
            state.CoverageStartUnixNano = backfillStart;
        }

        var coverageStart = state.CoverageStartUnixNano.Value;
        var rolledUntil = Math.Max(state.RolledUntilUnixNano, coverageStart);

        var hourBoundary = AlignDown(minuteRepassedUntil, NanosPerHour);
        if (hourBoundary > rolledUntil)
        {
            await repo.RollLogHoursAsync(rolledUntil, hourBoundary, ct);
            await repo.AdvanceRolledUntilAsync("logs", granularity, hourBoundary, ct);
        }
    }

    private static long AlignDown(long nano, long bucketSize) => nano / bucketSize * bucketSize;
}
