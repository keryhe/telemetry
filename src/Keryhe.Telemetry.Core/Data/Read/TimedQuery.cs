namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Runs a summary/count/series query under <see cref="QueryOptions.SummaryTimeoutSeconds"/> and
/// reports whether it timed out, instead of letting the exception propagate (decision 11: "exact,
/// bounded by a 5s statement timeout, falling back to a lower bound"; decision 31 reuses the same
/// timeout for metric series/exemplar queries). Nothing calls this yet — phases 2 and 3 wire it
/// into the summary endpoints.
///
/// Implemented with a linked <see cref="CancellationTokenSource"/> rather than by matching each
/// provider's own timeout exception type (Npgsql's <c>57014</c>, <c>SqlException.Number == -2</c>,
/// MySqlConnector's <c>CommandTimeoutExpired</c>, ClickHouse.Client's HTTP timeout, ...): the
/// provider set is closed but its exception shapes aren't worth coupling this reusable helper to,
/// and every ADO.NET provider already honors a linked token's cancellation by aborting the
/// in-flight command. The <paramref name="query"/> delegate also receives the timeout in seconds
/// so the caller can additionally pass it as a Dapper <c>CommandDefinition</c>'s
/// <c>commandTimeout</c>, belt-and-braces against a provider that ignores the cancellation token
/// but does honor its own command timeout.
/// </summary>
public static class TimedQuery
{
    public static async Task<(T? Result, bool TimedOut)> RunAsync<T>(
        Func<int, CancellationToken, Task<T>> query,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var result = await query(timeoutSeconds, cts.Token).ConfigureAwait(false);
            return (result, false);
        }
        // Only the internally-imposed timeout collapses to (default, TimedOut: true); a caller
        // cancellation (e.g. the HTTP request aborting) propagates as a normal OperationCanceledException.
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (default, true);
        }
        catch (TimeoutException)
        {
            return (default, true);
        }
    }
}
