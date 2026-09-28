namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>Runs an <see cref="ILockObserver"/> on a fixed interval (1s by default) until stopped, keeping every sample. A failing sample is kept, with its error, and never stops the loop.</summary>
public sealed class LockSampler : IAsyncDisposable
{
    private readonly ILockObserver _observer;
    private readonly TimeSpan _interval;
    private readonly List<LockSample> _samples = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public LockSampler(ILockObserver observer, TimeSpan? interval = null)
    {
        _observer = observer;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public IReadOnlyList<LockSample> Samples
    {
        get { lock (_samples) return _samples.ToList(); }
    }

    public void Start() => _loop = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                var sample = await _observer.SampleAsync(_stop.Token);
                lock (_samples) _samples.Add(sample);
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    });

    public async Task<IReadOnlyList<LockSample>> StopAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
        return Samples;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }
}
