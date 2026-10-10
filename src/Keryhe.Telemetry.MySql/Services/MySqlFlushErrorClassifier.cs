using Keryhe.Telemetry.Core.Data;
using MySqlConnector;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// Data too long (1406), incorrect or out-of-range values (1366, 1264, 1292), null (1048) and invalid JSON (3140) are
/// the data's fault; deadlocks (1205, 1213), lost connections (2002, 2006, 2013) and the rest are retried.
/// </summary>
public sealed class MySqlFlushErrorClassifier : FlushErrorClassifierBase
{
    private static readonly HashSet<int> PermanentCodes = [1406, 1366, 1264, 1292, 1048, 3140, 1265];

    protected override FlushErrorKind? ClassifyOne(Exception exception) =>
        exception is MySqlException my
            ? PermanentCodes.Contains((int)my.ErrorCode) ? FlushErrorKind.Permanent : FlushErrorKind.Transient
            : null;
}
