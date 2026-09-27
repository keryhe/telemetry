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
}
