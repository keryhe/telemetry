using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keryhe.Telemetry.Parquet;

/// <summary>
/// A collector's list of the files it committed to one partition (<c>_manifest.&lt;writer&gt;.json</c>), replaced by atomic
/// rename. A file is visible to a reader only once it is listed. Entries are in commit order; <see cref="NextSequence"/>
/// is persisted so a sequence number is never reused after the compactor prunes entries (phase 4).
/// </summary>
public sealed class ParquetManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Writer { get; set; } = "";
    public long NextSequence { get; set; } = 1;
    public List<ParquetManifestFile> Files { get; set; } = [];

    /// <summary>
    /// Compacted generations only (<c>_compacted.&lt;gen&gt;.json</c>, phase 4): the generation number, when it was published
    /// (Unix milliseconds, the clock a superseded file's grace period counts from), and per writer the highest sequence
    /// folded in. <see cref="Files"/> of a compacted manifest is its whole file set. A reader sees this generation's files
    /// plus each writer's files with a higher sequence.
    /// </summary>
    public int Generation { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public Dictionary<string, long> Through { get; set; } = [];

    /// <summary>
    /// A day generation only (the <c>_day</c> folder): per hour the day has absorbed, per writer the highest sequence of that
    /// hour's writer files folded in. An hour listed here is read from the day's files plus its writer files above these
    /// values, and its own compacted generation is ignored.
    /// </summary>
    public Dictionary<int, Dictionary<string, long>> HourThrough { get; set; } = [];

    public static string GenerationFileName(int generation) => $"_compacted.{generation:D6}.json";

    public static bool TryParseGeneration(string manifestName, out int generation)
    {
        generation = 0;
        const string prefix = "_compacted.";
        return manifestName.StartsWith(prefix, StringComparison.Ordinal) && manifestName.EndsWith(".json", StringComparison.Ordinal)
            && int.TryParse(manifestName.AsSpan(prefix.Length, manifestName.Length - prefix.Length - ".json".Length), out generation);
    }

    public static string FileNameFor(string writerId) => $"_manifest.{writerId}.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };
}

/// <summary>
/// One committed file: its <see cref="Sequence"/> (per writer per partition, in commit order), the batch token it was
/// written under (README D6), and its row count, size and the minimum and maximum of the table's partition time
/// column, so the compactor and readers can use counts and bounds without opening the file.
/// </summary>
public sealed class ParquetManifestFile
{
    public string Name { get; set; } = "";
    public long Sequence { get; set; }
    public string Token { get; set; } = "";
    public long Rows { get; set; }
    public long Bytes { get; set; }
    public long MinTime { get; set; }
    public long MaxTime { get; set; }

    /// <summary>When the file was committed (Unix milliseconds); the compactor's age rule reads it. 0 when unknown.</summary>
    public long CommittedAtUnixMs { get; set; }
}
