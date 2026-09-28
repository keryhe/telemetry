using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Caps the number of <c>/api/*/export</c> requests streaming at once, per API instance (decision
/// 17). Registered as a singleton (<c>AddKeryheTelemetryApi</c>) so every export controller action
/// shares the same slot count regardless of signal (logs/traces/metrics combined, not one gate per
/// signal) — the resource this protects is pooled database connections held for an export's whole
/// duration, which is shared across signals, not per-endpoint.
///
/// A <see cref="SemaphoreSlim"/> rather than the ASP.NET Core rate-limiting middleware: this needs
/// to release when the STREAM finishes (the response body write loop), not when the request
/// finishes routing/model-binding, and it needs to report "no slot" as a controller-level decision
/// (<c>429</c> with a body) before any query runs — both are simpler to express directly here than
/// through the middleware's own queueing/rejection pipeline.
/// </summary>
public sealed class ExportConcurrencyGate
{
    private readonly SemaphoreSlim _semaphore;

    public ExportConcurrencyGate(IOptions<ExportOptions> options)
    {
        var max = Math.Max(1, options.Value.MaxConcurrent);
        _semaphore = new SemaphoreSlim(max, max);
    }

    /// <summary>
    /// Tries to claim one export slot immediately (never waits — a full gate means 429, not a
    /// queued export). Returns an <see cref="IDisposable"/> that releases the slot, or null when no
    /// slot is available.
    /// </summary>
    public IDisposable? TryEnter()
        => _semaphore.Wait(0) ? new Releaser(_semaphore) : null;

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
