using Npgsql;
using NpgsqlTypes;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Timescale.Services;

/// <summary>
/// Timescale implementation of <see cref="IRollupStore"/> (plans/summary-rollups.md): a binary COPY of
/// partial rows into <c>request_rollup_minute</c> / <c>log_rollup_minute</c>. Plain appends, no
/// conflict handling, so concurrent collectors never contend.
/// </summary>
public class TimescaleRollupStore(NpgsqlDataSource dataSource) : IRollupStore
{
    public bool FedByViews => false;

    public async Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY request_rollup_minute (\"tenant_id\", \"service_name\", \"bucket_start_unix_nano\", \"request_count\", \"error_count\", \"sum_duration_nanos\", \"max_duration_nanos\", \"h00\", \"h01\", \"h02\", \"h03\", \"h04\", \"h05\", \"h06\", \"h07\", \"h08\", \"h09\", \"h10\", \"h11\", \"h12\", \"h13\", \"h14\", \"h15\", \"h16\", \"h17\", \"h18\", \"h19\", \"h20\", \"h21\", \"h22\", \"h23\") FROM STDIN (FORMAT BINARY)", cancellationToken);
        foreach (var r in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(r.TenantId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.ServiceName, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(r.BucketStartUnixNano, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.RequestCount, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.ErrorCount, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.SumDurationNanos, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.MaxDurationNanos, NpgsqlDbType.Bigint, cancellationToken);
            foreach (var band in r.Bands)
                await writer.WriteAsync(band, NpgsqlDbType.Bigint, cancellationToken);
        }
        await writer.CompleteAsync(cancellationToken);
    }

    public async Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY log_rollup_minute (\"tenant_id\", \"service_name\", \"severity_number\", \"bucket_start_unix_nano\", \"record_count\") FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (var r in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(r.TenantId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.ServiceName, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(r.SeverityNumber, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(r.BucketStartUnixNano, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(r.RecordCount, NpgsqlDbType.Bigint, cancellationToken);
        }
        await writer.CompleteAsync(cancellationToken);
    }
}
