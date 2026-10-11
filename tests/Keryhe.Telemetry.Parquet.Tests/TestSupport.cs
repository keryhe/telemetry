using Dapper;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Parquet.Tests;

/// <summary>A temp root with a collector's writer wired the way the DI registration does.</summary>
internal sealed class ParquetFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "parquet-tests-" + Guid.NewGuid().ToString("N"));
    public LocalParquetFileStore Store { get; }
    public ParquetWriterIdentity Identity { get; }
    public ParquetFileWriter FileWriter { get; }
    public ParquetBulkWriter Writer { get; }

    public ParquetFixture(string? writerId = "w1")
    {
        ParquetSchemaFile.EnsureAndVerify(Root);
        Store = new LocalParquetFileStore(Root);
        Identity = ParquetWriterIdentity.Acquire(Root, writerId);
        var options = new ParquetOptions { RootPath = Root };
        FileWriter = new ParquetFileWriter(options, Path.Combine(Root, "_tmp"));
        Writer = new ParquetBulkWriter(Store, FileWriter, new ManifestWriter(Store, Identity.Id), NullLogger<ParquetBulkWriter>.Instance);
    }

    public async Task<List<string>> FilesAsync(ParquetPartition partition)
        => [.. (await ParquetManifests.ResolveAsync(Store, [partition], default)).Select(f => f.File.Name)];

    public string[] AllParquetFiles(string table)
        => Directory.Exists(Path.Combine(Root, table))
            ? Directory.GetFiles(Path.Combine(Root, table), "*.parquet", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}")).ToArray()
            : [];

    /// <summary>Runs a query over the given files, the way a reader will.</summary>
    public static T Scalar<T>(IEnumerable<string> files, string select, string where = "true")
    {
        using var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        var list = string.Join(", ", files.Select(f => $"'{f.Replace("'", "''")}'"));
        return conn.ExecuteScalar<T>($"SELECT {select} FROM read_parquet([{list}]) WHERE {where}")!;
    }

    public void Dispose()
    {
        FileWriter.Dispose();
        Identity.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

internal static class Spans
{
    public const long Hour = 3_600_000_000_000L;

    // 2023-11-14T22:13:20Z
    public const long BaseNano = 1_700_000_000_123_456_789L;

    public static ResourceModel Resource(long tenant, string service) => new()
    {
        TenantId = tenant,
        SchemaUrl = "https://example/schema",
        Attributes = new Dictionary<string, object> { ["service.name"] = service, ["host.name"] = "h1" }
    };

    public static SpanModel Span(ResourceModel resource, long startNano, string name = "op", string? traceId = null, string? spanId = null)
        => new()
        {
            TraceIdHex = traceId ?? Guid.NewGuid().ToString("N"),
            SpanIdHex = spanId ?? Guid.NewGuid().ToString("N")[..16],
            Name = name,
            Kind = SpanKind.SERVER,
            StartTimeUnixNano = startNano,
            EndTimeUnixNano = startNano + 1_500_000,
            Resource = resource,
            InstrumentationScope = new InstrumentationScopeModel { Name = "scope", Version = "1.0" }
        };
}
