namespace Keryhe.Telemetry.Data.Threading;

internal static class LockSignal
{
    private static readonly SemaphoreSlim ResourceSemaphore = new(1, 1);
    private static readonly SemaphoreSlim ScopeSemaphore = new(1, 1);

    public static async Task<IDisposable> AcquireResourceAsync(CancellationToken cancellationToken)
    {
        await ResourceSemaphore.WaitAsync(cancellationToken);
        return new Releaser(ResourceSemaphore);
    }

    public static async Task<IDisposable> AcquireScopeAsync(CancellationToken cancellationToken)
    {
        await ScopeSemaphore.WaitAsync(cancellationToken);
        return new Releaser(ScopeSemaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            _semaphore.Release();
        }
    }
}