using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Config;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;
using Xunit;

namespace Keryhe.Telemetry.TestDataGenerator.Tests;

public class TimeAndDeterminismTests
{
    private static string Fingerprint(SimChunk c) =>
        string.Join(";", c.AllSpans().Select(s => $"{s.TraceId}/{s.SpanId}/{s.Name}/{s.Start.UtcTicks}/{s.End.UtcTicks}/{s.Status}"));

    [Fact]
    public void The_same_seed_yields_identical_telemetry()
    {
        var from = TestSupport.Day.AddHours(19);
        var a = TestSupport.Simulator().Simulate(from, from.AddMinutes(2), includeSamples: true);
        var b = TestSupport.Simulator().Simulate(from, from.AddMinutes(2), includeSamples: true);
        Assert.Equal(Fingerprint(a), Fingerprint(b));
        Assert.Equal(a.Samples.Select(s => s.Value), b.Samples.Select(s => s.Value));
    }

    [Fact]
    public void A_different_seed_yields_different_telemetry()
    {
        var from = TestSupport.Day.AddHours(19);
        var a = TestSupport.Simulator(TestSupport.Options(seed: 1)).Simulate(from, from.AddMinutes(1), false);
        var b = TestSupport.Simulator(TestSupport.Options(seed: 2)).Simulate(from, from.AddMinutes(1), false);
        Assert.NotEqual(Fingerprint(a), Fingerprint(b));
    }

    [Fact]
    public void How_time_is_chunked_does_not_change_the_traffic()
    {
        var from = TestSupport.Day.AddHours(19);
        var whole = TestSupport.Simulator().Simulate(from, from.AddSeconds(120), false);
        var sim = TestSupport.Simulator();
        var parts = Enumerable.Range(0, 4).Select(i => sim.Simulate(from.AddSeconds(30 * i), from.AddSeconds(30 * (i + 1)), false)).ToList();

        Assert.Equal(whole.Traces.Count, parts.Sum(p => p.Traces.Count));
        Assert.Equal(
            whole.Traces.Select(t => t.TraceId).Order(),
            parts.SelectMany(p => p.Traces).Select(t => t.TraceId).Order());
    }

    [Fact]
    public void Traffic_peaks_in_the_evening_and_dips_overnight()
    {
        var evening = TrafficCurve.Daily(TestSupport.Day.AddHours(20));
        var overnight = TrafficCurve.Daily(TestSupport.Day.AddHours(8));
        Assert.True(evening > 0.95);
        Assert.True(overnight < 0.2);
        Assert.True(TrafficCurve.Weekday(TestSupport.Day.AddDays(3)) > TrafficCurve.Weekday(TestSupport.Day)); // Saturday
    }

    [Fact]
    public void Tenant_scale_scales_the_volume()
    {
        var from = TestSupport.Day.AddHours(20);
        int Requests(double scale)
        {
            var o = TestSupport.Options();
            o.Tenants[0].Scale = scale;
            return TestSupport.Simulator(o).Simulate(from, from.AddMinutes(5), false).Traces.Count(t => t.Kind == SimSpanKind.Server);
        }
        var small = Requests(0.3);
        var large = Requests(3.0);
        Assert.InRange((double)large / small, 6.0, 16.0); // nominally 10x
    }

    [Fact]
    public void Peak_rate_is_roughly_the_configured_requests_per_second()
    {
        var from = TestSupport.Day.AddHours(20);
        var sim = TestSupport.Simulator(TestSupport.Options(peak: 2));
        var requests = sim.Simulate(from, from.AddMinutes(10), false).Traces.Count(t => t.Kind == SimSpanKind.Server);
        Assert.InRange(requests / 600.0, 1.2, 2.8); // 2/s at peak, with per-minute burstiness
    }

    private static IncidentOptions PaymentIncident() => new()
    {
        Name = "payment", Tenant = "acme-retail", Service = Services.Payment,
        At = TimeSpan.FromHours(14), Duration = TimeSpan.FromMinutes(20), LatencyMultiplier = 6, ExtraErrorRate = 0.5,
    };

    private static (double MedianMs, double ErrorRate) PaymentStats(DateTimeOffset from, params IncidentOptions[] incidents)
    {
        var options = TestSupport.Options(7, 8, incidents);
        var chunk = TestSupport.Simulator(options).Simulate(from, from.AddMinutes(8), false);
        var payments = chunk.AllSpans().Where(s => s.Kind == SimSpanKind.Server && s.Instance.Service == Services.Payment).ToList();
        Assert.True(payments.Count > 20, $"only {payments.Count} payment spans");
        var durations = payments.Select(s => (s.End - s.Start).TotalMilliseconds).Order().ToList();
        return (durations[durations.Count / 2], payments.Count(s => s.Status == SimStatus.Error) / (double)payments.Count);
    }

    [Fact]
    public void An_incident_slows_and_fails_only_its_service_and_only_inside_its_window()
    {
        var inside = PaymentStats(TestSupport.Day.AddHours(14).AddMinutes(5), PaymentIncident());
        var before = PaymentStats(TestSupport.Day.AddHours(13).AddMinutes(30), PaymentIncident());
        var after = PaymentStats(TestSupport.Day.AddHours(14).AddMinutes(40), PaymentIncident());

        Assert.True(inside.MedianMs > before.MedianMs * 2.5, $"{inside.MedianMs} vs {before.MedianMs}");
        Assert.True(inside.ErrorRate > 0.3, $"error rate during the incident was {inside.ErrorRate:P0}");
        Assert.True(before.ErrorRate < 0.15);
        Assert.True(after.ErrorRate < 0.15);
    }

    [Fact]
    public void An_incident_for_another_tenant_does_not_apply()
    {
        var other = PaymentIncident();
        other.Tenant = "contoso-dev";
        var stats = PaymentStats(TestSupport.Day.AddHours(14).AddMinutes(5), other);
        Assert.True(stats.ErrorRate < 0.15);
    }

    [Fact]
    public void A_bad_deploy_replaces_the_pods_and_a_rollback_restores_them()
    {
        var deploy = new IncidentOptions
        {
            Tenant = "acme-retail", Service = Services.Catalog, Version = "2.8.0-rc1",
            At = TimeSpan.FromHours(10.5), Duration = TimeSpan.FromMinutes(25),
        };
        var sim = TestSupport.Simulator(TestSupport.Options(7, 2, deploy));
        var registry = sim.Instances;

        var before = registry.ActiveAt(Services.Catalog, TestSupport.Day.AddHours(10)).ToList();
        var during = registry.ActiveAt(Services.Catalog, TestSupport.Day.AddHours(10.6)).ToList();
        var after = registry.ActiveAt(Services.Catalog, TestSupport.Day.AddHours(11.2)).ToList();

        Assert.All(during, i => Assert.Equal("2.8.0-rc1", i.Version));
        Assert.Empty(before.Select(i => i.PodName).Intersect(during.Select(i => i.PodName)));
        Assert.Equal(before.Select(i => i.PodName), after.Select(i => i.PodName)); // rollback brings the original pods back
    }

    [Fact]
    public void Pods_start_by_logging_once_and_a_new_version_logs_its_own_startup()
    {
        var sim = TestSupport.Simulator();
        var from = TestSupport.Day.AddHours(19);
        var first = sim.Simulate(from, from.AddMinutes(1), false);
        var second = sim.Simulate(from.AddMinutes(1), from.AddMinutes(2), false);
        Assert.Contains(first.BackgroundLogs, l => l.Body.Contains("Application started"));
        Assert.DoesNotContain(second.BackgroundLogs, l => l.Body.Contains("Application started"));
    }

    [Fact]
    public void Pod_names_do_not_collide_across_tenants()
    {
        var o = TestSupport.Options();
        var inc = new IncidentSchedule("a", []);
        var names = new[] { "acme-retail", "contoso-dev", "globex-payments" }
            .SelectMany(t => new InstanceRegistry(t, o, inc).ActiveAt(TestSupport.Day.AddHours(12)))
            .Select(i => i.PodName)
            .ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }
}
