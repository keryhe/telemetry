using System.Reflection;
using System.Text.Json;
using Keryhe.Telemetry.Api.Alerting.Evaluators;
using Keryhe.Telemetry.Api.Alerting.Models;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// The three rollup alerts over fake repositories (plans/summary-rollups.md, "Alerts"): window arithmetic
/// ([writtenThrough - window, writtenThrough) in whole minutes), firing/not-firing, timeouts, and the slow-request
/// alert staying on raw spans. No database.
/// </summary>
public class RollupAlertEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 20, DateTimeKind.Utc);
    private static readonly RollupOptions Options = new() { CloseGraceSeconds = 30, FlushIntervalSeconds = 15, ArrivalMarginSeconds = 60 };
    // 12:00:20 minus 105 s, rounded down to the minute.
    private static readonly long WrittenThrough = TimeConversion.DateTimeToUnixNano(new DateTime(2026, 10, 1, 11, 58, 0, DateTimeKind.Utc));

    private sealed class FakeRollups : IRollupReadRepository
    {
        public List<RequestRollupAggregate> Requests = [];
        public List<LogRollupAggregate> Logs = [];
        public bool TimedOut;
        public RollupQuery? RequestQuery, LogQuery;

        public Task<RollupReadResult<RequestRollupAggregate>> GetRequestRollupAsync(RollupQuery query, CancellationToken ct = default)
        {
            RequestQuery = query;
            return Task.FromResult(new RollupReadResult<RequestRollupAggregate> { Rows = TimedOut ? [] : Requests, TimedOut = TimedOut });
        }

        public Task<RollupReadResult<LogRollupAggregate>> GetLogRollupAsync(RollupQuery query, CancellationToken ct = default)
        {
            LogQuery = query;
            return Task.FromResult(new RollupReadResult<LogRollupAggregate> { Rows = TimedOut ? [] : Logs, TimedOut = TimedOut });
        }
    }

    public class TraceStub : DispatchProxy
    {
        public SlowRequestCount Result;
        public (DateTime Start, DateTime End, string? Service, double MinMs)? Call;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ITraceReadRepository.CountSlowInboundSpansAsync)) throw new NotSupportedException();
            Call = ((DateTime)args![0]!, (DateTime)args[1]!, (string?)args[2], (double)args[3]!);
            return Task.FromResult(Result);
        }
    }

    private static AlertRule Rule(string conditionJson, string? service = "web") => new() { Id = 7, ServiceName = service, ConditionJson = conditionJson };

    private static RequestRollupAggregate Req(long count, long errors, int band = 6, long max = 10_000_000)
    {
        var bands = new long[DurationBands.Count];
        bands[band] = count;
        return new RequestRollupAggregate { RequestCount = count, ErrorCount = errors, MaxDurationNanos = max, Bands = bands };
    }

    // ---- ErrorRate ----

    private static ErrorRateEvaluator ErrorRate(FakeRollups fake) =>
        new(fake, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<ErrorRateEvaluator>.Instance);

    private const string ErrorRateCondition = """{"WindowMinutes":5,"ThresholdPercent":10}""";

    [Fact]
    public async Task ErrorRate_ReadsTheWholeMinutesEndingAtWrittenThrough_AndFiresOverTheThreshold()
    {
        var fake = new FakeRollups { Requests = [Req(80, 4), Req(20, 11)] }; // 15 / 100 = 15%
        var result = await ErrorRate(fake).EvaluateAsync(Rule(ErrorRateCondition), Now);

        Assert.True(result.IsFiring);
        Assert.Contains("15/100 requests", result.Details);
        Assert.Equal(WrittenThrough, fake.RequestQuery!.EndNano);
        Assert.Equal(WrittenThrough - 5 * 60_000_000_000L, fake.RequestQuery.StartNano);
        Assert.Equal("web", fake.RequestQuery.Service);
    }

    [Fact]
    public async Task ErrorRate_AtOrUnderTheThreshold_NoRequests_OrATimeout_DoesNotFire()
    {
        Assert.False((await ErrorRate(new FakeRollups { Requests = [Req(100, 10)] }).EvaluateAsync(Rule(ErrorRateCondition), Now)).IsFiring); // exactly 10%
        Assert.False((await ErrorRate(new FakeRollups()).EvaluateAsync(Rule(ErrorRateCondition), Now)).IsFiring);
        Assert.False((await ErrorRate(new FakeRollups { Requests = [Req(100, 90)], TimedOut = true }).EvaluateAsync(Rule(ErrorRateCondition), Now)).IsFiring);
    }

    [Fact]
    public async Task ErrorRate_AOneMinuteRule_ReadsExactlyOneFullMinute()
    {
        var fake = new FakeRollups { Requests = [Req(10, 10)] };
        await ErrorRate(fake).EvaluateAsync(Rule("""{"WindowMinutes":1,"ThresholdPercent":1}"""), Now);
        Assert.Equal(60_000_000_000L, fake.RequestQuery!.EndNano - fake.RequestQuery.StartNano);
    }

    // ---- LogSeveritySpike ----

    private static LogSeveritySpikeEvaluator LogSpike(FakeRollups fake) =>
        new(fake, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<LogSeveritySpikeEvaluator>.Instance);

    private const string LogCondition = """{"WindowMinutes":10,"MinSeverity":17,"CountThreshold":5}""";

    [Fact]
    public async Task LogSpike_CountsTheRollupAtOrAboveTheSeverity_AndFiresOverTheThreshold()
    {
        var fake = new FakeRollups { Logs = [new() { SeverityNumber = 17, RecordCount = 4 }, new() { SeverityNumber = 21, RecordCount = 3 }] };
        var result = await LogSpike(fake).EvaluateAsync(Rule(LogCondition), Now);

        Assert.True(result.IsFiring);
        Assert.Contains("7 log records", result.Details);
        Assert.Equal(17, fake.LogQuery!.MinSeverity);
        Assert.Equal(WrittenThrough, fake.LogQuery.EndNano);
        Assert.Equal(WrittenThrough - 10 * 60_000_000_000L, fake.LogQuery.StartNano);
    }

    [Fact]
    public async Task LogSpike_AtTheThresholdOrOnATimeout_DoesNotFire()
    {
        Assert.False((await LogSpike(new FakeRollups { Logs = [new() { SeverityNumber = 17, RecordCount = 5 }] }).EvaluateAsync(Rule(LogCondition), Now)).IsFiring);
        Assert.False((await LogSpike(new FakeRollups { TimedOut = true }).EvaluateAsync(Rule(LogCondition), Now)).IsFiring);
    }

    // ---- SlowTrace ----

    private static (SlowTraceEvaluator Evaluator, TraceStub Traces) Slow(FakeRollups fake, SlowRequestCount count)
    {
        var traces = DispatchProxy.Create<ITraceReadRepository, TraceStub>();
        var stub = (TraceStub)(object)traces;
        stub.Result = count;
        return (new SlowTraceEvaluator(traces, fake, NullLogger<SlowTraceEvaluator>.Instance), stub);
    }

    private const string SlowCondition = """{"WindowMinutes":5,"MinDurationMs":500}""";

    [Fact]
    public async Task SlowRequests_StayOnRawSpans_OverTheRulesOwnWindow_AndTheMessageCarriesAnApproximateP99()
    {
        var (evaluator, traces) = Slow(new FakeRollups { Requests = [Req(10, 0, band: 12, max: 700_000_000)] }, new SlowRequestCount(4, false));
        var result = await evaluator.EvaluateAsync(Rule(SlowCondition), Now);

        Assert.True(result.IsFiring);
        Assert.Contains("4 slow request(s) over 500ms", result.Details);
        Assert.Contains("p99 ≈", result.Details);
        Assert.Equal((Now.AddMinutes(-5), Now, "web", 500d), traces.Call);   // no writtenThrough lag: raw, exact
    }

    [Fact]
    public async Task SlowRequests_NoneOrTimedOut_DoesNotFire_AndAFailedRollupOnlyDropsTheP99()
    {
        Assert.False((await Slow(new FakeRollups(), new SlowRequestCount(0, false)).Evaluator.EvaluateAsync(Rule(SlowCondition), Now)).IsFiring);
        Assert.False((await Slow(new FakeRollups(), new SlowRequestCount(0, true)).Evaluator.EvaluateAsync(Rule(SlowCondition), Now)).IsFiring);

        var result = await Slow(new FakeRollups { TimedOut = true }, new SlowRequestCount(2, false)).Evaluator.EvaluateAsync(Rule(SlowCondition), Now);
        Assert.True(result.IsFiring);
        Assert.DoesNotContain("p99", result.Details);
    }
}
