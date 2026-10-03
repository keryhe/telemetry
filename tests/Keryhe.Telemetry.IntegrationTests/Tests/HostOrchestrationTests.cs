using Keryhe.Telemetry.StressTests.Observers.Process;
using Keryhe.Telemetry.StressTests.Orchestration;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Host orchestration's pure logic (stress-test plan, Phase 3): log scanning, metric decoding, environment wiring. Needs no host or database.</summary>
public class HostOrchestrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static HostLogSummary Scan(params string[] lines)
    {
        var scanner = new HostLogScanner();
        foreach (var line in lines) scanner.OnLine(line, T0);
        return scanner.Summarize();
    }

    [Fact]
    public void Retention_sweep_line_yields_rows_and_elapsed_ms_with_its_own_timestamp()
    {
        var log = Scan("2026-09-28T21:10:50.619Z info: Keryhe.Telemetry.Api.Retention.RetentionWorker[0] " +
                       "Retention sweep complete: 272 span rows, 224 data-point rows, 259 log rows removed in 23 ms.");
        var sweep = Assert.Single(log.RetentionSweeps);
        Assert.Equal((272, 224, 259, 23), (sweep.SpanRows, sweep.DataPointRows, sweep.LogRows, sweep.ElapsedMs));
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 21, 10, 50, 619, TimeSpan.Zero), sweep.At);
    }

    [Fact]
    public void Flush_retries_and_dropped_batches_are_told_apart()
    {
        var log = Scan(
            "2026-09-28T21:10:50.000Z warn: Keryhe.Telemetry.Core.Data.TelemetryIngestionWorker[0] Error flushing logs batch of 2000 (attempt 2/6) — retrying in 310ms",
            "2026-09-28T21:10:51.000Z fail: Keryhe.Telemetry.Core.Data.TelemetryIngestionWorker[0] Error flushing traces batch of 1500 after 6 attempts — batch dropped");
        var retry = Assert.Single(log.FlushRetries);
        Assert.Equal(("logs", 2000, 2), (retry.Signal, retry.Count, retry.Attempt));
        var dropped = Assert.Single(log.BatchesDropped);
        Assert.Equal(("traces", 1500), (dropped.Signal, dropped.Count));
        Assert.Equal((1, 1), (log.Warnings, log.Errors));
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SqlException: Transaction (Process ID 61) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Number=1205", "SqlServer 1205 (deadlock victim)")]
    [InlineData("Npgsql.PostgresException : 40P01: deadlock detected", "Postgres 40P01 (deadlock detected)")]
    [InlineData("MySqlConnector.MySqlException (0x80004005): Deadlock found when trying to get lock; try restarting transaction", "MySql 1213 (deadlock)")]
    [InlineData("MySqlConnector.MySqlException: Lock wait timeout exceeded; try restarting transaction", "MySql 1205 (lock wait timeout)")]
    public void Provider_deadlock_and_lock_wait_signatures_are_recognised(string line, string kind)
    {
        var error = Assert.Single(Scan(line).ProviderErrors);
        Assert.Equal(kind, error.Kind);
    }

    [Fact]
    public void Ordinary_lines_produce_no_events()
    {
        var log = Scan("2026-09-28T21:10:50.619Z info: Microsoft.Hosting.Lifetime[14] Now listening on: http://127.0.0.1:61215",
                       "   at Some.Stack.Frame()");
        Assert.Empty(log.ProviderErrors);
        Assert.Empty(log.FlushRetries);
        Assert.Equal((0, 0), (log.Warnings, log.Errors));
    }

    [Fact]
    public void Shutdown_deadline_line_reports_the_unpersisted_count()
    {
        var log = Scan("2026-09-28T21:10:50.619Z warn: Keryhe.Telemetry.Core.Data.TelemetryIngestionWorker[0] " +
                       "Shutdown deadline reached before the logs queue drained -- 12345 records were not persisted");
        var d = Assert.Single(log.ShutdownDeadlines);
        Assert.Equal(("logs", 12345L), (d.Signal, d.Unpersisted));
    }

    [Fact]
    public void Metric_tags_and_quantiles_parse()
    {
        var tags = ProcessMetricsListener.ParseTags("outcome=ok,signal=traces");
        Assert.Equal("ok", tags["outcome"]);
        Assert.Equal("traces", tags["signal"]);
        Assert.Empty(ProcessMetricsListener.ParseTags(""));

        var q = ProcessMetricsListener.ParseQuantiles("0.5=1.5;0.95=3;0.99=4.25");
        Assert.Equal(new[] { 1.5, 3, 4.25 }, new[] { q[0.5], q[0.95], q[0.99] });
    }

    [Fact]
    public void Metric_store_totals_counters_ignoring_NaN_and_filters_by_tag()
    {
        var store = new MetricStore();
        MetricSample Counter(double v, string signal, int sec) =>
            new(T0.AddSeconds(sec), "m", "records_flushed", "counter", new Dictionary<string, string> { ["signal"] = signal }, v);
        store.Add(Counter(10, "logs", 1));
        store.Add(Counter(5, "logs", 2));
        store.Add(Counter(double.NaN, "logs", 3));
        store.Add(Counter(99, "traces", 2));

        Assert.Equal(15, store.Total("records_flushed", "signal", "logs"));
        Assert.Equal(99, store.Total("records_flushed", "signal", "traces"));
        Assert.Equal(double.NaN, store.Latest("records_flushed", "signal", "logs")!.Value);
        Assert.Null(store.Latest("records_flushed", "signal", "metrics"));
    }

    private static HostLaunchOptions Options(HostTopology topology) =>
        new("PostgreSQL", "collector-cs", "api-cs", topology, Path.GetTempPath(), RetentionIntervalSeconds: 7);

    [Fact]
    public void Split_hosts_each_get_only_their_own_endpoint_and_connection_string()
    {
        var (collector, grpc, noApi) = HostLauncher.BuildEnvironment(Options(HostTopology.Split), HostRole.Collector);
        Assert.NotNull(grpc);
        Assert.Null(noApi);
        Assert.Contains("ConnectionStrings__Collector", collector.Keys);
        Assert.DoesNotContain("ConnectionStrings__Api", collector.Keys);
        Assert.Equal("Http2", collector["Kestrel__Endpoints__Https__Protocols"]);
        Assert.StartsWith("http://127.0.0.1:", collector["Kestrel__Endpoints__Https__Url"]);
        // The plaintext endpoint is intended: without this the collector's transport guard refuses to start.
        Assert.Equal("true", collector["Telemetry__Collector__AllowInsecureTransport"]);

        var (apiEnv, noGrpc, api) = HostLauncher.BuildEnvironment(Options(HostTopology.Split), HostRole.Api);
        Assert.Null(noGrpc);
        Assert.NotNull(api);
        Assert.Contains("ConnectionStrings__Api", apiEnv.Keys);
        Assert.DoesNotContain("ConnectionStrings__Collector", apiEnv.Keys);
    }

    [Fact]
    public void Free_ports_are_distinct()
    {
        var ports = PortFinder.GetFreePorts(6);
        Assert.Equal(6, ports.Distinct().Count());
        Assert.All(ports, p => Assert.InRange(p, 1024, 65535));
    }
}
