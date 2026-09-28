namespace Keryhe.Telemetry.StressTests.Orchestration;

public static class RepoLocator
{
    /// <summary>The repository root (the directory holding <c>Telemetry.sln</c>), searching upward from the binaries.</summary>
    public static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Telemetry.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Could not find Telemetry.sln above " + AppContext.BaseDirectory);
    }
}
