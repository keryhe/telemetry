namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// The six-group severity bucketing used by <see cref="LogReadRepositoryBase"/>'s summary query,
/// kept in one place so every provider produces identical numbers for the same data. Group boundaries: Trace &lt;=4, Debug 5-8, Info NULL-or-9-12, Warn
/// 13-16, Error 17-20, Fatal &gt;20 — must match exactly wherever this is used.
/// </summary>
public static class LogSeverityGroupSql
{
    /// <summary>
    /// Six <c>SUM(CASE WHEN ...)</c> columns aliased Trace/Debug/Info/Warn/Error/Fatal, for a
    /// <c>GROUP BY</c> query over <paramref name="severityColumn"/> (e.g. <c>lr.severity_number</c>).
    /// </summary>
    public static string SumCaseColumns(string severityColumn) => $"""
        SUM(CASE WHEN {severityColumn} <= 4 THEN 1 ELSE 0 END) AS Trace,
        SUM(CASE WHEN {severityColumn} BETWEEN 5 AND 8 THEN 1 ELSE 0 END) AS Debug,
        SUM(CASE WHEN {severityColumn} IS NULL OR {severityColumn} BETWEEN 9 AND 12 THEN 1 ELSE 0 END) AS Info,
        SUM(CASE WHEN {severityColumn} BETWEEN 13 AND 16 THEN 1 ELSE 0 END) AS Warn,
        SUM(CASE WHEN {severityColumn} BETWEEN 17 AND 20 THEN 1 ELSE 0 END) AS Error,
        SUM(CASE WHEN {severityColumn} > 20 THEN 1 ELSE 0 END) AS Fatal
        """;
}
