using System.Globalization;
using System.Text.Json;

namespace Keryhe.Telemetry.Parquet;

/// <summary>The local-disk (or one machine's shared volume) <see cref="IParquetFileStore"/>.</summary>
public sealed class LocalParquetFileStore : IParquetFileStore
{
    internal const string TempFolder = ".tmp";

    public LocalParquetFileStore(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    private string DirectoryOf(ParquetPartition p) => Path.Combine(Root, p.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public string NewTempPath(ParquetPartition partition, string writerId, string name)
    {
        var dir = Path.Combine(DirectoryOf(partition), TempFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{writerId}.{Guid.NewGuid():N}.{name}");
    }

    public string PathOf(ParquetPartition partition, string name) => Path.Combine(DirectoryOf(partition), name);

    public Task CommitFileAsync(ParquetPartition partition, string tempPath, string finalName, CancellationToken ct)
    {
        File.Move(tempPath, PathOf(partition, finalName), overwrite: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListManifestNamesAsync(ParquetPartition partition, CancellationToken ct)
    {
        var dir = DirectoryOf(partition);
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>([]);
        var names = Directory.EnumerateFiles(dir, "_*.json").Select(Path.GetFileName).OfType<string>()
            .Where(n => n.StartsWith("_manifest.", StringComparison.Ordinal) || n.StartsWith("_compacted.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public async Task<ParquetManifest?> ReadManifestAsync(ParquetPartition partition, string manifestName, CancellationToken ct)
    {
        var path = PathOf(partition, manifestName);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<ParquetManifest>(stream, ParquetManifest.Json, ct);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task WriteManifestAsync(ParquetPartition partition, string manifestName, ParquetManifest manifest, CancellationToken ct)
    {
        var temp = await WriteManifestTempAsync(partition, manifestName, manifest, ct);
        File.Move(temp, PathOf(partition, manifestName), overwrite: true);
    }

    public async Task<bool> TryPublishManifestAsync(ParquetPartition partition, string manifestName, ParquetManifest manifest, CancellationToken ct)
    {
        var temp = await WriteManifestTempAsync(partition, manifestName, manifest, ct);
        try
        {
            File.Move(temp, PathOf(partition, manifestName), overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(PathOf(partition, manifestName)))
        {
            File.Delete(temp);
            return false;
        }
    }

    private async Task<string> WriteManifestTempAsync(ParquetPartition partition, string manifestName, ParquetManifest manifest, CancellationToken ct)
    {
        var temp = NewTempPath(partition, "manifest", manifestName);
        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, manifest, ParquetManifest.Json, ct);
        return temp;
    }

    public Task DeleteFileAsync(ParquetPartition partition, string name, CancellationToken ct)
    {
        File.Delete(PathOf(partition, name));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ParquetFileInfo>> ListFilesAsync(ParquetPartition partition, CancellationToken ct)
        => Task.FromResult(ListIn(DirectoryOf(partition), "*.parquet"));

    public Task<IReadOnlyList<ParquetFileInfo>> ListTempFilesAsync(ParquetPartition partition, CancellationToken ct)
        => Task.FromResult(ListIn(Path.Combine(DirectoryOf(partition), TempFolder), "*"));

    public Task DeleteTempFileAsync(ParquetPartition partition, string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(DirectoryOf(partition), TempFolder, name));
        return Task.CompletedTask;
    }

    private static IReadOnlyList<ParquetFileInfo> ListIn(string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        var list = new List<ParquetFileInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, pattern))
        {
            try
            {
                var info = new FileInfo(path);
                list.Add(new ParquetFileInfo(info.Name, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
            }
            catch (IOException) { /* deleted while listing */ }
        }
        return list;
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(Path.Combine(Root, relativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public async Task WriteTextAsync(string relativePath, string text, CancellationToken ct)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, text, ct);
        File.Move(temp, path, overwrite: true);
    }

    public IEnumerable<ParquetPartition> EnumeratePartitions(string table, long? tenantId, DateOnly from, DateOnly to)
    {
        var tableDir = Path.Combine(Root, table);
        if (!Directory.Exists(tableDir)) yield break;

        foreach (var tenantDir in Directory.EnumerateDirectories(tableDir, "tenant_id=*"))
        {
            if (!TryParse(tenantDir, "tenant_id=", out var tenant) || (tenantId is { } wanted && wanted != tenant)) continue;
            foreach (var dateDir in Directory.EnumerateDirectories(tenantDir, "date=*"))
            {
                var dateText = Path.GetFileName(dateDir)["date=".Length..];
                if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || date < from || date > to) continue;
                foreach (var hourDir in Directory.EnumerateDirectories(dateDir, "hour=*"))
                    if (TryParse(hourDir, "hour=", out var hour) && hour is >= 0 and < 24)
                        yield return new ParquetPartition(table, tenant, date, (int)hour);
            }
        }
    }

    public Task<bool> DeleteDayAsync(string table, long tenantId, DateOnly date, CancellationToken ct)
    {
        var dir = Path.Combine(Root, table, $"tenant_id={tenantId}", $"date={date:yyyy-MM-dd}");
        if (!Directory.Exists(dir)) return Task.FromResult(false);
        Directory.Delete(dir, recursive: true);
        return Task.FromResult(true);
    }

    public int DeleteTempFiles(string writerId)
    {
        var deleted = 0;
        if (!Directory.Exists(Root)) return 0;
        foreach (var temp in Directory.EnumerateDirectories(Root, TempFolder, SearchOption.AllDirectories))
            foreach (var file in Directory.EnumerateFiles(temp, writerId + ".*"))
            {
                try { File.Delete(file); deleted++; }
                catch (IOException) { /* in use or already gone */ }
            }
        return deleted;
    }

    private static bool TryParse(string directory, string prefix, out long value)
    {
        var name = Path.GetFileName(directory);
        value = 0;
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
