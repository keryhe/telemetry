using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.StressTests.Orchestration;

public sealed record FlushRetryEvent(DateTimeOffset At, string Signal, int Count, int Attempt);
public sealed record BatchDroppedEvent(DateTimeOffset At, string Signal, int Count);
public sealed record RetentionSweepEvent(DateTimeOffset At, long SpanRows, long DataPointRows, long LogRows, long ElapsedMs);
public sealed record ProviderErrorEvent(DateTimeOffset At, string Kind, string Line);
public sealed record ShutdownDeadlineEvent(DateTimeOffset At, string Signal, long Unpersisted);

public sealed record HostLogSummary(
    int Warnings, int Errors,
    IReadOnlyList<FlushRetryEvent> FlushRetries,
    IReadOnlyList<BatchDroppedEvent> BatchesDropped,
    IReadOnlyList<RetentionSweepEvent> RetentionSweeps,
    IReadOnlyList<ProviderErrorEvent> ProviderErrors,
    IReadOnlyList<ShutdownDeadlineEvent> ShutdownDeadlines);

/// <summary>
/// Scans a host's console output line by line (stress-test plan, Phase 3) and keeps the events the
/// report needs, each stamped with when it was seen: flush retries and dropped batches from the
/// ingestion worker, retention sweeps with rows removed and elapsed ms, provider deadlock/lock-wait
/// errors, and the shutdown-deadline line that says the drain did not finish.
///
/// Matches the wording of the hosts' own log messages, so a change to one of them (for example
/// <c>RetentionWorker</c>'s "Retention sweep complete" line) has to be mirrored here; the unit tests
/// pin each pattern to a sample line.
/// </summary>
public sealed partial class HostLogScanner
{
    // Console lines the harness asks for (see HostLauncher): "<UTC timestamp> <level>: <category>[id] <message>".
    [GeneratedRegex(@"^(?<ts>\d{4}-\d\d-\d\dT[\d:.]+Z) (?<lvl>trce|dbug|info|warn|fail|crit): ")]
    private static partial Regex Header();

    [GeneratedRegex(@"Error flushing (?<sig>\w+) batch of (?<n>\d+) \(attempt (?<a>\d+)/")]
    private static partial Regex FlushRetry();

    [GeneratedRegex(@"Error flushing (?<sig>\w+) batch of (?<n>\d+) after \d+ attempts .* batch dropped")]
    private static partial Regex BatchDropped();

    [GeneratedRegex(@"Retention sweep complete: (?<t>\d+) span rows, (?<m>\d+) data-point rows, (?<l>\d+) log rows removed in (?<ms>\d+) ms")]
    private static partial Regex RetentionSweep();

    [GeneratedRegex(@"Shutdown deadline reached before the (?<sig>\w+) queue drained -- (?<n>\d+) records were not persisted")]
    private static partial Regex ShutdownDeadline();

    // Deadlock and lock-wait signatures per provider.
    private static readonly (string Kind, Regex Pattern)[] ProviderPatterns =
    [
        ("SqlServer 1205 (deadlock victim)", new Regex(@"Number=1205|Error 1205|was deadlocked on|deadlock victim", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("Postgres 40P01 (deadlock detected)", new Regex(@"40P01|deadlock detected", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("MySql 1213 (deadlock)", new Regex(@"Error Code: 1213|Deadlock found when trying to get lock|MySqlErrorCode\.Deadlock", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("MySql 1205 (lock wait timeout)", new Regex(@"Lock wait timeout exceeded|MySqlErrorCode\.LockWaitTimeout|Error Code: 1205", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    private readonly object _gate = new();
    private readonly List<FlushRetryEvent> _retries = [];
    private readonly List<BatchDroppedEvent> _dropped = [];
    private readonly List<RetentionSweepEvent> _sweeps = [];
    private readonly List<ProviderErrorEvent> _providerErrors = [];
    private readonly List<ShutdownDeadlineEvent> _deadlines = [];
    private int _warnings, _errors;

    /// <summary>Feeds one console line. <paramref name="receivedAt"/> is used when the line carries no timestamp of its own.</summary>
    public void OnLine(string line, DateTimeOffset receivedAt)
    {
        var at = receivedAt;
        var header = Header().Match(line);
        if (header.Success)
        {
            if (DateTimeOffset.TryParse(header.Groups["ts"].Value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                at = parsed;
        }

        lock (_gate)
        {
            if (header.Success)
            {
                switch (header.Groups["lvl"].Value)
                {
                    case "warn": _warnings++; break;
                    case "fail" or "crit": _errors++; break;
                }
            }

            var dropped = BatchDropped().Match(line);
            if (dropped.Success)
                _dropped.Add(new BatchDroppedEvent(at, dropped.Groups["sig"].Value, int.Parse(dropped.Groups["n"].Value)));
            else if (FlushRetry().Match(line) is { Success: true } retry)
                _retries.Add(new FlushRetryEvent(at, retry.Groups["sig"].Value, int.Parse(retry.Groups["n"].Value), int.Parse(retry.Groups["a"].Value)));

            if (RetentionSweep().Match(line) is { Success: true } sweep)
                _sweeps.Add(new RetentionSweepEvent(at, long.Parse(sweep.Groups["t"].Value), long.Parse(sweep.Groups["m"].Value),
                    long.Parse(sweep.Groups["l"].Value), long.Parse(sweep.Groups["ms"].Value)));

            if (ShutdownDeadline().Match(line) is { Success: true } deadline)
                _deadlines.Add(new ShutdownDeadlineEvent(at, deadline.Groups["sig"].Value, long.Parse(deadline.Groups["n"].Value)));

            foreach (var (kind, pattern) in ProviderPatterns)
                if (pattern.IsMatch(line))
                {
                    _providerErrors.Add(new ProviderErrorEvent(at, kind, line.Length > 300 ? line[..300] : line));
                    break;
                }
        }
    }

    public HostLogSummary Summarize()
    {
        lock (_gate)
            return new HostLogSummary(_warnings, _errors, _retries.ToList(), _dropped.ToList(), _sweeps.ToList(),
                _providerErrors.ToList(), _deadlines.ToList());
    }
}
