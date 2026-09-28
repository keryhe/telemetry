using System.Data.Common;
using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;

namespace Keryhe.Telemetry.PostgreSQL.Services;

// =============================================================================
// PostgreSQL (plain) read repositories — the reference Dapper implementations.
// Connection comes from the host-configured NpgsqlDataSource (read connection string).
// =============================================================================

/// <summary>
/// Shared body for the <c>AttributePredicate</c> dialect hook on the analytics tier (PostgreSQL,
/// and Timescale via its unchanged subclass — list-pages-server-side plan, Phase 7, decision 7).
/// Duplicated as an override on every PostgreSQL read repository class below (there is no mixin,
/// matching this file's existing convention) but sharing one implementation, same pattern as
/// <c>SqlServerJsonAttributeHooks</c>/<c>ClickHouseJsonAttributeHooks</c>.
///
/// Positive (non-negated) matches use typed <c>@&gt;</c> containment against the GIN
/// <c>jsonb_path_ops</c> indexes added in schema 2.13.3, instead of the phase 1
/// <c>LOWER(col -&gt;&gt; key) = LOWER(value)</c> text comparison. <c>@&gt;</c> only matches the
/// JSON type given, so a value that parses as a JSON number or boolean is ALSO matched in that
/// native form -- <c>key:500</c> must still match both the stored string <c>"500"</c> and the
/// stored number <c>500</c>, exactly like phase 1's text-comparison semantics. Value comparison
/// stays case-sensitive under containment (phase 1 lowercased both sides in SQL because the text
/// form has no other way to be case-insensitive server-side); values entering this hook already
/// come from user search text via <c>CompileSearch</c>/label filters, which do not themselves
/// case-fold, so this is a narrow behavior change only for a value whose case doesn't already
/// match storage -- accepted because decision 7's own examples (numeric/boolean attributes) are
/// case-invariant by construction, and free-text values go through <c>FreeTextPredicate</c>, not
/// this hook.
///
/// Negation stays on the phase 1 unindexed text-comparison form unconditionally: containment
/// cannot efficiently express "key absent or holds a different value" (decision 7).
///
/// DEVIATION (documented in plans/list-pages-server-side.md's Phase 7 "Implementation notes"):
/// a positive string-valued match (the first containment branch) is exact-case, where phase 1
/// was case-insensitive (`LOWER(col ->> key) = LOWER(value)`) on every provider. There is no way
/// to keep case-insensitive containment AND have Postgres actually use the GIN index for it: an
/// OR between an indexable `@>` clause and a non-indexable `LOWER(...) = LOWER(...)` fallback
/// forces a full sequential scan regardless (Postgres cannot partially trust an index scan when
/// any OR-branch it can't index might match additional rows), which would defeat the entire
/// point of this phase. Numeric and boolean matches are unaffected (numbers have no case; the
/// boolean branch still lowers both sides before casting). This narrows only a same-key
/// string-valued positive search whose case doesn't already match storage, on the analytics
/// tier; the standard tier (SqlServer/MySql) and every negated search are unchanged.
/// </summary>
internal static class PostgreSqlJsonAttributeHooks
{
    public static string Predicate(string column, string keyParam, string valueParam, bool negated)
    {
        if (negated)
        {
            var expr = $"LOWER({column} ->> {keyParam})";
            var valueExpr = $"LOWER({valueParam})";
            return $"{expr} IS DISTINCT FROM {valueExpr}";
        }

        return $"""
            (
                {column} @> jsonb_build_object({keyParam}, {valueParam})
                OR (
                    {valueParam} ~ '^-?[0-9]+(\.[0-9]+)?([eE][-+]?[0-9]+)?$'
                    AND {column} @> jsonb_build_object({keyParam}, ({valueParam})::numeric)
                )
                OR (
                    LOWER({valueParam}) IN ('true', 'false')
                    AND {column} @> jsonb_build_object({keyParam}, (LOWER({valueParam}))::boolean)
                )
            )
            """;
    }
}

public class PostgreSqlTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : TraceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    // Npgsql binds a list parameter as a native array, so use = ANY(@ids) rather than
    // the base's IN @ids (which Dapper only expands for non-array providers like SqlServer).
    protected override string TraceIdInPredicate(string alias) => $"{alias}.trace_id = ANY(@traceIds)";
    protected override string ResourceIdInPredicate => "id = ANY(@resourceIds)";

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => PostgreSqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class PostgreSqlMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : MetricReadRepositoryBase(tenantContext, configuration)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => PostgreSqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class PostgreSqlLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : LogReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string AttributePredicate(string column, string keyParam, string valueParam, bool negated)
        => PostgreSqlJsonAttributeHooks.Predicate(column, keyParam, valueParam, negated);
}

public class PostgreSqlResourceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : ResourceReadRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlTenantCatalogRepository(NpgsqlDataSource dataSource)
    : TenantCatalogRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

/// <summary>PostgreSQL (plain) implementation of <see cref="IRollupRepository"/> — plain read-committed connections suit Postgres's MVCC (readers never block writers).</summary>
public class PostgreSqlLogRollupRepository(NpgsqlDataSource dataSource) : LogRollupRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}

public class PostgreSqlAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : AlertRuleRepositoryBase(tenantContext)
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    protected override string JsonParam(string parameterName) => $"CAST(@{parameterName} AS jsonb)";

    protected override string ReturningIdentity => "RETURNING id";

    protected override string ClaimFireSql => """
        UPDATE alert_rules
        SET last_fired_at = NOW()
        WHERE id = @ruleId
          AND tenant_id = @tenantId
          AND enabled = TRUE
          AND (last_fired_at IS NULL
               OR last_fired_at < NOW() - (@cooldownMinutes * INTERVAL '1 minute'))
        """;
}

/// <summary>
/// PostgreSQL (plain) implementation of the <see cref="IRetentionSettingsRepository"/> sweeps.
/// Span events and links are removed by the schema's <c>ON DELETE CASCADE</c> foreign keys, so
/// the trace sweep targets only <c>spans</c>.
///
/// The metric sweep is the exception to that pattern: it targets the data-point tables directly
/// on <c>time_unix_nano</c> rather than cascading from <c>metrics</c>, because after the 2.7.0
/// dedup a catalog row's <c>created_at</c> is "first seen" and cascading from it would discard a
/// metric's entire history.
///
/// The trace sweep is batched; the metric and log sweeps are not, and that asymmetry is deliberate.
/// <c>spans</c> is the largest table in the schema and the only one nothing else prunes, so its first
/// sweep is the biggest delete this system will ever run -- and on Postgres one enormous DELETE holds
/// a transaction open long enough to block autovacuum database-wide, emits all its WAL at once, and
/// loses every bit of progress if it is interrupted. The other two are left as single statements
/// because no index suits a bounded batch: the metric data-point tables are covered by BRIN and a
/// composite (metric_id, time_unix_nano), both of which serve one bulk delete well and a LIMIT badly.
/// Batching those would mean adding indexes, which is a schema change.
/// </summary>
public class PostgreSqlRetentionSettingsRepository(NpgsqlDataSource dataSource)
    : RetentionSettingsRepositoryBase
{
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);

    /// <summary>
    /// Rows removed per statement by the trace sweep. Bounded so a large first sweep does not run as
    /// one long transaction -- on Postgres that would block autovacuum from reclaiming dead tuples
    /// across the whole database, not just this table.
    /// </summary>
    private const int DeleteBatchSize = 50_000;

    /// <summary>
    /// Postgres has neither <c>DELETE TOP (n)</c> nor <c>DELETE ... LIMIT n</c>, so the batch is
    /// bounded by selecting a capped set of keys first. Keyed on <c>id</c> (the identity primary
    /// key) with an <c>ORDER BY start_time_unix_nano</c> that costs nothing extra: <c>idx_duration</c>'s
    /// leading column already serves both the <c>WHERE</c> and the <c>ORDER BY</c> as one forward
    /// index scan, so the sweep removes oldest-first without a separate sort.
    ///
    /// The <c>DELETE</c> repeats the cutoff predicate even though every row the CTE selected
    /// already satisfies it. That is redundant on plain Postgres but load-bearing on Timescale,
    /// which subclasses this repository unchanged: <c>spans</c> became a hypertable in schema
    /// 2.11.0, and a <c>DELETE ... WHERE id IN (...)</c> with no predicate on the partition column
    /// has to visit every chunk. Naming <c>start_time_unix_nano</c> lets chunk exclusion confine
    /// the delete to the oldest chunks, which is where all of its rows are anyway.
    /// </summary>
    private static readonly string TraceSweepSql = $"""
        WITH doomed AS (
            SELECT id FROM spans
            WHERE start_time_unix_nano < @cutoff
            ORDER BY start_time_unix_nano
            LIMIT {DeleteBatchSize}
        )
        DELETE FROM spans
        WHERE start_time_unix_nano < @cutoff AND id IN (SELECT id FROM doomed)
        """;

    public override async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // Each statement commits on its own -- that is the whole point, so do NOT wrap this loop in a
        // transaction. Doing so would reproduce exactly the long-running transaction the batching is
        // here to avoid. The loop terminates because the cutoff is computed once, so rows arriving
        // during the sweep are never older than it.
        var total = 0;
        int batch;
        do
        {
            batch = await conn.ExecuteAsync(new CommandDefinition(
                TraceSweepSql, new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
            total += batch;
        } while (batch == DeleteBatchSize);

        return total;
    }

    public override async Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        var total = 0;
        foreach (var table in TelemetryIngestionHelpers.TimePrunedMetricTables)
        {
            total += await conn.ExecuteAsync(new CommandDefinition(
                $"DELETE FROM {table} WHERE time_unix_nano < @cutoff",
                new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
        }

        return total;
    }

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffNano = CutoffNano(retentionPeriod);

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        var removed = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM log_records WHERE time_unix_nano < @cutoff",
            new { cutoff = cutoffNano }, cancellationToken: cancellationToken));
        await SweepLogRollupTablesAsync(conn, cutoffNano, cancellationToken);
        return removed;
    }
}
