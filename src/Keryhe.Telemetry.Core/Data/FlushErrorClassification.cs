namespace Keryhe.Telemetry.Core.Data;

/// <summary>Whether retrying a failed flush can help.</summary>
public enum FlushErrorKind
{
    /// <summary>A connection, lock, timeout or capacity problem: the same batch may succeed later.</summary>
    Transient,
    /// <summary>The database refuses this data (a value too long, a bad type): retrying the same batch cannot help.</summary>
    Permanent
}

/// <summary>
/// Classifies an exception thrown by a bulk writer's flush. Each provider registers its own from
/// <c>Add&lt;Provider&gt;CollectorServices</c>; <see cref="DefaultFlushErrorClassifier"/> (everything transient, which is
/// the behaviour from before batches were split) is used when none is.
/// </summary>
public interface IFlushErrorClassifier
{
    FlushErrorKind Classify(Exception exception);
}

/// <summary>Treats every error as transient.</summary>
public sealed class DefaultFlushErrorClassifier : IFlushErrorClassifier
{
    public static readonly DefaultFlushErrorClassifier Instance = new();
    public FlushErrorKind Classify(Exception exception) => FlushErrorKind.Transient;
}

/// <summary>How one flush (with its transient-error retries) ended.</summary>
public enum FlushOutcome
{
    Ok,
    /// <summary>Transient errors outlasted the retries.</summary>
    Exhausted,
    /// <summary>A permanent error: split the batch to isolate the bad records.</summary>
    Permanent
}

/// <summary>What <see cref="BatchBisector.RunAsync{T}"/> did with a batch that failed permanently.</summary>
/// <param name="Flushed">The pieces that were stored, each a sub-list of the batch.</param>
/// <param name="Permanent">Single records the database refuses; dropped.</param>
/// <param name="Exhausted">Records in pieces whose transient retries ran out; dropped.</param>
/// <param name="CapDropped">Records left when the split budget ran out; dropped.</param>
/// <param name="Flushes">Extra flush attempts spent.</param>
public sealed record BisectResult<T>(List<List<T>> Flushed, List<T> Permanent, int Exhausted, int CapDropped, int Flushes);

/// <summary>
/// Isolates the records a database permanently refuses. A batch that failed with a permanent error is cut in half and
/// each half flushed (with the caller's transient retry policy); a half that fails permanently is cut again, down to a
/// single record, which is reported and dropped. A merged batch can hold many tenants' exports, so one bad value must
/// not cost the rest. Work is bounded by <c>maxFlushes</c>; what is left when it runs out is counted, not retried.
/// </summary>
public static class BatchBisector
{
    /// <param name="batch">A batch that has just failed with <see cref="FlushOutcome.Permanent"/>.</param>
    /// <param name="flush">Flushes a piece (retrying transient errors itself) and reports how it ended. Called once per piece, so a
    /// provider whose retries must not repeat a block (ClickHouse's deduplication token) mints a new token inside it.</param>
    public static async Task<BisectResult<T>> RunAsync<T>(
        List<T> batch, Func<List<T>, CancellationToken, Task<FlushOutcome>> flush, int maxFlushes, CancellationToken ct)
    {
        if (batch.Count <= 1) return new BisectResult<T>([], [.. batch], 0, 0, 0);

        var flushed = new List<List<T>>();
        var permanent = new List<T>();
        var exhausted = 0;
        var capDropped = 0;
        var flushes = 0;

        // Depth-first, first half first, so the work done is bounded and the order of the batch is kept.
        var pending = new Stack<List<T>>();
        PushHalves(batch, pending);

        while (pending.Count > 0)
        {
            var piece = pending.Pop();
            if (flushes >= maxFlushes) { capDropped += piece.Count; continue; }

            flushes++;
            switch (await flush(piece, ct))
            {
                case FlushOutcome.Ok: flushed.Add(piece); break;
                case FlushOutcome.Exhausted: exhausted += piece.Count; break;
                default:
                    if (piece.Count == 1) permanent.Add(piece[0]);
                    else PushHalves(piece, pending);
                    break;
            }
        }

        return new BisectResult<T>(flushed, permanent, exhausted, capDropped, flushes);
    }

    private static void PushHalves<T>(List<T> piece, Stack<List<T>> pending)
    {
        if (piece.Count <= 1) { pending.Push(piece); return; }
        var mid = piece.Count / 2;
        pending.Push(piece.GetRange(mid, piece.Count - mid)); // popped second
        pending.Push(piece.GetRange(0, mid));                 // popped first
    }
}

/// <summary>Base for provider classifiers: looks through wrapper exceptions for the driver's own exception.</summary>
public abstract class FlushErrorClassifierBase : IFlushErrorClassifier
{
    /// <summary>The kind for one exception in the chain, or null when it is not one this classifier recognises.</summary>
    protected abstract FlushErrorKind? ClassifyOne(Exception exception);

    public FlushErrorKind Classify(Exception exception)
    {
        FlushErrorKind? found = null;
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (Classify(inner) == FlushErrorKind.Permanent) return FlushErrorKind.Permanent;
            // A transient driver exception anywhere in the chain wins over a permanent-looking wrapper; the first recognised one decides.
            found ??= ClassifyOne(e);
        }
        return found ?? FlushErrorKind.Transient;
    }
}
