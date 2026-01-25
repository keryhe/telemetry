namespace Keryhe.Telemetry.Data.Threading;

internal static class LockSignal
{
    private static Semaphore _semaphore = new Semaphore(1, 1);

    public static void Wait()
    {
        _semaphore.WaitOne();
    }

    public static int Release()
    {
        return _semaphore.Release();
    }
}