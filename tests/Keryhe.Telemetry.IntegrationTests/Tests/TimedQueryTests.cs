using System.Data.Common;
using Keryhe.Telemetry.Core.Data.Read;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Pure-logic unit tests for <see cref="TimedQuery"/> — no database dependency (list-pages-server-side plan, Phase 1).
///
/// A timeout is decided by the helper's own deadline, not by the exception's type (trace-summary-performance plan,
/// Phase 1): in the 3.0.1 stress ramp every relational driver ended an aborted command with its own exception
/// (Npgsql's <c>NpgsqlException</c> wrapping a <c>TimeoutException</c>, SqlClient's <c>SqlException</c>,
/// MySqlConnector's <c>MySqlException</c>), none of which the type-matching version caught, so each became a 500.
/// <see cref="TimedQueryProviderTestsBase"/> drives the real drivers.
/// </summary>
public class TimedQueryTests
{
    /// <summary>Shaped like Npgsql's: a driver exception wrapping the timeout, so it is neither of the types once matched.</summary>
    private sealed class FakeDriverException(string message, Exception inner) : DbException(message, inner);

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
        // The delegate's value is the driver's command timeout: the budget plus the grace, so the token fires first.
        var (result, timedOut) = await TimedQuery.RunAsync((seconds, _) => Task.FromResult(seconds), timeoutSeconds: 7);
        Assert.Equal(7 + TimedQuery.DriverTimeoutGraceSeconds, result);
        Assert.False(timedOut);
    }

    [Fact]
    public async Task DriverException_AfterTheDeadline_IsATimeout()
    {
        var (result, timedOut) = await TimedQuery.RunAsync<int>(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_200)); // ignores the token, as a driver mid-read does
            throw new FakeDriverException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"));
        }, timeoutSeconds: 1);

        Assert.True(timedOut);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task AnyExceptionType_AfterTheDeadline_IsATimeout()
    {
        // The rule is the deadline, not the type: an exception unrelated to timeouts that lands after it counts too
        // (it is still visible, on the query_timeouts counter's exception tag).
        var (_, timedOut) = await TimedQuery.RunAsync<int>(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_200));
            throw new InvalidOperationException("after the deadline");
        }, timeoutSeconds: 1);

        Assert.True(timedOut);
    }

    [Fact]
    public async Task ExceptionBeforeTheDeadline_Propagates()
    {
        // A fast, genuine error (bad SQL, a constraint) is not a timeout and must not be reported as "≥ N".
        await Assert.ThrowsAsync<FakeDriverException>(() => TimedQuery.RunAsync<int>(
            (_, _) => throw new FakeDriverException("syntax error", new InvalidOperationException()),
            timeoutSeconds: 5));
    }

    [Fact]
    public async Task CallerCancellation_Propagates_EvenAfterTheDeadline()
    {
        // The HTTP request aborting is not a timeout, whatever the query throws and however long it took.
        using var caller = new CancellationTokenSource();
        await Assert.ThrowsAsync<FakeDriverException>(async () => await TimedQuery.RunAsync<int>(async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1_200));
            caller.Cancel();
            throw new FakeDriverException("aborted", new TimeoutException());
        }, timeoutSeconds: 1, caller.Token));
    }

    [Fact]
    public async Task ZeroBudget_IsExpiredBeforeTheQueryStarts()
    {
        // Not a race with a timer: a query that checks its token first must see it cancelled, however fast it is.
        var (_, timedOut) = await TimedQuery.RunAsync<int>((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }, timeoutSeconds: 0);

        Assert.True(timedOut);
    }
}
