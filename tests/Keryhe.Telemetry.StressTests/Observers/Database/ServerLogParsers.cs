using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

/// <summary>Pulls deadlock and lock-wait detail out of a database container's log text. Pure, so it is tested without a database.</summary>
public static partial class ServerLogParsers
{
    private const int MaxBlocks = 50;

    [GeneratedRegex(@"^\s")]
    private static partial Regex Continuation();

    [GeneratedRegex(@"\b(DETAIL|HINT|CONTEXT|STATEMENT|QUERY):")]
    private static partial Regex PostgresFollowOn();

    /// <summary>
    /// Each <c>deadlock detected</c> report from a Postgres log: the ERROR line plus the DETAIL/HINT/CONTEXT/STATEMENT
    /// lines (and their tab-indented continuations) that follow it.
    /// </summary>
    public static IReadOnlyList<string> PostgresDeadlocks(string log)
    {
        var lines = log.Split('\n');
        var blocks = new List<string>();
        for (var i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
        {
            if (!lines[i].Contains("ERROR:") || !lines[i].Contains("deadlock detected")) continue;
            var block = new List<string> { lines[i].TrimEnd('\r') };
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j].TrimEnd('\r');
                if (Continuation().IsMatch(line) || PostgresFollowOn().IsMatch(line)) block.Add(line);
                else break;
            }
            blocks.Add(string.Join('\n', block));
        }
        return blocks;
    }

    /// <summary>
    /// Lines <c>log_lock_waits</c> writes when a session waited past <c>deadlock_timeout</c> (<c>still waiting for</c>) and
    /// when it finally got the lock (<c>acquired ... after</c>).
    /// </summary>
    public static IReadOnlyList<string> PostgresLockWaits(string log) =>
        log.Split('\n')
            .Where(l => l.Contains("still waiting for") || (l.Contains("acquired") && l.Contains("lock", StringComparison.OrdinalIgnoreCase) && l.Contains(" after ")))
            .Select(l => l.TrimEnd('\r')).Take(MaxBlocks * 4).ToList();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z \d+ \[")]
    private static partial Regex MySqlEntry();

    /// <summary>
    /// Each InnoDB deadlock dump (from <c>innodb_print_all_deadlocks</c>). The error log writes it as bare lines with
    /// no timestamp prefix, starting at <c>TRANSACTION &lt;id&gt;, ACTIVE ...</c> and running to the next timestamped
    /// entry, so a dump is that run of un-prefixed lines. Needs the log read without Docker's own timestamps.
    /// </summary>
    public static IReadOnlyList<string> MySqlDeadlocks(string log)
    {
        var lines = log.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var blocks = new List<string>();
        for (var i = 0; i < lines.Length && blocks.Count < MaxBlocks; i++)
        {
            if (!lines[i].StartsWith("TRANSACTION ", StringComparison.Ordinal)) continue;
            var block = new List<string>();
            for (; i < lines.Length && !MySqlEntry().IsMatch(lines[i]); i++)
                block.Add(lines[i]);
            blocks.Add(string.Join('\n', block).TrimEnd());
        }
        return blocks;
    }
}
