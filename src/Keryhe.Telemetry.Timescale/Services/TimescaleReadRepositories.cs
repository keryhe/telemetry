using Dapper;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Keryhe.Telemetry.PostgreSQL.Services;

namespace Keryhe.Telemetry.Timescale.Services;

// =============================================================================
// TimescaleDB read repositories.
//
// The read SQL is identical to plain PostgreSQL (same logical table/column set), so the
// Timescale variants inherit the PostgreSQL implementations unchanged. Retention is the one
// place the two diverge: Timescale drops whole chunks (see
// TimescaleRetentionSettingsRepository).
// =============================================================================

public sealed class TimescaleTraceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : PostgreSqlTraceReadRepository(dataSource, tenantContext, configuration)
{
    /// <summary>
    /// A hypertable cannot seek a trace id across chunks: <c>idx_spans_trace_span</c> exists per chunk, so a by-trace read
    /// probes every chunk (and every compressed one) however few spans match. With the trace's start time the read is bounded
    /// to <c>[start - margin, end + margin]</c> and constraint exclusion skips the rest. The range is the trace's own extent, not an
    /// open-ended window: a wide range on a chunk that is still being written gave the planner a time-index path it misjudged from stale
    /// statistics (stress run: 16 -> 29 ms). Plain PostgreSQL has one index and needs none of this.
    /// </summary>
    protected override (long Min, long Max)? HintedTraceTimeBounds(long startHintNano, long endHintNano)
        => (startHintNano - TraceHintMarginNanos, endHintNano + TraceHintMarginNanos);
}

public sealed class TimescaleMetricReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : PostgreSqlMetricReadRepository(dataSource, tenantContext, configuration);

public sealed class TimescaleLogReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : PostgreSqlLogReadRepository(dataSource, tenantContext, configuration);

public sealed class TimescaleResourceReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlResourceReadRepository(dataSource, tenantContext);

public sealed class TimescaleAlertRuleRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext)
    : PostgreSqlAlertRuleRepository(dataSource, tenantContext);

public sealed class TimescaleTenantCatalogRepository(NpgsqlDataSource dataSource)
    : PostgreSqlTenantCatalogRepository(dataSource);

/// <summary>
/// Timescale retention: <c>drop_chunks</c> per hypertable with the windows from
/// <c>retention_settings</c> (schema 3.0.0). Retention granularity is therefore the chunk interval
/// (6 h for spans and logs, 12-24 h for data points): a row survives until its whole chunk is older
/// than the cutoff, so a row up to one chunk interval past its window may still be present.
///
/// Not the plain-Postgres batched DELETE: a hypertable is many chunk tables, <c>ctid</c> is only
/// unique within one of them, and dropping a chunk is a metadata operation where deleting its rows
/// would leave dead tuples for vacuum. The returned count is the chunks' approximate row count
/// (<c>approximate_row_count</c>, taken just before the drop) -- an estimate, because counting
/// exactly would scan the very data being dropped.
/// </summary>
public sealed class TimescaleRetentionSettingsRepository(NpgsqlDataSource dataSource)
    : PostgreSqlRetentionSettingsRepository(dataSource)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;

    // The rollup hypertables follow their signal's window (summary-rollups plan, decision 8); their
    // rows are not part of the returned count.
    public override async Task<int> DeleteOldTracesAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await DropChunksAsync(["spans"], retentionPeriod, cancellationToken);
        await DropChunksAsync(["request_rollup_minute"], retentionPeriod, cancellationToken);
        return removed;
    }

    public override async Task<int> DeleteOldLogRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var removed = await DropChunksAsync(["log_records"], retentionPeriod, cancellationToken);
        await DropChunksAsync(["log_rollup_minute"], retentionPeriod, cancellationToken);
        return removed;
    }

    public override Task<int> DeleteOldMetricDataPointsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
        => DropChunksAsync(TelemetryIngestionHelpers.TimePrunedMetricTables, retentionPeriod, cancellationToken);

    private async Task<int> DropChunksAsync(IReadOnlyList<string> hypertables, TimeSpan retentionPeriod, CancellationToken ct)
    {
        var cutoff = CutoffNano(retentionPeriod);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        long total = 0;
        foreach (var table in hypertables)
        {
            var chunks = (await conn.QueryAsync<string>(new CommandDefinition(
                "SELECT show_chunks(@table::regclass, older_than => @cutoff::bigint)::text",
                new { table, cutoff }, cancellationToken: ct))).ToList();
            if (chunks.Count == 0) continue;

            foreach (var chunk in chunks)
            {
                // Statistics-based and instant; a chunk that has never been analyzed reports nothing
                // (<= 0), in which case it is small or new and an exact count is cheap.
                var estimate = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT approximate_row_count(@chunk::regclass)", new { chunk }, cancellationToken: ct));
                total += estimate > 0
                    ? estimate
                    : await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT count(*) FROM {chunk}", cancellationToken: ct));
            }

            await conn.ExecuteAsync(new CommandDefinition(
                "SELECT drop_chunks(@table::regclass, older_than => @cutoff::bigint)",
                new { table, cutoff }, cancellationToken: ct));
        }
        return (int)Math.Min(total, int.MaxValue);
    }
}

public sealed class TimescaleRollupReadRepository(NpgsqlDataSource dataSource, ITenantContext tenantContext, IConfiguration configuration)
    : PostgreSqlRollupReadRepository(dataSource, tenantContext, configuration);
