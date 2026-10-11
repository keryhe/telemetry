using System.Text.Json;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// <c>_schema.json</c> at the root (phase 1 decision 5): the telemetry schema version and a hash of the table
/// definitions. The collector creates it on first start; both hosts refuse a root with another version or hash.
/// </summary>
public static class ParquetSchemaFile
{
    public const string FileName = "_schema.json";

    /// <summary>The telemetry component's schema version; the relational scripts' <c>TELEMETRY_TARGET_VERSION</c>.</summary>
    public const string TelemetryVersion = "4.0.0";

    private sealed record Content(string Component, string Version, string TablesHash);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static Content Expected() => new("telemetry", TelemetryVersion, ParquetTables.DefinitionHash());

    /// <summary>Creates the root and its schema file if absent (collector, <c>apply-schema.sh</c>), then verifies it.</summary>
    public static void EnsureAndVerify(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
        {
            var temp = Path.Combine(root, $"{FileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(Expected(), Options));
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temp); } // another collector created it first
        }
        Verify(root);
    }

    /// <summary>Verifies an existing root (API host): a missing file means the collector has not initialized it.</summary>
    public static void Verify(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"'{root}' has no {FileName}: start the collector (it creates it) or run schema/apply-schema.sh telemetry parquet \"{root}\".");

        Content? found;
        try { found = JsonSerializer.Deserialize<Content>(File.ReadAllText(path), Options); }
        catch (JsonException e) { throw new InvalidOperationException($"'{path}' is not a valid schema file: {e.Message}", e); }

        var expected = Expected();
        if (found is null || found.Component != expected.Component || found.Version != expected.Version)
            throw new InvalidOperationException(
                $"'{root}' holds schema {found?.Component} {found?.Version}, but this build needs {expected.Component} {expected.Version}. " +
                "There is no migration: use a new root.");
        if (found.TablesHash != expected.TablesHash)
            throw new InvalidOperationException(
                $"'{root}' was written under other table definitions (hash {found.TablesHash}, this build {expected.TablesHash}). " +
                "There is no migration: use a new root.");
    }
}
