using System.Net;
using System.Net.Sockets;

namespace Keryhe.Telemetry.StressTests.Orchestration;

public static class PortFinder
{
    /// <summary>
    /// Asks the OS for <paramref name="count"/> distinct free loopback ports. Ports are released before
    /// return, so there is a small window in which another process could take one; the launcher's
    /// readiness check reports that as a host that failed to start.
    /// </summary>
    public static int[] GetFreePorts(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToList();
        try
        {
            foreach (var l in listeners) l.Start();
            return listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToArray();
        }
        finally
        {
            foreach (var l in listeners) l.Stop();
        }
    }
}
