using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Orchestration;
using Keryhe.Telemetry.StressTests.Scenarios;
using Keryhe.Telemetry.TestInfrastructure.Containers;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Scenario logic that needs no database or host (stress-test plan, Phase 6): profiles, the CLI matrix, ramp stop rules, quiescence and measuring windows.</summary>
public class ScenarioTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("smoke", 60, 300, 1, 500)]
    [InlineData("standard", 300, 1800, 3, 5000)]
    [InlineData("soak", 900, 14400, 3, 5000)]
    public void Builtin_fixed_profiles_match_the_plans_table(string name, int warmup, int measured, int browsers, double rate)
    {
        var p = ScenarioProfile.Resolve(name);
        Assert.False(p.IsRamp);
        Assert.Equal((name, warmup, measured, browsers), (p.Name, p.WarmupSeconds, p.MeasuredSeconds, p.Browsers.Users));
        Assert.Equal((rate, rate, rate), (p.Load.Traces.SpansPerSecond, p.Load.Logs.RecordsPerSecond, p.Load.Metrics.DataPointsPerSecond));
        // No retention contention by default: no backdated records, and a daily sweep (only the one at host start, before any
        // data). A run that measures retention opts in with --retention-interval and a profile that sets backdatedFraction.
        Assert.Equal(0, p.Load.Time.BackdatedFraction);
        Assert.Equal(86400, p.RetentionIntervalSeconds);
        // Decision 17: default container cap.
        Assert.Equal((4d, 8d), (p.ContainerCpus, p.ContainerMemoryGb));
    }

    [Fact]
    public void Ramp_profile_is_a_ramp_with_criteria()
    {
        var p = ScenarioProfile.Resolve("ramp");
        Assert.True(p.IsRamp);
        Assert.Equal(60, p.Ramp!.StepSeconds);
        Assert.Equal(0, p.Ramp.Criteria.MaxDroppedRecords);
    }

    [Fact]
    public void A_profile_file_only_lists_what_it_overrides()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stress-profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "// comment\n{ \"measuredSeconds\": 42, \"browsers\": { \"users\": 0 }, \"load\": { \"seed\": 9 } }");
        try
        {
            var p = ScenarioProfile.Resolve(path);
            Assert.Equal((42, 0, 9), (p.MeasuredSeconds, p.Browsers.Users, p.Load.Seed));
            Assert.Equal(60, p.WarmupSeconds); // default
            Assert.StartsWith("stress-profile-", p.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_profiles_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => new ScenarioProfile { Browsers = { Users = 9 } }.Validate());
        Assert.Throws<InvalidDataException>(() => new ScenarioProfile { Tenants = 0 }.Validate());
        Assert.Throws<InvalidDataException>(() => new ScenarioProfile { Ramp = new RampProfile { StepSeconds = 0 } }.Validate());
        Assert.Throws<FileNotFoundException>(() => ScenarioProfile.Resolve("no-such-profile"));
    }

    [Fact]
    public void Matrix_expands_provider_profile_and_all_means_every_value()
    {
        var all = RunCommand.BuildMatrix("all", "all", "smoke", "fixed", false, null);
        Assert.Equal(ProviderContainerFactory.ProviderNames.Count, all.Count);
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        Assert.Contains(all, s => s.Id == "clickhouse-split-smoke");
        Assert.Contains(all, s => s.Id == "parquet-split-smoke");

        var one = Assert.Single(RunCommand.BuildMatrix("sqlserver", "split", "smoke", "fixed", false, 3));
        Assert.Equal(("SqlServer", HostTopology.Split, 3), (one.Provider, one.Topology, one.Profile.Browsers.Users));
    }

    [Fact]
    public void Scenario_flag_picks_the_default_profile_and_rejects_a_mismatch()
    {
        var ramp = Assert.Single(RunCommand.BuildMatrix("PostgreSQL", "split", "", "ramp", true, null));
        Assert.True(ramp.Profile.IsRamp);
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("PostgreSQL", "split", "smoke", "ramp", true, null));
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("Oracle", "split", "smoke", "fixed", false, null));
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("PostgreSQL", "sideways", "smoke", "fixed", false, null));

        // profile=all with --scenario fixed leaves the ramp out; without --scenario it is included.
        Assert.DoesNotContain(RunCommand.BuildMatrix("PostgreSQL", "split", "all", "fixed", true, null), s => s.Profile.IsRamp);
        Assert.Contains(RunCommand.BuildMatrix("PostgreSQL", "split", "all", "fixed", false, null), s => s.Profile.IsRamp);
    }

    [Fact]
    public void Control_plane_option_defaults_per_provider_and_tags_only_a_departure_from_the_default()
    {
        // No option: nothing recorded on the profile (the runner records what the container actually used), no tag.
        var parquet = RunCommand.BuildMatrix("Parquet", "split", "smoke", "fixed", false, null).Single();
        Assert.Null(parquet.Profile.ControlPlane);
        Assert.Equal("smoke", parquet.Profile.Name);

        // Parquet's default is SQLite, so asking for it is not a departure; PostgreSQL is.
        Assert.Equal("smoke", RunCommand.BuildMatrix("Parquet", "split", "smoke", "fixed", false, null, controlPlane: "sqlite").Single().Profile.Name);
        var parquetPostgres = RunCommand.BuildMatrix("Parquet", "split", "smoke", "fixed", false, null, controlPlane: "PostgreSQL").Single();
        Assert.Equal(("smoke-cppostgresql", "postgresql"), (parquetPostgres.Profile.Name, parquetPostgres.Profile.ControlPlane));
        Assert.Equal("parquet-split-smoke-cppostgresql", parquetPostgres.Id);

        // ClickHouse's default is PostgreSQL; SQLite is the departure. The relational providers keep their own database.
        Assert.Equal("smoke", RunCommand.BuildMatrix("ClickHouse", "split", "smoke", "fixed", false, null, controlPlane: "postgresql").Single().Profile.Name);
        Assert.Equal("smoke-cpsqlite", RunCommand.BuildMatrix("ClickHouse", "split", "smoke", "fixed", false, null, controlPlane: "sqlite").Single().Profile.Name);
        Assert.Equal("smoke-cpsqlite", RunCommand.BuildMatrix("SqlServer", "split", "smoke", "fixed", false, null, controlPlane: "sqlite").Single().Profile.Name);
        Assert.Equal("smoke", RunCommand.BuildMatrix("PostgreSQL", "split", "smoke", "fixed", false, null, controlPlane: "postgresql").Single().Profile.Name);

        // One option over a matrix applies provider by provider, and an unknown value or one that cannot apply is refused up front.
        var all = RunCommand.BuildMatrix("all", "split", "smoke", "fixed", false, null, controlPlane: "sqlite");
        Assert.Equal(["smoke-cpsqlite", "smoke-cpsqlite", "smoke-cpsqlite", "smoke-cpsqlite", "smoke"],
            all.OrderBy(spec => Array.IndexOf(ProviderContainerFactory.ProviderNames.ToArray(), spec.Provider)).Select(spec => spec.Profile.Name));
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("Parquet", "split", "smoke", "fixed", false, null, controlPlane: "mongo"));
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("SqlServer", "split", "smoke", "fixed", false, null, controlPlane: "postgresql"));
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("all", "split", "smoke", "fixed", false, null, controlPlane: "postgresql"));
    }

    [Fact]
    public void The_container_factory_puts_the_control_plane_where_it_was_asked_to()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keryhe-factory-test");

        // Parquet: a SQLite file by default, in the directory it was given.
        var parquet = ProviderContainerFactory.Create("Parquet", null, directory);
        Assert.Equal("Sqlite", parquet.ControlPlaneProviderName);
        Assert.Equal($"Data Source={Path.Combine(directory, "control-plane.db")}", parquet.ControlPlaneConnectionString);
        Assert.Equal(string.Empty, parquet.ContainerId);
        Assert.Equal("PostgreSQL", ProviderContainerFactory.Create("Parquet", "postgresql").ControlPlaneProviderName);

        // Any other provider can move its control plane to a SQLite file; without a directory to keep it in that is an error.
        foreach (var provider in new[] { "PostgreSQL", "SqlServer", "MySql", "ClickHouse" })
        {
            var moved = ProviderContainerFactory.Create(provider, "sqlite", directory);
            Assert.Equal("Sqlite", moved.ControlPlaneProviderName);
            Assert.True(Path.IsPathRooted(moved.ControlPlaneConnectionString["Data Source=".Length..]), moved.ControlPlaneConnectionString);
            Assert.Throws<ArgumentException>(() => ProviderContainerFactory.Create(provider, "sqlite"));
        }
        Assert.Equal("PostgreSQL", ProviderContainerFactory.Create("ClickHouse").ControlPlaneProviderName);
    }

    [Fact]
    public void The_hosts_get_an_absolute_sqlite_control_plane_path_and_the_collector_only_checks_it()
    {
        var file = Path.Combine(Path.GetTempPath(), "keryhe-host-env", "control-plane.db");
        var options = new HostLaunchOptions("Parquet", "/tmp/parquet-root", "/tmp/parquet-root", "Sqlite", $"Data Source={file}", HostTopology.Split, Path.GetTempPath(), RetentionIntervalSeconds: 7);

        var (collector, _, _) = HostLauncher.BuildEnvironment(options, HostRole.Collector);
        var (api, _, _) = HostLauncher.BuildEnvironment(options, HostRole.Api);

        Assert.Equal("Sqlite", collector["Telemetry__Collector__Provider__ControlPlane"]);
        Assert.Equal("Sqlite", api["Telemetry__Api__Provider__ControlPlane"]);
        foreach (var env in new[] { collector, api })
        {
            var connectionString = env["ConnectionStrings__ControlPlane"];
            Assert.StartsWith("Data Source=", connectionString);
            Assert.True(Path.IsPathRooted(connectionString["Data Source=".Length..]), connectionString);
            // Nothing tells the API to skip creating it (the file exists already, made by the harness through the same library call).
            Assert.DoesNotContain("Telemetry__ControlPlane__Sqlite__ApplySchema", env.Keys);
        }
    }

    [Fact]
    public void Overrides_do_not_leak_between_scenarios()
    {
        var specs = RunCommand.BuildMatrix("all", "split", "smoke", "fixed", false, 2);
        specs[0].Profile.Load.Traces.SpansPerSecond = 1;
        Assert.All(specs.Skip(1), s => Assert.Equal(500, s.Profile.Load.Traces.SpansPerSecond));
    }

    private static WindowSummary Window(double p99Ms = 10, long ok = 100, long failed = 0) =>
        new("traces", 60, 6000, 6000, 0, 0, 0, ok, 0, failed, new LatencySummary(ok, 5, 5, 8, p99Ms, p99Ms));

    private static StepMeasurements Step(WindowSummary? window = null, double dropped = 0, double gate = 0, double?[]? traceLags = null, double?[]? logLags = null) =>
        new([window ?? Window()], dropped, gate, traceLags ?? [], logLags ?? []);

    private static readonly RampCriteria Criteria = new();

    [Fact]
    public void A_healthy_step_trips_nothing()
    {
        Assert.Empty(RampEvaluator.Tripped(Step(traceLags: [100, 110, 90, 105, 100, 95]), Criteria));
    }

    [Fact]
    public void Each_criterion_trips_on_its_own_and_only_past_its_limit()
    {
        Assert.Equal([RampEvaluator.Dropped], RampEvaluator.Tripped(Step(dropped: 1), Criteria));
        Assert.Empty(RampEvaluator.Tripped(Step(dropped: 0), Criteria));

        Assert.Equal([RampEvaluator.GateWait], RampEvaluator.Tripped(Step(gate: 251), Criteria));
        Assert.Empty(RampEvaluator.Tripped(Step(gate: 250), Criteria));

        Assert.Equal([RampEvaluator.ExportP99], RampEvaluator.Tripped(Step(Window(p99Ms: 5001)), Criteria));
        Assert.Empty(RampEvaluator.Tripped(Step(Window(p99Ms: 5000)), Criteria));

        Assert.Equal([RampEvaluator.ErrorRate], RampEvaluator.Tripped(Step(Window(ok: 90, failed: 10)), Criteria));
        Assert.Empty(RampEvaluator.Tripped(Step(Window(ok: 100, failed: 0)), Criteria));
    }

    [Fact]
    public void Throttled_rate_trips_past_its_limit_and_is_off_at_zero()
    {
        static WindowSummary Throttle(long attempts, long ok = 100) =>
            new("traces", 60, 6000, 6000, 0, 0, 0, ok, 0, 0, new LatencySummary(ok, 5, 5, 8, 10, 10), ThrottledAttempts: attempts);

        Assert.Equal([RampEvaluator.Throttled], RampEvaluator.Tripped(Step(Throttle(attempts: 10)), Criteria));   // 10 of 110 attempts: 9%
        Assert.Empty(RampEvaluator.Tripped(Step(Throttle(attempts: 4)), Criteria));                               // 4 of 104: under the 5% default
        Assert.Empty(RampEvaluator.Tripped(Step(Throttle(attempts: 50)), new RampCriteria { MaxThrottledRatePercent = 0 }));
        // A throttled attempt that was retried and accepted is not a server error.
        Assert.DoesNotContain(RampEvaluator.ErrorRate, RampEvaluator.Tripped(Step(Throttle(attempts: 50)), Criteria));
    }

    [Fact]
    public void The_exporter_reads_the_retry_delay_from_the_status_details_trailer()
    {
        var status = new Google.Rpc.Status { Code = (int)Grpc.Core.StatusCode.Unavailable, Message = "full" };
        status.Details.Add(Google.Protobuf.WellKnownTypes.Any.Pack(new Google.Rpc.RetryInfo { RetryDelay = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromMilliseconds(1500)) }));
        var withInfo = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "full"),
            new Grpc.Core.Metadata { { "grpc-status-details-bin", Google.Protobuf.MessageExtensions.ToByteArray(status) } });
        var without = new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "shutting down"));

        Assert.Equal(TimeSpan.FromMilliseconds(1500), Keryhe.Telemetry.StressTests.Load.OtlpExporter.ThrottleDelay(withInfo));
        Assert.Null(Keryhe.Telemetry.StressTests.Load.OtlpExporter.ThrottleDelay(without));   // a plain UNAVAILABLE is a failure, not throttling
    }

    [Fact]
    public void Lag_growth_needs_a_climb_not_a_high_plateau_and_a_missing_probe_counts_as_growth()
    {
        var steady = new double?[] { 8000, 8100, 7900, 8000, 8050, 7950 };
        var climbing = new double?[] { 1000, 1200, 3000, 6000, 9000, 12000 };
        var tinyClimb = new double?[] { 50, 60, 100, 120, 150, 200 };
        var lost = new double?[] { 100, 100, 100, 100, 100, null };

        Assert.False(RampEvaluator.LagGrows(steady, Criteria));
        Assert.True(RampEvaluator.LagGrows(climbing, Criteria));
        Assert.False(RampEvaluator.LagGrows(tinyClimb, Criteria)); // 4x, but under the 3s floor
        Assert.True(RampEvaluator.LagGrows(lost, Criteria));
        Assert.False(RampEvaluator.LagGrows([1000, 9000, 20000], Criteria)); // too few probes to judge

        // Its last third also averages over MaxLagSeconds (10 s), so the absolute criterion trips alongside.
        Assert.Equal([RampEvaluator.LagGrowth, RampEvaluator.LagAbsolute], RampEvaluator.Tripped(Step(logLags: climbing), Criteria));
    }

    [Fact]
    public void Log_lag_is_judged_as_measured()
    {
        // The log probe reads an unpinned list, so what it measures is the real lag: nothing is subtracted before the criteria apply.
        var steady = new double?[] { 150, 200, 250, 200, 300, 250 };
        Assert.Empty(RampEvaluator.Tripped(Step(logLags: steady), Criteria));

        var high = new double?[] { 14000, 14500, 15000, 15000, 15000, 16000 };
        Assert.Equal([RampEvaluator.LagAbsolute], RampEvaluator.Tripped(Step(logLags: high), Criteria));
    }

    [Fact]
    public void Absolute_lag_trips_on_a_high_plateau_and_can_be_disabled()
    {
        var plateau = new double?[] { 12000, 12000, 12000, 12000, 12000, 12000 };
        Assert.False(RampEvaluator.LagGrows(plateau, Criteria));
        Assert.True(RampEvaluator.LagTooHigh(plateau, Criteria));
        Assert.Equal([RampEvaluator.LagAbsolute], RampEvaluator.Tripped(Step(traceLags: plateau), Criteria));
        Assert.False(RampEvaluator.LagTooHigh(plateau, new RampCriteria { MaxLagSeconds = 0 }));
        Assert.False(RampEvaluator.LagTooHigh([20000, 20000, 20000], Criteria)); // too few probes to judge
    }

    [Fact]
    public void Probes_still_polling_at_step_end_count_once_they_are_already_over_the_limit()
    {
        var start = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(60);
        MarkerResult[] done = [new(1, start.AddSeconds(1), 5200, 150, null), new(2, start.AddSeconds(6), 5300, 160, null)];
        PendingMarker[] pending =
        [
            new(3, start.AddSeconds(40), LogPending: true, null, TracePending: false, 170), // 20 s old: log counted
            new(4, start.AddSeconds(48), LogPending: true, null, TracePending: true, null), // 12 s old: trace and log counted
            new(5, start.AddSeconds(58), LogPending: true, null, TracePending: true, null), // 2 s old: says nothing yet
            new(6, start.AddSeconds(70), LogPending: true, null, TracePending: true, null), // after the step
        ];

        var (trace, log) = ScenarioRunner.StepLags(done, pending, start, end, Criteria);

        Assert.Equal([150.0, 160, 170, 12000], trace.Select(l => l!.Value));
        Assert.Equal([5200.0, 5300, 20000, 12000], log.Select(l => l!.Value));
        var (traceOff, _) = ScenarioRunner.StepLags(done, pending, start, end, new RampCriteria { MaxLagSeconds = 0 });
        Assert.Equal(2, traceOff.Count);
    }

    [Fact]
    public void Retention_interval_override_accepts_presets_or_seconds_and_tags_the_scenario()
    {
        var realistic = RunCommand.BuildMatrix("PostgreSQL", "split", "ramp", "ramp", true, null, "realistic").Single();
        Assert.Equal(ScenarioProfile.RealisticRetentionIntervalSeconds, realistic.Profile.RetentionIntervalSeconds);
        Assert.Equal("postgresql-split-ramp-ret3600", realistic.Id);

        var seconds = RunCommand.BuildMatrix("PostgreSQL", "split", "smoke", "fixed", false, null, "120").Single();
        Assert.Equal(120, seconds.Profile.RetentionIntervalSeconds);
        Assert.Equal(86400, RunCommand.BuildMatrix("PostgreSQL", "split", "smoke", "fixed", false, null).Single().Profile.RetentionIntervalSeconds);
        Assert.Throws<ArgumentException>(() => RunCommand.BuildMatrix("PostgreSQL", "split", "smoke", "fixed", false, null, "hourly"));
    }

    [Fact]
    public void Quiescence_needs_an_empty_queue_and_a_stable_flush_count_for_the_whole_period()
    {
        var tracker = new QuiescenceTracker(TimeSpan.FromSeconds(10));
        Assert.False(tracker.Update(T0, resident: 500, flushedTotal: 100));
        Assert.False(tracker.Update(T0.AddSeconds(1), resident: 0, flushedTotal: 600));       // drained, but flushed just moved
        Assert.False(tracker.Update(T0.AddSeconds(5), resident: 0, flushedTotal: 600));        // quiet for 4s
        Assert.False(tracker.Update(T0.AddSeconds(8), resident: 5, flushedTotal: 600));        // records arrived again: restart
        Assert.False(tracker.Update(T0.AddSeconds(9), resident: 0, flushedTotal: 605));        // flushed moved again
        Assert.False(tracker.Update(T0.AddSeconds(10), resident: 0, flushedTotal: 605));       // quiet starts here
        Assert.False(tracker.Update(T0.AddSeconds(19), resident: 0, flushedTotal: 605));       // 9s quiet
        Assert.True(tracker.Update(T0.AddSeconds(20), resident: 0, flushedTotal: 605));        // 10s quiet
    }

    [Fact]
    public void Quiescence_without_a_resident_gauge_still_waits_for_flushing_to_stop()
    {
        var tracker = new QuiescenceTracker(TimeSpan.FromSeconds(3));
        Assert.False(tracker.Update(T0, resident: null, flushedTotal: 10));
        Assert.False(tracker.Update(T0.AddSeconds(2), resident: null, flushedTotal: 10));
        Assert.True(tracker.Update(T0.AddSeconds(4), resident: null, flushedTotal: 10));
    }

    [Fact]
    public void Window_counts_only_what_happens_inside_it_and_error_rate_ignores_partial_rejections()
    {
        var stats = new SignalStats("logs");
        stats.Start();
        stats.Offered(100, TimeSpan.Zero);
        stats.Completed(100, 10, ExportOutcome.Ok, "OK");   // before the window: not counted in it

        stats.BeginWindow();
        stats.Offered(100, TimeSpan.Zero);
        stats.Completed(100, 20, ExportOutcome.Ok, "OK");
        stats.Offered(100, TimeSpan.Zero);
        stats.Completed(100, 30, ExportOutcome.Rejected, "OK");
        stats.Offered(100, TimeSpan.Zero);
        stats.Completed(100, 40, ExportOutcome.Failed, "UNAVAILABLE");
        stats.NotSent(50);
        var w = stats.EndWindow();

        Assert.Equal((350L, 100L, 100L, 100L, 50L), (w.OfferedRecords, w.AckedRecords, w.RejectedRecords, w.FailedRecords, w.NotSentRecords));
        Assert.Equal(3, w.Latency.Count);
        Assert.Equal(100.0 / 3, w.ErrorRatePercent, 6);
    }

    [Fact]
    public void Window_metrics_helper_filters_by_time_and_tag()
    {
        var store = new StressTests.Observers.Process.MetricStore();
        void Add(int sec, string signal, double v) => store.Add(new(T0.AddSeconds(sec), "m", "records_dropped", "counter", new Dictionary<string, string> { ["signal"] = signal }, v));
        Add(1, "logs", 5); Add(5, "logs", 7); Add(5, "traces", 100); Add(9, "logs", 11);
        var inWindow = store.Window("records_dropped", T0.AddSeconds(2), T0.AddSeconds(8), "signal", "logs");
        Assert.Equal(7, Assert.Single(inWindow).Value);
    }
}
