using Keryhe.Telemetry.Core.Data;
using Npgsql;

namespace Keryhe.Telemetry.PostgreSQL.Services;

/// <summary>
/// SQLSTATE classes 22 (data exception: a value too long, a bad cast) and 23 (integrity) are the data's fault and will
/// fail again; connection (08), serialization/deadlock (40), resources (53) and operator intervention (57) pass.
/// </summary>
public sealed class PostgreSqlFlushErrorClassifier : FlushErrorClassifierBase
{
    protected override FlushErrorKind? ClassifyOne(Exception exception)
    {
        if (exception is PostgresException pg)
            return pg.SqlState is { Length: >= 2 } state && (state.StartsWith("22") || state.StartsWith("23"))
                ? FlushErrorKind.Permanent
                : FlushErrorKind.Transient;
        return exception is NpgsqlException ? FlushErrorKind.Transient : null;
    }
}
