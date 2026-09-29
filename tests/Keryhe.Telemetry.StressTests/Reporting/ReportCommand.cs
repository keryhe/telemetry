namespace Keryhe.Telemetry.StressTests.Reporting;

/// <summary><c>report --in &lt;dir&gt; [--out &lt;dir&gt;]</c>: rebuilds the report outputs from a results folder written by <c>run</c>.</summary>
public static class ReportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? input = null, output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--in" when i + 1 < args.Length: input = args[++i]; break;
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}\nusage: report --in <results dir> [--out <dir>]"); return 2;
            }
        }
        if (input is null || !Directory.Exists(input))
        {
            Console.Error.WriteLine("--in must name an existing results folder.");
            return 2;
        }
        var written = await ReportBuilder.BuildAsync(input, output);
        foreach (var path in written) Console.WriteLine($"Wrote {path}");
        return written.Count == 0 ? 1 : 0;
    }
}
