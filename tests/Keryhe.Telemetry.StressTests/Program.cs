using System.Text.Json;
using Keryhe.Telemetry.StressTests.Load;

// Phase 2 entry point: drives the OTLP load tool against an already-running host. The full CLI
// (--provider/--topology/--profile/--scenario, container and host orchestration) arrives in Phase 6.
//
//   load --target http://localhost:5117 --tenant <id>:<apiKey> [--tenant ...]
//        [--api http://localhost:5188] [--profile file.json] [--duration 60] [--marker-interval 5] [--out result.json]

if (args.Length > 0 && args[0] == "host-smoke")
    return await Keryhe.Telemetry.StressTests.Orchestration.HostSmokeCommand.RunAsync(args[1..]);

if (args.Length > 0 && args[0] == "playwright-install")
{
    // Playwright's own installer: downloads the Chromium build this Playwright version drives (once per machine).
    return Microsoft.Playwright.Program.Main(["install", "chromium"]);
}

if (args.Length == 0 || args[0] != "load")
{
    Console.Error.WriteLine("usage: host-smoke [--provider <p>] [--topology allinone|split] [--duration <s>] [--retention-interval <s>] [--browsers <n>] [--browser-warmup <s>] [--browser-export] [--browser-timeout <s>] [--out <dir>]\n       playwright-install\n       load --target <grpc url> --tenant <id>:<apiKey> [--tenant ...] [--api <url>] [--profile <file>] [--duration <s>] [--marker-interval <s>] [--out <file>]");
    return 2;
}

string? target = null, api = null, profilePath = null, outPath = null;
var duration = 60;
var markerInterval = 0;
var tenants = new List<LoadTenant>();
for (var i = 1; i < args.Length; i++)
{
    string Next() => args[++i];
    switch (args[i])
    {
        case "--target": target = Next(); break;
        case "--api": api = Next(); break;
        case "--profile": profilePath = Next(); break;
        case "--out": outPath = Next(); break;
        case "--duration": duration = int.Parse(Next()); break;
        case "--marker-interval": markerInterval = int.Parse(Next()); break;
        case "--tenant":
            var parts = Next().Split(':', 2);
            tenants.Add(new LoadTenant(long.Parse(parts[0]), $"tenant-{parts[0]}", parts[1]));
            break;
        default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
    }
}
if (target is null || tenants.Count == 0) { Console.Error.WriteLine("--target and at least one --tenant are required"); return 2; }
if (markerInterval > 0 && api is null) { Console.Error.WriteLine("--marker-interval needs --api"); return 2; }

var profile = profilePath is null ? new LoadProfile() : LoadProfile.Load(profilePath);
await using var generator = new OtlpLoadGenerator(profile, tenants, new Uri(target));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = api is null ? null : new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/") };
MarkerProbe? probe = http is null || markerInterval <= 0 ? null
    : new MarkerProbe(generator.Exporter, generator.Topology, 0, http,
        TimeSpan.FromSeconds(markerInterval), TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(60));

Console.WriteLine($"Sending for {duration}s to {target} as {tenants.Count} tenant(s)...");
var run = generator.RunAsync(cts.Token);
var markers = probe?.RunAsync(cts.Token) ?? Task.CompletedTask;
await Task.WhenAll(run, markers);

var snapshot = generator.Snapshot();
foreach (var s in snapshot.Signals)
    Console.WriteLine($"{s.Signal,-8} offered {s.OfferedPerSecond,8:F0}/s  acked {s.AckedPerSecond,8:F0}/s  " +
                      $"ok {s.ExportsOk} rejected {s.ExportsRejected} failed {s.ExportsFailed} not-sent {s.NotSentRecords}  " +
                      $"latency p50 {s.Latency.P50Ms:F1} p95 {s.Latency.P95Ms:F1} p99 {s.Latency.P99Ms:F1} max {s.Latency.MaxMs:F1} ms");
if (probe is not null)
{
    var results = probe.Results;
    Console.WriteLine($"markers: {results.Count}, log lag ms [{string.Join(", ", results.Select(r => r.LogLagMs?.ToString("F0") ?? "timeout"))}]");
    Console.WriteLine($"         trace lag ms [{string.Join(", ", results.Select(r => r.TraceLagMs?.ToString("F0") ?? "timeout"))}]");
}

if (outPath is not null)
{
    var json = JsonSerializer.Serialize(new { snapshot, markers = probe?.Results }, new JsonSerializerOptions(LoadProfile.JsonOptions) { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    await File.WriteAllTextAsync(outPath, json);
    Console.WriteLine($"wrote {outPath}");
}
return 0;
