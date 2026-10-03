using System.Collections.Concurrent;
using System.Diagnostics;
using Keryhe.Telemetry.TestInfrastructure.Seeding;
using Microsoft.Playwright;

namespace Keryhe.Telemetry.StressTests.Browser;

/// <summary>
/// The headless-browser page tour (stress-test plan, Phase 5): N users, each in its own Chromium context pinned
/// to one tenant, loop the tour until stopped, recording for every step the time to ready and every <c>/api</c>
/// call it made. Start it once the data exists (after warm-up) and stop it when the measured window ends.
/// </summary>
public sealed class PlaywrightTour : IAsyncDisposable
{
    private readonly Uri _ui;
    private readonly TourOptions _options;
    private readonly string _screenshotDir;
    private readonly List<UserSession> _users;
    private readonly ConcurrentQueue<PageResult> _results = new();
    private readonly CancellationTokenSource _stop = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _iterations;
    private Task[] _loops = [];

    private sealed record UserSession(int Index, SeededTenant Tenant, TourData Data);

    private PlaywrightTour(Uri ui, TourOptions options, string outDir, List<UserSession> users)
    {
        _ui = new Uri(ui.AbsoluteUri.TrimEnd('/') + "/");
        _options = options;
        _screenshotDir = Path.Combine(outDir, "screenshots");
        _users = users;
    }

    /// <summary>Launches Chromium and discovers each user's tenant data through the API. Throws a clear message when the browser has not been installed.</summary>
    public static async Task<PlaywrightTour> StartAsync(Uri ui, Uri api, IReadOnlyList<SeededTenant> tenants, TourOptions options, string outDir, CancellationToken cancellationToken = default)
    {
        if (tenants.Count == 0) throw new ArgumentException("At least one tenant is needed.", nameof(tenants));
        using var http = new HttpClient { BaseAddress = new Uri(api.AbsoluteUri.TrimEnd('/') + "/") };
        var users = new List<UserSession>();
        for (var i = 0; i < Math.Clamp(options.Users, 1, 5); i++)
        {
            var tenant = tenants[i % tenants.Count];
            users.Add(new UserSession(i, tenant, await TourDiscovery.DiscoverWithRetryAsync(http, tenant.Id, options.DiscoveryWait, cancellationToken)));
        }

        var tour = new PlaywrightTour(ui, options, outDir, users);
        try
        {
            tour._playwright = await Playwright.CreateAsync();
            tour._browser = await tour._playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = !options.Headed });
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
        {
            await tour.DisposeAsync();
            throw new InvalidOperationException("Chromium is not installed for Playwright. Run: dotnet run --project tests/Keryhe.Telemetry.StressTests -- playwright-install", ex);
        }
        return tour;
    }

    public IReadOnlyList<PageResult> Results => _results.ToArray();

    public void Start() => _loops = _users.Select(u => Task.Run(() => RunUserAsync(u, _stop.Token))).ToArray();

    /// <summary>Stops the users after their current step and returns everything recorded.</summary>
    public async Task<TourResults> StopAsync()
    {
        _stop.Cancel();
        await WaitForLoopsAsync();
        return new TourResults(_results.ToArray(), _iterations, _users.Select(u => u.Data).ToList());
    }

    /// <summary>Waits for the user loops to finish their current step, without letting a faulted or stuck loop fail or stall the scenario.</summary>
    private async Task WaitForLoopsAsync()
    {
        try { await Task.WhenAll(_loops).WaitAsync(_options.ReadyTimeout + TimeSpan.FromSeconds(30)); }
        catch (Exception) { }
    }

    private async Task RunUserAsync(UserSession user, CancellationToken ct)
    {
        await using var context = await _browser!.NewContextAsync();
        // Same keys TenantService reads, so no UI click is needed to pick the tenant.
        await context.AddInitScriptAsync(
            $"localStorage.setItem('selectedTenantId', '{user.Tenant.Id}'); localStorage.setItem('selectedTenantName', {System.Text.Json.JsonSerializer.Serialize(user.Tenant.Name)});");
        var page = await context.NewPageAsync();
        // Actions (clicks) wait for actionability up to the same limit as readiness, not Playwright's 30s default.
        page.SetDefaultTimeout((float)_options.ReadyTimeout.TotalMilliseconds);
        var watcher = new PageWatcher(page);

        for (var loop = 0; !ct.IsCancellationRequested; loop++)
        {
            var window = _options.Windows[loop % _options.Windows.Count];
            foreach (var step in TourPlan.Build(window, _options, user.Data, user.Tenant.Id))
            {
                if (ct.IsCancellationRequested) break;
                try { _results.Enqueue(await RunStepAsync(user, page, watcher, step, window)); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // RunStepAsync handles Playwright errors itself; anything else is recorded so the loop (and the run) carries on.
                    _results.Enqueue(new PageResult(user.Index, user.Tenant.Id, step.Name, step.Page, window, DateTimeOffset.UtcNow, null,
                        TimedOut: false, ex.Message, null, [], []));
                }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                try { await Task.Delay(_options.ThinkTime, ct); } catch (OperationCanceledException) { }
            }
            Interlocked.Increment(ref _iterations);
        }
    }

    private async Task<PageResult> RunStepAsync(UserSession user, IPage page, PageWatcher watcher, TourStep step, string window)
    {
        var capture = watcher.BeginStep();
        var startedAt = DateTimeOffset.UtcNow;
        var start = Stopwatch.GetTimestamp();
        double? readyMs = null;
        string? error = null, screenshot = null;
        try
        {
            if (step.RelativeUrl is { } url)
                await page.GotoAsync(new Uri(_ui, url).ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = (float)_options.ReadyTimeout.TotalMilliseconds });
            else
                await step.Act!(page);

            readyMs = step.Ready.Count == 0
                ? Stopwatch.GetElapsedTime(start).TotalMilliseconds
                : await watcher.WaitReadyAsync(page, capture, step.Ready, start, _options.ReadyTimeout, step.RequiresApi);
            if (readyMs is null) screenshot = await ScreenshotAsync(page, user, step);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            error = string.Join(" | ", ex.Message.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6));
            screenshot = await ScreenshotAsync(page, user, step);
        }

        try { await Task.WhenAll(capture.Pending); } catch (Exception) { }
        return new PageResult(user.Index, user.Tenant.Id, step.Name, step.Page, window, startedAt, readyMs,
            TimedOut: readyMs is null && error is null, error, screenshot,
            capture.Requests.OrderBy(r => r.At).ToList(), capture.ConsoleErrors.ToList());
    }

    private async Task<string?> ScreenshotAsync(IPage page, UserSession user, TourStep step)
    {
        try
        {
            Directory.CreateDirectory(_screenshotDir);
            var path = Path.Combine(_screenshotDir, $"u{user.Index}-{step.Name.Replace(':', '_')}-{DateTime.UtcNow:HHmmss}.png");
            // A page that is already too wedged to become ready can also wedge the screenshot; a diagnostic must never fail the run.
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Timeout = 15_000 });
            return path;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await WaitForLoopsAsync();
        try { if (_browser is not null) await _browser.CloseAsync(); } catch (Exception) { }
        _playwright?.Dispose();
        _stop.Dispose();
    }
}
