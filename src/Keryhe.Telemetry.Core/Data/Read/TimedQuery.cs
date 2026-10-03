using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Runs a summary/count/series query under <see cref="QueryOptions.SummaryTimeoutSeconds"/> and
/// reports whether it timed out, instead of letting the exception propagate (decision 11: "exact,
/// bounded by a 5s statement timeout, falling back to a lower bound"; decision 31 reuses the same
/// timeout for metric series/exemplar queries).
///
/// <para><b>A timeout is decided by this helper's own deadline, not by the exception's type.</b> Any exception
/// raised once the budget has elapsed (or once its own token has fired) is a timeout, unless the caller's token was
/// cancelled, in which case it propagates as the request abort it is. Matching exception types does not work: under
/// load every provider surfaces an aborted command differently. The 3.0.1 stress ramp recorded Npgsql's
/// <c>NpgsqlException</c> wrapping a <c>TimeoutException</c> ("Exception while reading from stream", raised when the
/// cancel request itself goes unanswered, after which the connector is broken), SqlClient's <c>SqlException</c>
/// "Execution Timeout Expired" (-2) and MySqlConnector's <c>MySqlException</c> "The Command Timeout expired". None
/// of them is an <see cref="OperationCanceledException"/> or a bare <see cref="TimeoutException"/>, so each one
/// escaped as a 500 and the "≥ N" fallback never ran.</para>
///
/// <para>The <paramref name="query"/> delegate receives a command timeout for the driver that is the budget plus
/// <see cref="DriverTimeoutGraceSeconds"/>, so this helper's token always fires first and the driver's own command
/// timeout is only a backstop for a provider that ignores cancellation. Callers pass it to Dapper's
/// <c>CommandDefinition</c> as <c>commandTimeout</c>.</para>
///
/// <para><b>After a timeout, do not reuse the connection the query ran on.</b> The command was aborted mid-statement
/// and the connection is in a state the driver owns (Npgsql's is broken outright when the cancel goes unanswered).
/// Any follow-up query — a capped count, the page after a "last" count — opens a fresh connection.</para>
///
/// <para>Every timeout is counted on the <c>Keryhe.Telemetry.Query</c> meter's <c>query_timeouts</c> counter,
/// tagged with the exception type that ended the query (readable with <c>dotnet-counters</c>, no exporter
/// needed). The tag is how a genuine error that happens to land after the deadline stays visible instead of
/// silently becoming "≥ N".</para>
/// </summary>
public static class TimedQuery
{
    /// <summary>Added to the budget for the driver's own command timeout, so the linked token always wins the race.</summary>
    public const int DriverTimeoutGraceSeconds = 2;

    private static readonly Meter Meter = new("Keryhe.Telemetry.Query", "1.0.0");
    private static readonly Counter<long> Timeouts = Meter.CreateCounter<long>(
        "query_timeouts", description: "Summary-class queries that ran out of their time budget, by the exception type that ended them.");

    public static async Task<(T? Result, bool TimedOut)> RunAsync<T>(
        Func<int, CancellationToken, Task<T>> query,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // CancelAfter(0) only schedules a timer, so a fast query could still finish first; a zero budget means expired
        // before the query starts (the repositories document 0 as "expire now").
        if (budget <= TimeSpan.Zero) cts.Cancel();
        else cts.CancelAfter(budget);
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await query(Math.Max(0, timeoutSeconds) + DriverTimeoutGraceSeconds, cts.Token).ConfigureAwait(false);
            return (result, false);
        }
        // A caller cancellation (e.g. the HTTP request aborting) is not a timeout and propagates unchanged.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && (cts.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= budget))
        {
            Timeouts.Add(1, new KeyValuePair<string, object?>("exception", ex.GetType().Name));
            return (default, true);
        }
    }
}
