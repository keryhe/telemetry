using Keryhe.Telemetry.StressTests.Load;
using Keryhe.Telemetry.StressTests.Observers.Database;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>The OTLP load tool's own logic (stress-test plan, Phase 2): shaping, ledger, scheduler. Needs no server or database.</summary>
public class OtlpLoadToolTests
{
    private static readonly LoadTenant[] Tenants =
        [new(1, "a", "key-a"), new(2, "b", "key-b")];

    private static LoadProfile Shaped() => new()
    {
        Seed = 42,
        TenantWeights = [3, 1],
        Transport = new TransportLoad { RecordsPerExport = 200 },
        Time = new TimeShaping { LateArrivalFraction = 0.2, BackdatedFraction = 0.1, OrphanFraction = 0.3, RedeliveryFraction = 0.5 }
    };

    private static (TraceShaper T, LogShaper L, MetricShaper M) Shapers(LoadProfile p)
    {
        var topology = new Topology(Tenants, p);
        return (new TraceShaper(p, topology, new Random(1)), new LogShaper(p, topology, new Random(2)), new MetricShaper(p, topology, new Random(3)));
    }

    [Fact]
    public void Ledger_entries_match_the_records_actually_in_each_request()
    {
        var (traces, logs, metrics) = Shapers(Shaped());
        for (var i = 0; i < 20; i++)
        {
            var t = traces.Next();
            // The rollup entries (plans/summary-rollups.md) are a second ledger of the same records, so they are summed apart.
            var signalTables = t.Entries.Where(e => !CountedTable.IsRollup(e.Table));
            Assert.Equal(t.Request.ResourceSpans.Sum(r => r.ScopeSpans.Sum(s => s.Spans.Count)), signalTables.Sum(e => e.Rows));
            Assert.Equal(t.Records, signalTables.Sum(e => e.Rows));
            var inbound = t.Request.ResourceSpans.SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans)
                .Count(s => s.Kind is OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Server or OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Consumer);
            Assert.True(inbound >= t.Entries.Where(e => e.Table == RollupTables.Request).Sum(e => e.Rows)); // current-age inbound only: backdated ones are not ledgered there

            var l = logs.Next();
            Assert.Equal(l.Request.ResourceLogs.Sum(r => r.ScopeLogs.Sum(s => s.LogRecords.Count)), l.Entries.Where(e => !CountedTable.IsRollup(e.Table)).Sum(e => e.Rows));
            Assert.Equal(l.Entries.Where(e => e.Table == "log_records" && e.Age == RecordAge.Current).Sum(e => e.Rows),
                l.Entries.Where(e => e.Table == RollupTables.Log).Sum(e => e.Rows));

            var m = metrics.Next();
            var points = m.Request.ResourceMetrics.SelectMany(r => r.ScopeMetrics).SelectMany(s => s.Metrics).Sum(x =>
                x.Gauge?.DataPoints.Count ?? x.Sum?.DataPoints.Count ?? x.Histogram?.DataPoints.Count
                ?? x.ExponentialHistogram?.DataPoints.Count ?? x.Summary?.DataPoints.Count ?? 0);
            Assert.Equal(points, m.Entries.Sum(e => e.Rows));
        }
    }

    [Fact]
    public void Metric_points_are_attributed_to_the_table_of_their_type()
    {
        var (_, _, metrics) = Shapers(new LoadProfile { Transport = new TransportLoad { RecordsPerExport = 500 } });
        var tables = metrics.Next().Entries.Select(e => e.Table).ToHashSet();
        Assert.Contains("gauge_data_points", tables);
        Assert.Contains("histogram_data_points", tables);
        Assert.Contains("summary_data_points", tables);
    }

    [Fact]
    public void Same_seed_gives_the_same_shape()
    {
        static List<(int Tenant, int Records)> Shape(int seed)
        {
            var p = Shaped(); p.Seed = seed;
            var (traces, logs, _) = Shapers(p);
            return Enumerable.Range(0, 10).Select(_ => traces.Next()).Select(x => (x.TenantIndex, x.Records))
                .Concat(Enumerable.Range(0, 10).Select(_ => logs.Next()).Select(x => (x.TenantIndex, x.Records))).ToList();
        }
        Assert.Equal(Shape(5), Shape(5));
    }

    [Fact]
    public void Full_orphan_fraction_never_sends_a_root_span()
    {
        var p = new LoadProfile { Time = new TimeShaping { OrphanFraction = 1 }, Traces = new TraceLoad { SpansPerTrace = new IntRange(4, 8) } };
        var (traces, _, _) = Shapers(p);
        var spans = Enumerable.Range(0, 10).SelectMany(_ => traces.Next().Request.ResourceSpans)
            .SelectMany(r => r.ScopeSpans).SelectMany(s => s.Spans).ToList();
        Assert.All(spans, s => Assert.False(s.ParentSpanId.IsEmpty));
    }

    [Fact]
    public void Backdated_records_are_past_every_retention_window()
    {
        var p = new LoadProfile { Time = new TimeShaping { BackdatedFraction = 1 } };
        var (_, logs, _) = Shapers(p);
        var payload = logs.Next();
        Assert.All(payload.Entries, e => Assert.Equal(RecordAge.Backdated, e.Age));
        var cutoff = TimeStamps.NowNanos() - 190L * 86_400_000_000_000L;
        Assert.All(payload.Request.ResourceLogs.SelectMany(r => r.ScopeLogs).SelectMany(s => s.LogRecords),
            r => Assert.True((long)r.TimeUnixNano < cutoff));
    }

    [Fact]
    public void Tenant_weights_skew_traffic()
    {
        var p = Shaped();
        var topology = new Topology(Tenants, p);
        var rng = new Random(9);
        var picks = Enumerable.Range(0, 4000).Select(_ => topology.PickTenant(rng)).ToList();
        var first = picks.Count(x => x == 0) / 4000.0;
        Assert.InRange(first, 0.70, 0.80);
    }

    [Fact]
    public void Ledger_separates_accepted_duplicates_rejected_and_failed()
    {
        var ledger = new SentLedger();
        var span = new LedgerEntry("spans", RecordAge.Current, 10, false, true);
        var log = new LedgerEntry("log_records", RecordAge.Current, 5, false, false);
        ledger.RecordAccepted(1, [span, log]);
        ledger.RecordAccepted(1, [span with { Redelivery = true }, log with { Redelivery = true }]);
        ledger.RecordRejected(1, [log]);
        ledger.RecordFailed(2, [span]);

        var cells = ledger.Snapshot();
        var spans1 = cells.Single(c => c.TenantId == 1 && c.Table == "spans");
        Assert.Equal((10, 10, 0, 10), (spans1.Rows, spans1.DuplicateRowsCollapsed, spans1.DuplicateRowsPersisted, spans1.ExpectedRows));
        var logs1 = cells.Single(c => c.TenantId == 1 && c.Table == "log_records");
        Assert.Equal((5, 0, 5, 10, 5), (logs1.Rows, logs1.DuplicateRowsCollapsed, logs1.DuplicateRowsPersisted, logs1.ExpectedRows, logs1.RowsRejected));
        Assert.Equal(10, cells.Single(c => c.TenantId == 2).RowsFailed);
    }

    [Fact]
    public async Task Rate_controller_is_open_loop_and_follows_rate_changes()
    {
        // Changed mid-run, as the ramp does: 200/s for a second, then 50/s for a second.
        var controller = new RateController(200);
        var count = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = controller.RunAsync((_, _) => Interlocked.Increment(ref count), cts.Token);

        await Task.Delay(1000);
        var firstSecond = Volatile.Read(ref count);
        controller.Rate = 50;
        await run;
        var secondSecond = count - firstSecond;

        Assert.InRange(firstSecond, 170, 230);
        Assert.InRange(secondSecond, 35, 65);
    }

    [Fact]
    public async Task Rate_controller_is_open_loop_catches_up_after_a_stall_and_reports_the_lateness()
    {
        // One tick stalls the scheduler for 100ms (ten ticks' worth at 100/s); every other callback is instant. A closed-loop
        // sender would lose those ten ticks for good. The open-loop controller catches up from the clock and reports how late
        // the delayed ticks were. (An earlier version made EVERY callback sleep 20ms and asserted at least 90 of 100 ticks
        // fired: a synchronous scheduler cannot do that, and the test passed only when a catch-up batch happened to overrun
        // the window, which depended on timing.)
        var controller = new RateController(100);
        var count = 0;
        var maxLag = TimeSpan.Zero;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await controller.RunAsync((_, lag) =>
        {
            if (lag > maxLag) maxLag = lag;
            if (Interlocked.Increment(ref count) == 1) Thread.Sleep(100);
        }, cts.Token);

        // 100 are scheduled in the second. The slack (80, not 100) covers a loaded machine stalling the scheduler thread near
        // the end of the window; the ten delayed by the stall must not be among the missing.
        Assert.True(count + controller.SkippedTicks >= 80, $"fired {count}, skipped {controller.SkippedTicks}");
        Assert.True(maxLag > TimeSpan.FromMilliseconds(50), $"max lag {maxLag.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Latency_histogram_percentiles_bracket_the_data()
    {
        var h = new LatencyHistogram();
        for (var i = 1; i <= 1000; i++) h.Record(i);
        var s = h.Summarize();
        Assert.Equal(1000, s.Count);
        Assert.InRange(s.P50Ms, 500, 530);
        Assert.InRange(s.P99Ms, 990, 1000);
        Assert.Equal(1000, s.MaxMs);
    }

    [Fact]
    public void Profile_json_overrides_only_what_it_names()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "seed": 9, "traces": { "spansPerSecond": 123 }, "time": { "orphanFraction": 0.5 } }""");
        var p = LoadProfile.Load(path);
        Assert.Equal(9, p.Seed);
        Assert.Equal(123, p.Traces.SpansPerSecond);
        Assert.Equal(0.5, p.Time.OrphanFraction);
        Assert.Equal(500, p.Logs.RecordsPerSecond);
    }
}
