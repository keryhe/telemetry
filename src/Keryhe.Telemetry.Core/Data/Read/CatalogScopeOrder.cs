namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// The one order of the metrics catalog's <c>groupBy=scope</c> rows, shared by every provider so it never depends on a
/// database's collation: scope name case-insensitively with an empty scope last, then metric name case-insensitively
/// (ordinal tie-breaks keep it total).
/// </summary>
public static class CatalogScopeOrder
{
    public static IEnumerable<T> Sort<T>(IEnumerable<T> rows, Func<T, string?> scope, Func<T, string> name) =>
        rows.OrderBy(r => string.IsNullOrEmpty(scope(r)) ? 1 : 0)
            .ThenBy(r => scope(r) ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => scope(r) ?? "", StringComparer.Ordinal)
            .ThenBy(r => name(r), StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => name(r), StringComparer.Ordinal);
}
