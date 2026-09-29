using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Configurations;
using Keryhe.Telemetry.TestInfrastructure.Containers;

namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary>Gathers what the numbers must be read against: git state, this machine, Docker's VM, and the images used.</summary>
public static class RunMetadataCollector
{
    public static async Task<RunMetadata> CollectAsync(string repoRoot, IEnumerable<string> providers, string commandLine)
    {
        var sha = await GitAsync(repoRoot, "rev-parse", "HEAD");
        var dirty = !string.IsNullOrWhiteSpace(await GitAsync(repoRoot, "status", "--porcelain", "--untracked-files=no"));

        int? dockerCpus = null;
        long? dockerMem = null;
        string? dockerVersion = null;
        try
        {
            // Docker Desktop runs containers in a VM whose CPU and memory allotment bounds every result, so it is recorded.
            using var client = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
            var info = await client.System.GetSystemInfoAsync();
            dockerCpus = (int)info.NCPU;
            dockerMem = info.MemTotal;
            dockerVersion = info.ServerVersion;
        }
        catch (Exception) { /* Docker unreachable: leave the fields empty rather than fail the report */ }

        var images = providers.Distinct().ToDictionary(p => p, p => ProviderContainerFactory.Create(p).ImageName);
        return new RunMetadata(DateTimeOffset.UtcNow, string.IsNullOrEmpty(sha) ? null : sha, dirty, RuntimeInformation.OSDescription, Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, RuntimeInformation.FrameworkDescription, dockerCpus, dockerMem, dockerVersion, images, commandLine);
    }

    private static async Task<string?> GitAsync(string repoRoot, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception) { return null; }
    }
}
