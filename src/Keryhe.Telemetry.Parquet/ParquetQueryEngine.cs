using System.Text;
using DuckDB.NET.Data;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Parquet;

/// <summary>What a statement needs to see: a table, for one tenant, over a time range of its partition time (null = unbounded).</summary>
public readonly record struct ParquetViewScope(ParquetTable Table, long TenantId, long? FromNano, long? ToNano);

/// <summary>
/// Resolves the Parquet files a read sees (README "Layout": the writers' manifests, the compacted generation and a merged
/// day's files, <see cref="ParquetManifests"/>). Partitions are tenant-day-hour folders, so a time range names whole hours;
/// the in-file row-group statistics do the rest.
/// </summary>
public sealed class ParquetFileResolver(IParquetFileStore store)
{
    public async Task<List<string>> ResolveAsync(ParquetViewScope scope, CancellationToken ct)
    {
        var from = scope.FromNano is { } f ? DateOnly.FromDateTime(DateTime.UnixEpoch.AddTicks(Math.Max(0L, f) / 100)) : DateOnly.MinValue;
        var to = scope.ToNano is { } t ? DateOnly.FromDateTime(DateTime.UnixEpoch.AddTicks(Math.Max(0L, t) / 100)) : DateOnly.MaxValue;

        var hours = new List<ParquetPartition>();
        foreach (var partition in store.EnumeratePartitions(scope.Table.Name, scope.TenantId, from, to).OrderBy(p => p.Date).ThenBy(p => p.Hour))
        {
            if (scope.FromNano is { } lo && partition.EndNano <= lo) continue;
            if (scope.ToNano is { } hi && partition.StartNano > hi) continue;
            hours.Add(partition);
        }
        var resolved = await ParquetManifests.ResolveAsync(store, hours, ct);
        return [.. resolved.Select(r => r.PathIn(store))];
    }
}

/// <summary>
/// The API host's read engine (README D5, phase 3 decision 3): one in-memory DuckDB database for the process, holding the
/// <c>memory_limit</c>, <c>threads</c> and <c>temp_directory</c> of <see cref="ParquetOptions.DuckDb"/>, and a connection per
/// statement group with the temp views its scopes name, so SQL reads <c>spans</c>, <c>trace_index</c> and so on. A view with no
/// files is an empty relation of the right types. Never a glob: the file list comes from the manifests.
/// </summary>
public sealed class ParquetQueryEngine : IDisposable
{
    private readonly DuckDBConnection _root;
    private readonly ParquetFileResolver _resolver;
    private readonly ILogger? _log;

    public ParquetQueryEngine(IOptions<ParquetOptions> options, ParquetFileResolver resolver, ILogger<ParquetQueryEngine>? log = null)
    {
        _resolver = resolver;
        _log = log;
        var o = options.Value;
        var limits = o.DuckDb;
        var temp = limits.TempDirectory ?? Path.Combine(o.RootPath, "_tmp", Environment.MachineName + "-api");
        Directory.CreateDirectory(temp);
        _root = new DuckDBConnection("DataSource=:memory:");
        _root.Open();
        _root.Execute($"SET memory_limit='{limits.MemoryLimit}'");
        _root.Execute($"SET threads={limits.Threads ?? Environment.ProcessorCount}");
        _root.Execute($"SET temp_directory='{temp.Replace("'", "''")}'");
    }

    /// <summary>Opens a connection with the scopes' views defined. The caller disposes it.</summary>
    public async Task<NamedParameterConnection> OpenAsync(IEnumerable<ParquetViewScope> scopes, CancellationToken ct)
    {
        var connection = _root.Duplicate();
        try
        {
            connection.Open();
            var filesRead = 0;
            foreach (var scope in scopes)
            {
                var files = await _resolver.ResolveAsync(scope, ct);
                filesRead += files.Count;
                connection.Execute($"CREATE OR REPLACE TEMP VIEW {scope.Table.Name} AS {ViewSelect(scope.Table, files)}");
            }
            return new NamedParameterConnection(connection, _log) { FilesRead = filesRead };
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>(Re)defines one view on an open connection, for a statement that needs a wider or narrower range than it opened with.</summary>
    public async Task DefineAsync(NamedParameterConnection connection, ParquetViewScope scope, CancellationToken ct)
    {
        var files = await _resolver.ResolveAsync(scope, ct);
        connection.Inner.Execute($"CREATE OR REPLACE TEMP VIEW {scope.Table.Name} AS {ViewSelect(scope.Table, files)}");
        connection.FilesRead += files.Count;
    }

    internal static string ViewSelect(ParquetTable table, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return "SELECT " + string.Join(", ", table.Columns.Select(c => $"CAST(NULL AS {c.Type}) AS \"{c.Name}\"")) + " WHERE false";

        var list = new StringBuilder();
        foreach (var file in files)
        {
            if (list.Length > 0) list.Append(", ");
            list.Append('\'').Append(file.Replace("'", "''")).Append('\'');
        }
        return $"SELECT * FROM read_parquet([{list}])";
    }

    public void Dispose() => _root.Dispose();
}
