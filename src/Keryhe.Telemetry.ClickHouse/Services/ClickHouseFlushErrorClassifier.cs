using ClickHouse.Client;
using Keryhe.Telemetry.Core.Data;

namespace Keryhe.Telemetry.ClickHouse.Services;

/// <summary>
/// Parse and type errors from the server are the data's fault. Memory limits (241), too many parts (252), timeouts and
/// network errors pass. ClickHouse columns are unbounded strings, so client data rarely produces a permanent error; this
/// mostly keeps a writer bug from costing a whole day buffer.
/// </summary>
public sealed class ClickHouseFlushErrorClassifier : FlushErrorClassifierBase
{
    // CANNOT_PARSE_TEXT 6, CANNOT_PARSE_NUMBER 27, CANNOT_PARSE_DATE 38, CANNOT_PARSE_DATETIME 41, ILLEGAL_TYPE_OF_ARGUMENT 43,
    // TYPE_MISMATCH 53, CANNOT_CONVERT_TYPE 70, CANNOT_PARSE_UUID 376.
    private static readonly HashSet<int> PermanentCodes = [6, 27, 38, 41, 43, 53, 70, 376];

    protected override FlushErrorKind? ClassifyOne(Exception exception) =>
        exception is ClickHouseServerException ch
            ? PermanentCodes.Contains(ch.ErrorCode) ? FlushErrorKind.Permanent : FlushErrorKind.Transient
            : null;
}
