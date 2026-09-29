using System.Text.Json;
using Keryhe.Telemetry.StressTests.Observers.Process;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Scenarios;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>
/// Builds <c>result.json</c>, <c>report.html</c> (and <c>comparison.html</c> for a matrix) from a results folder, using only what the scenarios
/// wrote to disk (<c>scenario.json</c>, the hosts' metric NDJSON, <c>run.json</c>). So a report can be rebuilt later, or after a change to the report,
/// without re-running anything.
/// </summary>
public static class ReportBuilder
{
    public const string RunMetadataFile = "run.json";

    public static async Task<RunResult> LoadAsync(string directory)
    {
        RunMetadata? metadata = null;
        var runFile = Path.Combine(directory, RunMetadataFile);
        if (File.Exists(runFile)) metadata = JsonSerializer.Deserialize<RunMetadata>(await File.ReadAllTextAsync(runFile), ResultJson.Options);

        var reports = new List<ScenarioReport>();
        foreach (var folder in Directory.GetDirectories(directory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, "scenario.json");
            if (!File.Exists(file)) continue;
            var scenario = JsonSerializer.Deserialize<ScenarioResult>(await File.ReadAllTextAsync(file), ResultJson.Options)
                           ?? throw new InvalidDataException($"'{file}' is empty.");
            var samples = new Dictionary<HostRole, IReadOnlyList<MetricSample>>();
            foreach (var host in scenario.Hosts.Where(h => h.MetricsFile is not null))
                samples[host.Role] = await ReadSamplesAsync(Path.Combine(folder, host.MetricsFile!));
            reports.Add(ScenarioAnalyzer.Analyze(Path.GetFileName(folder), scenario, samples));
        }
        return new RunResult(RunResult.CurrentSchemaVersion, metadata, reports);
    }

    private static async Task<IReadOnlyList<MetricSample>> ReadSamplesAsync(string path)
    {
        var samples = new List<MetricSample>();
        if (!File.Exists(path)) return samples;
        await foreach (var line in File.ReadLinesAsync(path))
            if (line.Length > 0 && JsonSerializer.Deserialize<MetricSample>(line, ResultJson.Options) is { } s) samples.Add(s);
        return samples;
    }

    /// <summary>Loads the folder and writes the three outputs into it (or <paramref name="outDirectory"/>). Returns the paths written.</summary>
    public static async Task<IReadOnlyList<string>> BuildAsync(string directory, string? outDirectory = null)
    {
        var run = await LoadAsync(directory);
        outDirectory ??= directory;
        Directory.CreateDirectory(outDirectory);

        var written = new List<string>();
        async Task Write(string name, string content)
        {
            var path = Path.Combine(outDirectory, name);
            await File.WriteAllTextAsync(path, content);
            written.Add(path);
        }
        await Write("result.json", JsonSerializer.Serialize(run, ResultJson.Compact));
        await Write("report.html", HtmlReportWriter.Write(run));
        if (run.Scenarios.Count > 1) await Write("comparison.html", ComparisonWriter.Write(run));
        return written;
    }
}
