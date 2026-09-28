using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>What the page did while one tour step ran: its <c>/api</c> traffic, in-flight count, last activity time and console errors.</summary>
public sealed class StepCapture
{
    private long _lastActivity = Stopwatch.GetTimestamp();
    private int _inFlight;
    internal ConcurrentQueue<ApiRequestRecord> Requests { get; } = new();
    internal ConcurrentQueue<string> ConsoleErrors { get; } = new();
    internal ConcurrentQueue<Task> Pending { get; } = new();

    public int InFlight => Volatile.Read(ref _inFlight);
    public long LastActivity => Interlocked.Read(ref _lastActivity);
    public int Completed => Requests.Count;

    internal void Started() { Interlocked.Increment(ref _inFlight); Touch(); }
    internal void Ended() { Interlocked.Decrement(ref _inFlight); Touch(); }
    private void Touch() => Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
}

/// <summary>
/// Hooks one <see cref="IPage"/> and attributes each <c>/api</c> request to the step that was current when it
/// started (a request that outlives its step still lands in that step's capture). Also implements the plan's
/// page-ready definition: every readiness selector visible <b>and</b> the step's API requests settled.
/// </summary>
public sealed class PageWatcher
{
    /// <summary>No request in flight and none finished for this long counts as settled, so a chain of dependent calls is not mistaken for idle between links.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly ConcurrentDictionary<IRequest, StepCapture> _inFlight = new();

    public PageWatcher(IPage page)
    {
        Current = new StepCapture();
        page.Request += (_, r) =>
        {
            if (!ApiRequestNormalizer.IsApi(r.Url)) return;
            var capture = Current;
            _inFlight[r] = capture;
            capture.Started();
        };
        page.RequestFinished += (_, r) => Track(FinishedAsync(r));
        page.RequestFailed += (_, r) =>
        {
            if (!_inFlight.TryRemove(r, out var capture)) return;
            capture.Requests.Enqueue(new ApiRequestRecord(DateTimeOffset.UtcNow, r.Method, ApiRequestNormalizer.Template(r.Url), 0, 0, null, null, null, null, Failed: true));
            capture.Ended();
        };
        page.Console += (_, m) => { if (m.Type == "error") Current.ConsoleErrors.Enqueue(m.Text); };
        page.PageError += (_, e) => Current.ConsoleErrors.Enqueue("pageerror: " + e);
    }

    public StepCapture Current { get; private set; }

    /// <summary>Starts a fresh capture for the next step; requests already in flight stay attributed to the previous one.</summary>
    public StepCapture BeginStep() => Current = new StepCapture();

    private void Track(Task task) => Current.Pending.Enqueue(task);

    private async Task FinishedAsync(IRequest r)
    {
        if (!_inFlight.TryRemove(r, out var capture)) return;
        try
        {
            var template = ApiRequestNormalizer.Template(r.Url);
            var response = await r.ResponseAsync();
            var status = response?.Status ?? 0;
            var timing = r.Timing;
            var duration = timing.ResponseEnd > 0 ? timing.ResponseEnd : 0;

            long? bytes = null;
            try { bytes = (await r.SizesAsync()).ResponseBodySize; } catch (PlaywrightException) { }

            string? source = null; bool? lower = null, timedOut = null;
            if (response is not null && status == 200 && ApiRequestNormalizer.CarriesMarkers(template))
            {
                try { (source, lower, timedOut) = ApiRequestNormalizer.Markers(await response.TextAsync()); } catch (PlaywrightException) { }
            }
            capture.Requests.Enqueue(new ApiRequestRecord(DateTimeOffset.UtcNow, r.Method, template, status, duration, bytes, source, lower, timedOut, Failed: false));
        }
        catch (PlaywrightException)
        {
            capture.Requests.Enqueue(new ApiRequestRecord(DateTimeOffset.UtcNow, r.Method, ApiRequestNormalizer.Template(r.Url), 0, 0, null, null, null, null, Failed: true));
        }
        finally
        {
            capture.Ended();
        }
    }

    /// <summary>
    /// Waits for the step to be ready and returns milliseconds from <paramref name="startTimestamp"/> (a
    /// <see cref="Stopwatch.GetTimestamp"/> value) to the moment it became so, or null on timeout. The quiet
    /// period that proves the requests settled is not counted. A step that never issues an <c>/api</c> request
    /// is not ready when <paramref name="requireApi"/> is set, since every page under test loads data.
    /// </summary>
    public async Task<double?> WaitReadyAsync(IPage page, StepCapture capture, IReadOnlyList<string> ready, long startTimestamp, TimeSpan timeout, bool requireApi = true)
    {
        var deadline = startTimestamp + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        long Remaining() => Math.Max(1, (long)Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline).TotalMilliseconds);
        try
        {
            await Task.WhenAll(ready.Select(sel => page.Locator(sel).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = Remaining() })));
        }
        catch (TimeoutException)
        {
            return null;
        }
        var visibleAt = Stopwatch.GetTimestamp();

        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var settled = capture.InFlight == 0 && Stopwatch.GetElapsedTime(capture.LastActivity, now) >= Quiet && (!requireApi || capture.Completed > 0);
            if (settled) break;
            if (now >= deadline) return null;
            await Task.Delay(50);
        }
        await Task.WhenAll(capture.Pending);
        var readyAt = Math.Max(visibleAt, capture.LastActivity);
        return Stopwatch.GetElapsedTime(startTimestamp, readyAt).TotalMilliseconds;
    }
}
