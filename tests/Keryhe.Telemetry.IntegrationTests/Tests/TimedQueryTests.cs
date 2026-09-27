using Keryhe.Telemetry.Core.Data.Read;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Pure-logic unit tests for <see cref="TimedQuery"/> — no database dependency (list-pages-server-side plan, Phase 1).</summary>
public class TimedQueryTests
{
    [Fact]
    public async Task Fast_Query_Returns_Result_Not_TimedOut()
    {
        var (result, timedOut) = await TimedQuery.RunAsync((_, _) => Task.FromResult(42), timeoutSeconds: 5);
        Assert.Equal(42, result);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task Slow_Query_Reports_TimedOut()
    {
        var (result, timedOut) = await TimedQuery.RunAsync(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return 42;
        }, timeoutSeconds: 1);

        Assert.Equal(0, result);
        Assert.True(timedOut);
    }

    [Fact]
    public async Task Caller_Cancellation_Propagates_Instead_Of_Reporting_TimedOut()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TimedQuery.RunAsync(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 42;
            }, timeoutSeconds: 30, cts.Token));
    }

    [Fact]
    public async Task Passes_Timeout_Seconds_Through_To_Delegate()
    {
        var (result, timedOut) = await TimedQuery.RunAsync((seconds, _) => Task.FromResult(seconds), timeoutSeconds: 7);
        Assert.Equal(7, result);
        Assert.False(timedOut);
    }
}
