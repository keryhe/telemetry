using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.SqlServer.Services;

/// <summary>
/// SQL Server implementation of <see cref="IRollupStore"/> (plans/summary-rollups.md): a
/// <c>SqlBulkCopy</c> of partial rows, plain appends into clustered tables. <c>RollupWorker</c> caps
/// each call under SQL Server's lock-escalation threshold and sorts by key.
/// </summary>
public class SqlServerRollupStore(IConfiguration configuration) : IRollupStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Collector")!;

    public bool FedByViews => false;

    public async Task AppendRequestsAsync(IReadOnlyList<RequestRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var table = new DataTable();
        foreach (var name in new[] { "tenant_id", "bucket_start_unix_nano", "request_count", "error_count", "sum_duration_nanos", "max_duration_nanos" })
            table.Columns.Add(name, typeof(long));
        table.Columns.Add("service_name", typeof(string));
        for (var i = 0; i < Core.Data.DurationBands.Count; i++)
            table.Columns.Add($"h{i:00}", typeof(long));
        foreach (var r in rows)
        {
            var row = table.NewRow();
            row["tenant_id"] = r.TenantId;
            row["service_name"] = r.ServiceName;
            row["bucket_start_unix_nano"] = r.BucketStartUnixNano;
            row["request_count"] = r.RequestCount;
            row["error_count"] = r.ErrorCount;
            row["sum_duration_nanos"] = r.SumDurationNanos;
            row["max_duration_nanos"] = r.MaxDurationNanos;
            for (var i = 0; i < r.Bands.Length; i++)
                row[$"h{i:00}"] = r.Bands[i];
            table.Rows.Add(row);
        }
        await BulkCopyAsync("request_rollup_minute", table, cancellationToken);
    }

    public async Task AppendLogsAsync(IReadOnlyList<LogRollupRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        var table = new DataTable();
        table.Columns.Add("tenant_id", typeof(long));
        table.Columns.Add("service_name", typeof(string));
        table.Columns.Add("severity_number", typeof(int));
        table.Columns.Add("bucket_start_unix_nano", typeof(long));
        table.Columns.Add("record_count", typeof(long));
        foreach (var r in rows)
            table.Rows.Add(r.TenantId, r.ServiceName, r.SeverityNumber, r.BucketStartUnixNano, r.RecordCount);
        await BulkCopyAsync("log_rollup_minute", table, cancellationToken);
    }

    private async Task BulkCopyAsync(string destination, DataTable table, CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        using var copy = new SqlBulkCopy(conn) { DestinationTableName = destination, BatchSize = table.Rows.Count };
        foreach (DataColumn column in table.Columns)
            copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await copy.WriteToServerAsync(table, cancellationToken);
    }
}
