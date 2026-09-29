using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using Keryhe.Telemetry.StressTests.Observers.Database;

namespace Keryhe.Telemetry.StressTests.Observers.Containers;

/// <summary>
/// The <c>docker stats</c> stream for one container, through the same Docker API Testcontainers uses: CPU (in
/// cores, so 1.5 means one and a half cores busy), memory, block I/O and network, roughly once a second.
/// </summary>
public sealed class ContainerStatsSampler : IAsyncDisposable
{
    private readonly string _containerId;
    private readonly List<ContainerStatsSample> _samples = [];
    private readonly CancellationTokenSource _stop = new();
    private DockerClient? _client;
    private Task? _stream;

    public ContainerStatsSampler(string containerId) => _containerId = containerId;

    public IReadOnlyList<ContainerStatsSample> Samples
    {
        get { lock (_samples) return _samples.ToList(); }
    }

    public void Start()
    {
        _client = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        _stream = Task.Run(async () =>
        {
            try
            {
                await _client.Containers.GetContainerStatsAsync(
                    _containerId, new ContainerStatsParameters { Stream = true },
                    new Progress<ContainerStatsResponse>(Record), _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or DockerContainerNotFoundException or IOException) { }
        });
    }

    private void Record(ContainerStatsResponse r)
    {
        var sample = ToSample(r);
        if (sample is null) return;
        lock (_samples) _samples.Add(sample);
    }

    /// <summary>Converts one Docker stats frame. Returns null for the first frame, which has no previous CPU reading to diff against.</summary>
    internal static ContainerStatsSample? ToSample(ContainerStatsResponse r)
    {
        double Cpu(CPUStats c) => (double)(c.CPUUsage?.TotalUsage ?? 0);
        var cpuDelta = Cpu(r.CPUStats) - Cpu(r.PreCPUStats);
        var systemDelta = (double)(r.CPUStats.SystemUsage ?? 0) - (double)(r.PreCPUStats.SystemUsage ?? 0);
        if (Cpu(r.PreCPUStats) == 0 || systemDelta <= 0) return null;
        var cpus = (r.CPUStats.OnlineCPUs ?? 0) > 0 ? (int)r.CPUStats.OnlineCPUs! : (r.CPUStats.CPUUsage?.PercpuUsage?.Count ?? 1);
        var cores = cpuDelta / systemDelta * cpus;

        // Like `docker stats`, page cache is not counted as used memory.
        long cache = 0;
        if (r.MemoryStats.Stats is { } stats)
        {
            if (stats.TryGetValue("inactive_file", out var inactive)) cache = (long)inactive;
            else if (stats.TryGetValue("cache", out var c)) cache = (long)c;
        }

        long Blk(string op) => r.BlkioStats?.IoServiceBytesRecursive?.Where(e => string.Equals(e.Op, op, StringComparison.OrdinalIgnoreCase)).Sum(e => (long)e.Value) ?? 0;

        return new ContainerStatsSample(
            DateTimeOffset.UtcNow, cores, (long)r.MemoryStats.Usage - cache, (long)r.MemoryStats.Limit,
            Blk("read"), Blk("write"),
            r.Networks?.Values.Sum(n => (long)n.RxBytes) ?? 0, r.Networks?.Values.Sum(n => (long)n.TxBytes) ?? 0);
    }

    public async Task<IReadOnlyList<ContainerStatsSample>> StopAsync()
    {
        _stop.Cancel();
        if (_stream is not null) await _stream;
        _client?.Dispose();
        _client = null;
        return Samples;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }
}
