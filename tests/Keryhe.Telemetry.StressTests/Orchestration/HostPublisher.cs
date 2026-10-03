using System.Diagnostics;

namespace Keryhe.Telemetry.StressTests.Orchestration;

public enum HostRole { Collector, Api }

/// <summary>The published output directories of the hosts a run needs.</summary>
public sealed class PublishedHosts
{
    private readonly Dictionary<HostRole, string> _dirs = [];

    public string Directory(HostRole role) =>
        _dirs.TryGetValue(role, out var dir) ? dir : throw new InvalidOperationException($"{role} was not published.");

    internal void Add(HostRole role, string dir) => _dirs[role] = dir;

    public static string ProjectName(HostRole role) => role switch
    {
        HostRole.Collector => "Keryhe.Telemetry.Collector.Server",
        _ => "Keryhe.Telemetry.Api.Server"
    };

    public string DllPath(HostRole role) => Path.Combine(Directory(role), ProjectName(role) + ".dll");
}

/// <summary>
/// Publishes the hosts once per run (stress-test plan, Phase 3): <c>dotnet publish -c Release</c>
/// into the run's working directory. Publishing runs sequentially because the hosts share the UI
/// project's Angular build. It does not pass <c>-p:BuildSpa=false</c>: that skips the Angular build
/// AND stops the UI project adding its wwwroot to the package, which would leave the UI empty.
/// The default build is incremental (a stamp file skips npm once the SPA is current).
/// </summary>
public static class HostPublisher
{
    /// <summary>Where a published host carries the UI's entry page.</summary>
    public const string UiIndexRelativePath = "wwwroot/_content/Keryhe.Telemetry.Ui/index.html";

    public static async Task<PublishedHosts> PublishAsync(
        string repoRoot, string outDir, IEnumerable<HostRole> roles, CancellationToken ct = default, bool skipIfPresent = false)
    {
        var published = new PublishedHosts();
        System.IO.Directory.CreateDirectory(outDir);

        foreach (var role in roles.Distinct())
        {
            var project = PublishedHosts.ProjectName(role);
            var target = Path.Combine(outDir, project);
            var csproj = Path.Combine(repoRoot, "src", project, project + ".csproj");
            var logPath = Path.Combine(outDir, $"publish-{project}.log");

            if (skipIfPresent && File.Exists(Path.Combine(target, project + ".dll")))
            {
                published.Add(role, target);
                continue;
            }

            var exit = await RunDotnetAsync(["publish", csproj, "-c", "Release", "-o", target], logPath, ct);
            if (exit.Code != 0)
                throw new InvalidOperationException(
                    $"dotnet publish of {project} failed (exit {exit.Code}); see {logPath}\n{Tail(exit.Output)}");

            // A missing Node toolchain only makes the UI project warn and package an empty UI, so a
            // run would otherwise go on to time out every page in the browser tour.
            if (role != HostRole.Collector && !File.Exists(Path.Combine(target, UiIndexRelativePath)))
                throw new InvalidOperationException(
                    $"{project} published without the UI ({UiIndexRelativePath} is missing). " +
                    "Install Node.js so Keryhe.Telemetry.Ui can build src/telemetry-client, and do not pass -p:BuildSpa=false. " +
                    $"See {logPath}.");

            published.Add(role, target);
        }
        return published;
    }

    private static async Task<(int Code, string Output)> RunDotnetAsync(string[] args, string logPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await stdout + await stderr;
        await File.WriteAllTextAsync(logPath, output, ct);
        return (process.ExitCode, output);
    }

    private static string Tail(string text, int lines = 15) =>
        string.Join('\n', text.Split('\n').TakeLast(lines));
}
