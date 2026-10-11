using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Keryhe.Telemetry.Core;

namespace Keryhe.Telemetry.Parquet.Tests;

public class StartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parquet-startup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ServiceProvider Collector(string? writerId = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:Parquet:RootPath"] = _root,
            ["Telemetry:Parquet:WriterId"] = writerId
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddParquetCollectorServices(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheCollectorCreatesTheRootAndSchemaFileAndResolvesTheBulkWriter()
    {
        using var sp = Collector();
        Assert.IsType<ParquetBulkWriter>(sp.GetRequiredService<ITelemetryBulkWriter>());
        var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "_schema.json"))).RootElement;
        Assert.Equal("4.0.0", schema.GetProperty("version").GetString());
        Assert.Equal(ParquetTables.DefinitionHash(), schema.GetProperty("tablesHash").GetString());
        Assert.True(ParquetWriterIdentity.IsValidId(sp.GetRequiredService<ParquetWriterIdentity>().Id));
    }

    [Fact]
    public void AWrongSchemaVersionFailsNamingBothVersions()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "_schema.json"),
            JsonSerializer.Serialize(new { component = "telemetry", version = "3.0.0", tablesHash = ParquetTables.DefinitionHash() }));
        using var sp = Collector();
        var e = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<ITelemetryBulkWriter>());
        Assert.Contains("3.0.0", e.Message);
        Assert.Contains("4.0.0", e.Message);
    }

    [Fact]
    public void ADifferentTableHashFailsNamingBothHashes()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "_schema.json"),
            JsonSerializer.Serialize(new { component = "telemetry", version = "4.0.0", tablesHash = "deadbeef" }));
        using var sp = Collector();
        var e = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<ITelemetryBulkWriter>());
        Assert.Contains("deadbeef", e.Message);
        Assert.Contains(ParquetTables.DefinitionHash(), e.Message);
    }

    [Fact]
    public void AnApiHostRefusesARootTheCollectorHasNotInitialized()
    {
        Directory.CreateDirectory(_root);
        var e = Assert.Throws<InvalidOperationException>(() => ParquetSchemaFile.Verify(_root));
        Assert.Contains("_schema.json", e.Message);
    }

    [Fact]
    public void ASecondLiveCollectorWithTheSameWriterIdFailsStartup()
    {
        using var first = Collector("shared-id");
        first.GetRequiredService<ParquetWriterIdentity>();
        using var second = Collector("shared-id");
        var e = Assert.Throws<InvalidOperationException>(() => second.GetRequiredService<ParquetWriterIdentity>());
        Assert.Contains("shared-id", e.Message);
        Assert.Contains("already in use", e.Message);
    }

    [Fact]
    public void ARestartedCollectorWithoutAConfiguredIdKeepsItsSavedId_AndASecondLiveOneGetsItsOwn()
    {
        string firstId;
        using (var a = Collector()) firstId = a.GetRequiredService<ParquetWriterIdentity>().Id;
        using var again = Collector();
        Assert.Equal(firstId, again.GetRequiredService<ParquetWriterIdentity>().Id);
        using var concurrent = Collector();
        Assert.NotEqual(firstId, concurrent.GetRequiredService<ParquetWriterIdentity>().Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("../x")]
    public void InvalidOptionsFailValidation(string writerId)
    {
        var options = new ParquetOptions { RootPath = _root, WriterId = writerId };
        Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Throws<InvalidOperationException>(new ParquetOptions().Validate); // no RootPath
        Assert.Throws<InvalidOperationException>(new ParquetOptions { RootPath = _root, Compression = "lz4" }.Validate);
        Assert.Throws<InvalidOperationException>(new ParquetOptions { RootPath = _root, Writer = new() { DuckDb = new() { MemoryLimit = "1GB'; DROP" } } }.Validate);
    }

    [Fact]
    public void TheDefaultsAreValid() => new ParquetOptions { RootPath = _root }.Validate();
}
