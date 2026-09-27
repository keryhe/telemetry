using System.Data.Common;
using Dapper;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Shared (Postgres/Timescale/SqlServer/MySql) implementation of <see cref="IRollupRepository"/>.
/// ClickHouse gets a wholly separate implementation (<c>ClickHouseLogRollupRepository</c>) — its
/// best-effort lease and <c>ReplacingMergeTree</c> dedup strategy don't fit this base's
/// delete-then-insert-in-one-transaction shape.
///
/// Not tenant-scoped: rollups aggregate every tenant's resources at once (
/// <c>log_rollup_minute</c>/<c>_hour</c> carry no tenant column, the same as <c>log_records</c>
/// itself — tenant scoping happens at read time via a join to <c>resources</c>, same as every
/// other signal table).
/// </summary>
public abstract class LogRollupRepositoryBase : IRollupRepository
{
    protected const long NanosPerMinute = 60_000_000_000L;
    protected const long NanosPerHour = 3_600_000_000_000L;

    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The connection the recompute (raw-data-reading) half of <see cref="RollLogMinutesAsync"/>
    /// reads through. Identical to <see cref="OpenConnectionAsync"/> everywhere except SqlServer,
    /// which overrides this to a SNAPSHOT-isolated, DEADLOCK_PRIORITY LOW connection (decision 35)
    /// so the recompute never takes shared locks against ingestion — the lease claim and state
    /// reads/writes stay on the plain read-committed connection from <see cref="OpenConnectionAsync"/>.
    /// </summary>
    protected virtual Task<DbConnection> OpenRecomputeConnectionAsync(CancellationToken cancellationToken) => OpenConnectionAsync(cancellationToken);

    /// <summary>Integer floor-division, overridden by MySql (whose <c>/</c> promotes to DECIMAL) to use <c>DIV</c>.</summary>
    protected virtual string IntDivExpr(string numerator, string denominator) => $"({numerator} / {denominator})";

    /// <summary>
    /// Starts the recompute transaction. MySql overrides this to READ COMMITTED: under InnoDB's
    /// default REPEATABLE READ, the recompute's <c>INSERT ... SELECT</c> would take shared locks on
    /// the <c>log_records</c> rows it reads, blocking ingestion.
    /// </summary>
    protected virtual async Task<DbTransaction> BeginRecomputeTransactionAsync(DbConnection conn, CancellationToken cancellationToken)
        => await conn.BeginTransactionAsync(cancellationToken);

    public async Task<bool> TryClaimLeaseAsync(string signal, string granularity, string owner, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var newExpiry = now + leaseDuration;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE rollup_state
            SET lease_owner = @owner, lease_expires_at = @newExpiry
            WHERE signal_name = @signal AND granularity = @granularity AND lease_expires_at < @now
            """,
            new { owner, newExpiry, signal, granularity, now }, cancellationToken: ct));
        return rows > 0;
    }

    public async Task<RollupStateInfo?> GetStateAsync(string signal, string granularity, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RollupStateInfo>(new CommandDefinition(
            """
            SELECT coverage_start_unix_nano AS CoverageStartUnixNano,
                   rolled_until_unix_nano   AS RolledUntilUnixNano,
                   repassed_until_unix_nano AS RepassedUntilUnixNano
            FROM rollup_state WHERE signal_name = @signal AND granularity = @granularity
            """,
            new { signal, granularity }, cancellationToken: ct));
    }

    public virtual async Task SetCoverageStartAsync(string signal, string granularity, long coverageStartUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE rollup_state SET coverage_start_unix_nano = @coverageStartUnixNano
            WHERE signal_name = @signal AND granularity = @granularity AND coverage_start_unix_nano IS NULL
            """,
            new { coverageStartUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public virtual async Task AdvanceRolledUntilAsync(string signal, string granularity, long rolledUntilUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rollup_state SET rolled_until_unix_nano = @v WHERE signal_name = @signal AND granularity = @granularity",
            new { v = rolledUntilUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public virtual async Task AdvanceRepassedUntilAsync(string signal, string granularity, long repassedUntilUnixNano, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE rollup_state SET repassed_until_unix_nano = @v WHERE signal_name = @signal AND granularity = @granularity",
            new { v = repassedUntilUnixNano, signal, granularity }, cancellationToken: ct));
    }

    public virtual async Task RollLogMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        var bucketExpr = $"({IntDivExpr("lr.time_unix_nano", NanosPerMinute.ToString())} * {NanosPerMinute})";
        var deleteSql = "DELETE FROM log_rollup_minute WHERE bucket_unix_nano >= @from AND bucket_unix_nano < @to";
        var insertSql = $"""
            INSERT INTO log_rollup_minute (bucket_unix_nano, resource_id, trace_count, debug_count, info_count, warn_count, error_count, fatal_count)
            SELECT {bucketExpr}, lr.resource_id, {LogSeverityGroupSql.SumCaseColumns("lr.severity_number")}
            FROM log_records lr
            WHERE lr.time_unix_nano >= @from AND lr.time_unix_nano < @to
            GROUP BY {bucketExpr}, lr.resource_id
            """;

        // Two statements rather than one semicolon-joined batch: MySqlConnector needs
        // `Allow User Variables`/multi-statement opt-in to run a batch, which this connection
        // string doesn't set, and there's no benefit to a batch here since both statements already
        // run in the same transaction.
        await using var conn = await OpenRecomputeConnectionAsync(ct);
        await using var tx = await BeginRecomputeTransactionAsync(conn, ct);
        var args = new { from = fromInclusive, to = toExclusive };
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, args, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertSql, args, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public virtual async Task RollLogHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        var bucketExpr = $"({IntDivExpr("lrm.bucket_unix_nano", NanosPerHour.ToString())} * {NanosPerHour})";
        var deleteSql = "DELETE FROM log_rollup_hour WHERE bucket_unix_nano >= @from AND bucket_unix_nano < @to";
        var insertSql = $"""
            INSERT INTO log_rollup_hour (bucket_unix_nano, resource_id, trace_count, debug_count, info_count, warn_count, error_count, fatal_count)
            SELECT {bucketExpr}, lrm.resource_id,
                   SUM(lrm.trace_count), SUM(lrm.debug_count), SUM(lrm.info_count),
                   SUM(lrm.warn_count), SUM(lrm.error_count), SUM(lrm.fatal_count)
            FROM log_rollup_minute lrm
            WHERE lrm.bucket_unix_nano >= @from AND lrm.bucket_unix_nano < @to
            GROUP BY {bucketExpr}, lrm.resource_id
            """;

        await using var conn = await OpenRecomputeConnectionAsync(ct);
        await using var tx = await BeginRecomputeTransactionAsync(conn, ct);
        var args = new { from = fromInclusive, to = toExclusive };
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, args, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertSql, args, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // =========================================================================
    // TRACE ROLLUPS (list-pages-server-side plan, Phase 3, decisions 37-38, 41)
    // =========================================================================

    /// <summary>
    /// Orphan detection (decision 41): for traces with a span starting in the range and no
    /// null-parent span anywhere in the trace, finds the earliest span (ties broken by the lowest
    /// span_id, an arbitrary but deterministic pick) and keeps it only when that span's own parent
    /// does not exist as any span_id. Delete-then-insert in one transaction, same shape as the log
    /// recompute, so a re-roll both adds newly-detected orphans and removes rows whose real root
    /// has since arrived.
    /// </summary>
    public virtual async Task RollOrphanRootsAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        const string deleteSql = "DELETE FROM orphan_roots WHERE start_time_unix_nano >= @from AND start_time_unix_nano < @to";
        const string insertSql = """
            INSERT INTO orphan_roots (trace_id, span_id, resource_id, start_time_unix_nano, end_time_unix_nano)
            SELECT e2.trace_id, e2.span_id, e2.resource_id, e2.start_time_unix_nano, e2.end_time_unix_nano
            FROM (
                SELECT ea.trace_id, ea.span_id
                FROM (
                    SELECT e.trace_id, MIN(e.span_id) AS span_id
                    FROM spans e
                    JOIN (
                        SELECT s.trace_id,
                               MIN(s.start_time_unix_nano) AS min_start,
                               SUM(CASE WHEN s.parent_span_id IS NULL THEN 1 ELSE 0 END) AS null_parent_count
                        FROM spans s
                        WHERE s.trace_id IN (
                            SELECT DISTINCT s2.trace_id FROM spans s2
                            WHERE s2.start_time_unix_nano >= @from AND s2.start_time_unix_nano < @to
                        )
                        GROUP BY s.trace_id
                    ) c ON c.trace_id = e.trace_id AND c.min_start = e.start_time_unix_nano
                    WHERE c.null_parent_count = 0
                    GROUP BY e.trace_id
                ) ea
            ) earliest
            JOIN spans e2 ON e2.trace_id = earliest.trace_id AND e2.span_id = earliest.span_id
            WHERE NOT EXISTS (SELECT 1 FROM spans p WHERE p.span_id = e2.parent_span_id)
            """;

        await using var conn = await OpenRecomputeConnectionAsync(ct);
        await using var tx = await BeginRecomputeTransactionAsync(conn, ct);
        var args = new { from = fromInclusive, to = toExclusive };
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, args, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertSql, args, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Trace summary recompute (decisions 37-38, 41). Anchors are null-parent roots plus
    /// <c>orphan_roots</c> (with the late-root <c>NOT EXISTS</c> re-check, so a trace whose real
    /// root arrived after <see cref="RollOrphanRootsAsync"/>'s last pass for this range is never
    /// double-counted); each anchor trace's error flag and whole-trace duration are aggregated
    /// over its full span set via <c>spans.trace_id</c> (the <c>uk_trace_span</c> prefix). Root
    /// names are folded to <c>'__other__'</c> past the 200-distinct-name-per-(bucket,resource)
    /// cardinality guard, ranked by trace count via <c>ROW_NUMBER()</c>.
    /// </summary>
    public virtual async Task RollTraceMinutesAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        var bucketExpr = $"({IntDivExpr("a.anchor_start", NanosPerMinute.ToString())} * {NanosPerMinute})";
        var lbColumns = string.Join(", ", LatencyBucketSql.ColumnNames());
        var lbSelect = LatencyBucketSql.SumCaseColumns("f.duration_nano");

        const string deleteSql = "DELETE FROM trace_rollup_minute WHERE bucket_unix_nano >= @from AND bucket_unix_nano < @to";

        // Nested derived tables rather than a WITH clause: PostgreSQL/SqlServer accept WITH before
        // INSERT, but MySQL attaches WITH only to the query expression that follows it (it must
        // come after "INSERT INTO tbl (cols)", not before INSERT) -- rather than special-case that
        // placement per provider, the anchors/trace_level computation is written twice (once for
        // the per-trace aggregate, once for the name-popularity ranking) as plain subqueries,
        // which every provider parses the same way.
        var anchorsSql = $"""
            (
                SELECT s.trace_id AS trace_id, s.resource_id AS resource_id, s.name AS root_name,
                       s.kind AS anchor_kind, s.start_time_unix_nano AS anchor_start
                FROM spans s
                WHERE s.parent_span_id IS NULL AND s.start_time_unix_nano >= @from AND s.start_time_unix_nano < @to
                UNION ALL
                SELECT o.trace_id, o.resource_id, sp.name, sp.kind, o.start_time_unix_nano
                FROM orphan_roots o
                JOIN spans sp ON sp.trace_id = o.trace_id AND sp.span_id = o.span_id
                WHERE o.start_time_unix_nano >= @from AND o.start_time_unix_nano < @to
                  AND NOT EXISTS (SELECT 1 FROM spans np WHERE np.trace_id = o.trace_id AND np.parent_span_id IS NULL)
            )
            """;

        var insertSql = $"""
            INSERT INTO trace_rollup_minute (bucket_unix_nano, resource_id, root_name, inbound, trace_count, error_count, duration_sum_ms, duration_max_ms, {lbColumns})
            SELECT f.bucket, f.resource_id, f.root_name, f.inbound,
                   COUNT(*), SUM(f.has_error),
                   SUM(f.duration_nano) / 1000000.0, MAX(f.duration_nano) / 1000000.0,
                   {lbSelect}
            FROM (
                SELECT tl.bucket AS bucket, tl.resource_id AS resource_id,
                       CASE WHEN rn.rn <= 200 THEN tl.root_name ELSE '__other__' END AS root_name,
                       tl.inbound AS inbound, tl.has_error AS has_error, tl.duration_nano AS duration_nano
                FROM (
                    SELECT a.trace_id AS trace_id, a.resource_id AS resource_id, a.root_name AS root_name,
                           CASE WHEN a.anchor_kind IN ('SERVER', 'CONSUMER') THEN 1 ELSE 0 END AS inbound,
                           {bucketExpr} AS bucket,
                           MAX(CASE WHEN fs.status_code = 'ERROR' THEN 1 ELSE 0 END) AS has_error,
                           (MAX(fs.end_time_unix_nano) - MIN(fs.start_time_unix_nano)) AS duration_nano
                    FROM {anchorsSql} a
                    JOIN spans fs ON fs.trace_id = a.trace_id
                    GROUP BY a.trace_id, a.resource_id, a.root_name, a.anchor_kind, a.anchor_start
                ) tl
                JOIN (
                    SELECT bucket, resource_id, root_name,
                           ROW_NUMBER() OVER (PARTITION BY bucket, resource_id ORDER BY cnt DESC, root_name) AS rn
                    FROM (
                        SELECT bucket, resource_id, root_name, COUNT(*) AS cnt
                        FROM (
                            SELECT a.trace_id AS trace_id, a.resource_id AS resource_id, a.root_name AS root_name,
                                   {bucketExpr} AS bucket
                            FROM {anchorsSql} a
                        ) t2
                        GROUP BY bucket, resource_id, root_name
                    ) nc
                ) rn ON rn.bucket = tl.bucket AND rn.resource_id = tl.resource_id AND rn.root_name = tl.root_name
            ) f
            GROUP BY f.bucket, f.resource_id, f.root_name, f.inbound
            """;

        await using var conn = await OpenRecomputeConnectionAsync(ct);
        await using var tx = await BeginRecomputeTransactionAsync(conn, ct);
        var args = new { from = fromInclusive, to = toExclusive };
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, args, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertSql, args, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public virtual async Task RollTraceHoursAsync(long fromInclusive, long toExclusive, CancellationToken ct = default)
    {
        var bucketExpr = $"({IntDivExpr("trm.bucket_unix_nano", NanosPerHour.ToString())} * {NanosPerHour})";
        var lbColumns = string.Join(", ", LatencyBucketSql.ColumnNames());
        var lbSums = string.Join(",\n                   ", LatencyBucketSql.ColumnNames().Select(c => $"SUM(trm.{c})"));

        var deleteSql = "DELETE FROM trace_rollup_hour WHERE bucket_unix_nano >= @from AND bucket_unix_nano < @to";
        var insertSql = $"""
            INSERT INTO trace_rollup_hour (bucket_unix_nano, resource_id, root_name, inbound, trace_count, error_count, duration_sum_ms, duration_max_ms, {lbColumns})
            SELECT {bucketExpr}, trm.resource_id, trm.root_name, trm.inbound,
                   SUM(trm.trace_count), SUM(trm.error_count),
                   SUM(trm.duration_sum_ms), MAX(trm.duration_max_ms),
                   {lbSums}
            FROM trace_rollup_minute trm
            WHERE trm.bucket_unix_nano >= @from AND trm.bucket_unix_nano < @to
            GROUP BY {bucketExpr}, trm.resource_id, trm.root_name, trm.inbound
            """;

        await using var conn = await OpenRecomputeConnectionAsync(ct);
        await using var tx = await BeginRecomputeTransactionAsync(conn, ct);
        var args = new { from = fromInclusive, to = toExclusive };
        await conn.ExecuteAsync(new CommandDefinition(deleteSql, args, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(insertSql, args, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }
}
