using System.Text.RegularExpressions;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// A collector's id on a root (phase 1 decision 6) and the exclusive lock that keeps two live collectors from sharing
/// one. The lock is a held <c>_writers/&lt;id&gt;</c> file (<see cref="FileShare.None"/>). With no configured id the
/// machine name plus a random suffix is saved there on first start; a restart takes the first saved id of this machine
/// that is not locked, so it keeps its manifests, and a second collector on the same machine gets its own.
/// </summary>
public sealed partial class ParquetWriterIdentity : IDisposable
{
    private readonly FileStream _lock;

    private ParquetWriterIdentity(string id, FileStream stream)
    {
        Id = id;
        _lock = stream;
    }

    public string Id { get; }

    public static bool IsValidId(string id) => ValidId().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidId();

    public static ParquetWriterIdentity Acquire(string root, string? configuredId)
    {
        var dir = Path.Combine(root, "_writers");
        Directory.CreateDirectory(dir);

        if (!string.IsNullOrEmpty(configuredId))
        {
            return TryLock(dir, configuredId)
                ?? throw new InvalidOperationException(
                    $"Writer id '{configuredId}' is already in use by another running collector on '{root}'. " +
                    $"Two live collectors must not share a {ParquetOptions.SectionName}:WriterId; give each its own or leave it unset.");
        }

        var machine = Sanitize(Environment.MachineName);
        foreach (var path in Directory.EnumerateFiles(dir, machine + "-*").Order(StringComparer.Ordinal))
            if (TryLock(dir, Path.GetFileName(path)) is { } reused) return reused;

        return TryLock(dir, $"{machine}-{Guid.NewGuid().ToString("N")[..8]}")
            ?? throw new InvalidOperationException($"Could not create a writer id under '{dir}'.");
    }

    private static ParquetWriterIdentity? TryLock(string dir, string id)
    {
        try
        {
            var stream = new FileStream(Path.Combine(dir, id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new ParquetWriterIdentity(id, stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return clean.Length is 0 ? "collector" : clean[..Math.Min(clean.Length, 40)];
    }

    public void Dispose() => _lock.Dispose();
}
