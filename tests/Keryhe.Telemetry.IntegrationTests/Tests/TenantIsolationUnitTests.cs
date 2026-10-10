using System.Net;
using Keryhe.Telemetry.Collector;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Threading;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>Collector improvements phase 3, no database: per-tenant accounting in the gate, the rate limiter, the failure limiter and the quota options.</summary>
[Trait("Suite", "TenantIsolation")]
public class TenantIsolationUnitTests
{
    private static RecordCountGate.TenantTally Tally(long tenant, int records, long bytes = 0)
    {
        var t = new RecordCountGate.TenantTally();
        t.Add(tenant, records, bytes);
        return t;
    }

    // ---- the gate's per-tenant share ----

    [Fact]
    public async Task One_tenant_is_capped_at_its_share_while_another_still_gets_in()
    {
        var gate = new RecordCountGate(capacity: 100);
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 0.5, 40, 0, TimeSpan.Zero, default));
        Assert.Equal(RecordCountGate.Result.TenantQuota, await gate.TryAcquireAsync(1, 0.5, 11, 0, TimeSpan.Zero, default));   // 51 > 50
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 0.5, 10, 0, TimeSpan.Zero, default));      // exactly 50
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(2, 0.5, 40, 0, TimeSpan.Zero, default));      // the other tenant is unaffected
        Assert.Equal(100, gate.Resident == 90 ? 100 : 0);
        Assert.Equal(new Dictionary<long, int> { [1] = 50, [2] = 40 }, gate.TenantResident());
    }

    [Fact]
    public async Task Whole_queue_full_is_reported_as_full_not_as_a_tenant_problem_and_starts_the_saturation_clock()
    {
        var gate = new RecordCountGate(capacity: 100);
        await gate.TryAcquireAsync(1, 1.0, 100, 0, TimeSpan.Zero, default);
        Assert.Equal(RecordCountGate.Result.Full, await gate.TryAcquireAsync(2, 0.5, 10, 0, TimeSpan.Zero, default));
        Assert.True(gate.SaturatedFor >= TimeSpan.Zero && gate.IsSaturated);

        // Control: a tenant refused for its share does not mark the instance saturated (readiness).
        var other = new RecordCountGate(capacity: 100);
        await other.TryAcquireAsync(1, 0.5, 40, 0, TimeSpan.Zero, default);
        Assert.Equal(RecordCountGate.Result.TenantQuota, await other.TryAcquireAsync(1, 0.5, 20, 0, TimeSpan.Zero, default));
        Assert.Equal(TimeSpan.Zero, other.SaturatedFor);
    }

    [Fact]
    public async Task A_tenant_holding_nothing_is_admitted_even_when_one_export_exceeds_its_share()
    {
        var gate = new RecordCountGate(capacity: 100);
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 0.1, 80, 0, TimeSpan.Zero, default));      // 80 > 10, but it holds nothing
        Assert.Equal(RecordCountGate.Result.TenantQuota, await gate.TryAcquireAsync(1, 0.1, 1, 0, TimeSpan.Zero, default));
    }

    [Fact]
    public async Task The_share_applies_to_bytes_too()
    {
        var gate = new RecordCountGate(capacity: 1000, byteCapacity: 1000);
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 0.5, 1, 400, TimeSpan.Zero, default));
        Assert.Equal(RecordCountGate.Result.TenantQuota, await gate.TryAcquireAsync(1, 0.5, 1, 200, TimeSpan.Zero, default));  // 600 > 500 bytes, records far below
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(2, 0.5, 1, 200, TimeSpan.Zero, default));
    }

    [Fact]
    public async Task Release_gives_each_tenant_its_share_back_from_a_merged_batch()
    {
        var gate = new RecordCountGate(capacity: 100, byteCapacity: 10_000);
        await gate.TryAcquireAsync(1, 1.0, 30, 3_000, TimeSpan.Zero, default);
        await gate.TryAcquireAsync(2, 1.0, 20, 2_000, TimeSpan.Zero, default);

        var merged = new RecordCountGate.TenantTally();
        merged.Add(1, 30, 3_000);
        merged.Add(2, 5, 500);
        gate.Release(merged);

        Assert.Equal(15, gate.Resident);
        Assert.Equal(1_500, gate.ResidentBytes);
        Assert.Equal(new Dictionary<long, int> { [2] = 15 }, gate.TenantResident());   // tenant 1 held nothing and is gone

        gate.Release(Tally(2, 15, 1_500));
        Assert.Empty(gate.TenantResident());
        Assert.Equal((0, 0L), (gate.Resident, gate.ResidentBytes));
    }

    [Fact]
    public async Task A_share_of_one_is_todays_behaviour()
    {
        var gate = new RecordCountGate(capacity: 100);
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 1.0, 60, 0, TimeSpan.Zero, default));
        Assert.Equal(RecordCountGate.Result.Admitted, await gate.TryAcquireAsync(1, 1.0, 40, 0, TimeSpan.Zero, default));
    }

    // ---- the rate limiter ----

    [Fact]
    public async Task Rate_limiter_admits_a_burst_then_says_how_long_until_tokens_return()
    {
        var limiter = new TenantRateLimiter();
        Assert.True(limiter.TryAcquire(1, "traces", 100, 100, out _));
        Assert.False(limiter.TryAcquire(1, "traces", 50, 100, out var retry));
        Assert.InRange(retry.TotalSeconds, 0.3, 0.55);                         // 50 tokens at 100/s

        Assert.True(limiter.TryAcquire(2, "traces", 100, 100, out _));         // another tenant has its own bucket
        Assert.True(limiter.TryAcquire(1, "logs", 100, 100, out _));           // and so does another signal

        await Task.Delay(retry + TimeSpan.FromMilliseconds(80));
        Assert.True(limiter.TryAcquire(1, "traces", 50, 100, out _));
    }

    [Fact]
    public void An_export_larger_than_the_burst_is_charged_the_burst_not_refused_for_ever()
    {
        var limiter = new TenantRateLimiter();
        Assert.True(limiter.TryAcquire(1, "traces", 5_000, 100, out _));       // needs a full bucket, takes it
        Assert.False(limiter.TryAcquire(1, "traces", 1, 100, out var retry));
        Assert.True(retry > TimeSpan.Zero);
        Assert.True(limiter.TryAcquire(1, "traces", 5_000, 0, out _));         // rate 0 is off
    }

    // ---- the failure limiter ----

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static AuthFailureLimiter Limiter(Clock clock, double perSecond = 5, int burst = 20, int max = 100_000) =>
        new(Options.Create(new TelemetryCollectorOptions { AuthFailureLimit = new() { PerSecond = perSecond, Burst = burst, MaxTrackedClients = max } }), clock);

    [Fact]
    public void Only_recorded_failures_take_tokens_and_they_come_back_with_time()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var limiter = Limiter(clock);
        var ip = IPAddress.Parse("203.0.113.9");

        for (var i = 0; i < 100; i++) Assert.False(limiter.IsExhausted(ip));   // asking costs nothing
        for (var i = 0; i < 20; i++) limiter.RecordFailure(ip);
        Assert.True(limiter.IsExhausted(ip));

        clock.Advance(TimeSpan.FromSeconds(1));                                 // 5 per second
        Assert.False(limiter.IsExhausted(ip));
        for (var i = 0; i < 5; i++) limiter.RecordFailure(ip);
        Assert.True(limiter.IsExhausted(ip));

        Assert.False(limiter.IsExhausted(IPAddress.Parse("198.51.100.7")));     // another address has its own bucket
    }

    [Fact]
    public void A_limit_of_zero_turns_the_failure_limiter_off()
    {
        var limiter = Limiter(new Clock(DateTimeOffset.UtcNow), perSecond: 0);
        for (var i = 0; i < 1000; i++) limiter.RecordFailure(IPAddress.Loopback);
        Assert.False(limiter.IsExhausted(IPAddress.Loopback));
        Assert.Equal(0, limiter.Tracked);
    }

    [Fact]
    public void IPv6_addresses_are_grouped_by_their_64_bit_prefix_and_v4_mapped_addresses_are_v4()
    {
        var limiter = Limiter(new Clock(DateTimeOffset.UtcNow));
        var a = IPAddress.Parse("2001:db8:1:2::1");
        var sameBlock = IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        for (var i = 0; i < 20; i++) limiter.RecordFailure(a);
        Assert.True(limiter.IsExhausted(sameBlock));
        Assert.False(limiter.IsExhausted(IPAddress.Parse("2001:db8:1:3::1")));

        for (var i = 0; i < 20; i++) limiter.RecordFailure(IPAddress.Parse("192.0.2.1"));
        Assert.True(limiter.IsExhausted(IPAddress.Parse("192.0.2.1").MapToIPv6()));
    }

    [Fact]
    public void Tracked_addresses_are_bounded_new_ones_share_an_overflow_bucket_and_idle_ones_are_dropped()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var limiter = Limiter(clock, max: 10);
        for (var i = 1; i <= 50; i++) limiter.RecordFailure(IPAddress.Parse($"10.0.0.{i}"));
        Assert.True(limiter.Tracked <= 11);                                     // ten addresses and the overflow bucket

        // The overflow bucket is shared: its owners exhaust it together.
        for (var i = 0; i < 25; i++) limiter.RecordFailure(IPAddress.Parse($"10.1.0.{i + 1}"));
        Assert.True(limiter.IsExhausted(IPAddress.Parse("10.9.9.9")));

        clock.Advance(TimeSpan.FromMinutes(5));                                 // every bucket would be full again
        limiter.IsExhausted(IPAddress.Parse("10.200.0.1"));                     // triggers the sweep
        Assert.True(limiter.Tracked <= 3);
    }

    // ---- options ----

    [Fact]
    public void Quota_options_resolve_overrides_and_reject_bad_values()
    {
        var o = new TenantQuotaOptions
        {
            MaxShare = 0.4, RecordsPerSecond = 100,
            Overrides = { ["7"] = new() { MaxShare = 0.9 }, ["8"] = new() { RecordsPerSecond = 5 } }
        };
        o.Validate();
        Assert.Equal((0.4, 100d), (o.ShareFor(1), o.RateFor(1)));
        Assert.Equal((0.9, 100d), (o.ShareFor(7), o.RateFor(7)));
        Assert.Equal((0.4, 5d), (o.ShareFor(8), o.RateFor(8)));

        Assert.Throws<InvalidOperationException>(() => new TenantQuotaOptions { MaxShare = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TenantQuotaOptions { MaxShare = 1.5 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TenantQuotaOptions { RecordsPerSecond = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TenantQuotaOptions { Overrides = { ["x"] = new() } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TenantQuotaOptions { Overrides = { ["3"] = new() { MaxShare = 2 } } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AuthFailureLimitOptions { Burst = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new TenantResolutionOptions { MaxConcurrentLookups = 0 }.Validate());
    }
}
