using System.Globalization;
using Dapper;
using DuckDB.NET.Data;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// Writes one Parquet file per call with DuckDB (README U1): rows go through the appender into a temp table (nested
/// columns as JSON text), then <c>COPY (SELECT ... ORDER BY &lt;sort key&gt;) TO &lt;temp path&gt;</c>.
/// One in-memory database per process holds the memory, thread and temp-directory limits; each write takes its own
/// connection to it. The file is written wherever <c>tempPath</c> points; committing it is the caller's job.
/// </summary>
public sealed class ParquetFileWriter : IDisposable
{
    private readonly DuckDBConnection _root;
    private readonly ParquetOptions _options;

    public ParquetFileWriter(ParquetOptions options, string tempDirectory)
    {
        _options = options;
        var limits = options.Writer.DuckDb;
        _root = new DuckDBConnection("DataSource=:memory:");
        _root.Open();
        Directory.CreateDirectory(tempDirectory);
        _root.Execute($"SET memory_limit='{limits.MemoryLimit}'");
        _root.Execute($"SET threads={limits.Threads ?? Environment.ProcessorCount}");
        _root.Execute($"SET temp_directory='{Escape(limits.TempDirectory ?? tempDirectory)}'");
        _root.Execute("SET preserve_insertion_order=false");
    }

    /// <summary>
    /// Writes <paramref name="items"/> to <paramref name="tempPath"/> and returns the file's size in bytes.
    /// <paramref name="append"/> appends one item's values, in <see cref="ParquetTable.Columns"/> order, and ends the row.
    /// </summary>
    public Task<long> WriteAsync<T>(
        ParquetTable table, IReadOnlyList<T> items, Action<T, IDuckDBAppenderRow> append, string tempPath, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var stage = "stage_" + Guid.NewGuid().ToString("N");
            using var conn = _root.Duplicate();
            conn.Open();
            conn.Execute($"CREATE TEMP TABLE {stage} ({table.StagingColumnList})");
            try
            {
                using (var appender = conn.CreateAppender(stage))
                {
                    foreach (var item in items)
                    {
                        var row = appender.CreateRow();
                        append(item, row);
                        row.EndRow();
                    }
                }

                ct.ThrowIfCancellationRequested();
                conn.Execute(
                    $"COPY (SELECT {table.SelectList} FROM {stage} ORDER BY {table.SortKey}) TO '{Escape(tempPath)}' " +
                    $"(FORMAT parquet, COMPRESSION {_options.Compression.ToLowerInvariant()}, " +
                    $"ROW_GROUP_SIZE {_options.RowGroupRows.ToString(CultureInfo.InvariantCulture)})");
            }
            finally
            {
                conn.Execute($"DROP TABLE IF EXISTS {stage}");
            }
            return new FileInfo(tempPath).Length;
        }, ct);

    private static string Escape(string value) => value.Replace("'", "''");

    public void Dispose() => _root.Dispose();
}
