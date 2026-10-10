using Keryhe.Telemetry.Core.Data;
using Microsoft.Data.SqlClient;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// Truncation (2628, 8152, 4815), null, constraint and conversion errors are the data's fault; deadlocks (1205), timeouts
/// (-2), failover and throttling errors are not. <c>SqlBulkCopy</c> reports some data errors as an
/// <see cref="InvalidOperationException"/> ("invalid column length", "cannot be converted").
/// </summary>
public sealed class SqlServerFlushErrorClassifier : FlushErrorClassifierBase
{
    private static readonly HashSet<int> PermanentNumbers = [2628, 8152, 4815, 515, 547, 245, 8114, 8115, 2627, 2601];

    protected override FlushErrorKind? ClassifyOne(Exception exception)
    {
        if (exception is SqlException sql)
            return sql.Errors.Cast<SqlError>().Any(e => PermanentNumbers.Contains(e.Number))
                ? FlushErrorKind.Permanent
                : FlushErrorKind.Transient;
        if (exception is InvalidOperationException ioe)
        {
            var m = ioe.Message;
            if (m.Contains("invalid column length", StringComparison.OrdinalIgnoreCase)
                || m.Contains("would be truncated", StringComparison.OrdinalIgnoreCase)
                || m.Contains("cannot be converted to type", StringComparison.OrdinalIgnoreCase)
                || m.Contains("does not allow nulls", StringComparison.OrdinalIgnoreCase))
                return FlushErrorKind.Permanent;
        }
        return null;
    }
}
