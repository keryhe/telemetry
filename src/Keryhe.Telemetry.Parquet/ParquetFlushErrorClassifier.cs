using DuckDB.NET.Data;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// What a failed Parquet flush means (phase 2 decision 6). Disk full, permission denied, a read-only or missing root and
/// every other I/O error are operator-fixable, so they are transient: dropping on them would lose data a retry could save.
/// A DuckDB conversion or input error on a record is the data's fault and permanent, so <c>BatchBisector</c> isolates the
/// record. A <see cref="NotSupportedException"/> is permanent too. Out-of-memory and anything unrecognised stay transient.
/// </summary>
public sealed class ParquetFlushErrorClassifier : FlushErrorClassifierBase
{
    private static readonly string[] PermanentPrefixes =
        ["Conversion Error", "Invalid Input Error", "Out of Range Error", "Invalid Error", "Not implemented Error"];

    protected override FlushErrorKind? ClassifyOne(Exception exception) => exception switch
    {
        NotSupportedException => FlushErrorKind.Permanent,
        DuckDBException d => PermanentPrefixes.Any(p => d.Message.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? FlushErrorKind.Permanent
            : FlushErrorKind.Transient,
        IOException or UnauthorizedAccessException => FlushErrorKind.Transient,
        _ => null
    };
}
