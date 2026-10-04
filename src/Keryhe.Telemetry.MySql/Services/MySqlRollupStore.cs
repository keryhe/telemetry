using MySqlConnector;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.MySql.Services;

/// <summary>
/// MySQL implementation of <see cref="IRollupStore"/> (plans/summary-rollups.md): multi-row
/// <c>INSERT</c>s of partial rows (rows arrive sorted by key, which avoids the secondary-index
/// deadlocks unordered inserts cause), chunked well under the placeholder limit.
/// </summary>
public class MySqlRollupStore(IConfiguration configuration) : IRollupStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    private const int ChunkSize = 500;
    private const string RequestColumns = "tenant_id, service_name, bucket_start_unix_nano, request_count, error_count, sum_duration_nanos, max_duration_nanos, h00, h01, h02, h03, h04, h05, h06, h07, h08, h09, h10, h11, h12, h13, h14, h15, h16, h17, h18, h19, h20, h21, h22, h23";

    public bool FedByViews => false;

    public async Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        const int columnCount = 7 + Core.Data.DurationBands.Count;
        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, rows.Count - offset);
            var values = string.Join(",", Enumerable.Range(0, count).Select(i =>
                "(" + string.Join(",", Enumerable.Range(0, columnCount).Select(c => $"@p{i}_{c}")) + ")"));
            await using var cmd = new MySqlCommand($"INSERT INTO request_rollup_minute ({RequestColumns}) VALUES {values}", conn);
            for (var i = 0; i < count; i++)
            {
                var r = rows[offset + i];
                cmd.Parameters.AddWithValue($"@p{i}_0", r.TenantId);
                cmd.Parameters.AddWithValue($"@p{i}_1", r.ServiceName);
                cmd.Parameters.AddWithValue($"@p{i}_2", r.BucketStartUnixNano);
                cmd.Parameters.AddWithValue($"@p{i}_3", r.RequestCount);
                cmd.Parameters.AddWithValue($"@p{i}_4", r.ErrorCount);
                cmd.Parameters.AddWithValue($"@p{i}_5", r.SumDurationNanos);
                cmd.Parameters.AddWithValue($"@p{i}_6", r.MaxDurationNanos);
                for (var b = 0; b < r.Bands.Length; b++)
                    cmd.Parameters.AddWithValue($"@p{i}_{7 + b}", r.Bands[b]);
            }
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        for (var offset = 0; offset < rows.Count; offset += ChunkSize)
        {
            var count = Math.Min(ChunkSize, rows.Count - offset);
            var values = string.Join(",", Enumerable.Range(0, count).Select(i => $"(@t{i}, @s{i}, @v{i}, @b{i}, @c{i})"));
            await using var cmd = new MySqlCommand(
                $"INSERT INTO log_rollup_minute (tenant_id, service_name, severity_number, bucket_start_unix_nano, record_count) VALUES {values}", conn);
            for (var i = 0; i < count; i++)
            {
                var r = rows[offset + i];
                cmd.Parameters.AddWithValue($"@t{i}", r.TenantId);
                cmd.Parameters.AddWithValue($"@s{i}", r.ServiceName);
                cmd.Parameters.AddWithValue($"@v{i}", r.SeverityNumber);
                cmd.Parameters.AddWithValue($"@b{i}", r.BucketStartUnixNano);
                cmd.Parameters.AddWithValue($"@c{i}", r.RecordCount);
            }
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
