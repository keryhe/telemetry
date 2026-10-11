using System.Text.RegularExpressions;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>No-Docker tests of the retention defaults and <see cref="RetentionPolicy"/> (plans/tenant-retention.md).</summary>
[Trait("Suite", "Retention")]
public class RetentionPolicyTests
{
    private static string ControlPlaneScript(string provider)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", $"{provider}-ControlPlane.sql"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "schema", $"{provider}-ControlPlane.sql"));
    }

    /// <summary>The built-in windows and a new tenant's column defaults must be the same numbers.</summary>
    [Theory]
    [InlineData("PostgreSQL")]
    [InlineData("SqlServer")]
    [InlineData("MySQL")]
    public void The_schema_defaults_equal_RetentionDefaults(string provider)
    {
        var script = ControlPlaneScript(provider);
        int Default(string column)
        {
            var m = Regex.Match(script, $@"""?{column}""?\s+INT(?:EGER)?\s+NOT NULL DEFAULT (\d+)", RegexOptions.IgnoreCase);
            Assert.True(m.Success, $"{provider}: no NOT NULL DEFAULT for {column}");
            return int.Parse(m.Groups[1].Value);
        }

        Assert.Equal(RetentionDefaults.TraceDays, Default("trace_retention_days"));
        Assert.Equal(RetentionDefaults.LogDays, Default("log_retention_days"));
        Assert.Equal(RetentionDefaults.MetricDays, Default("metric_retention_days"));
        Assert.DoesNotContain("retention_settings", script);
    }

    [Fact]
    public void An_unknown_tenant_takes_the_built_in_window_and_a_known_one_its_own()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var policy = new RetentionPolicy(new Dictionary<long, RetentionSettings>
        {
            [1] = new() { TraceRetentionDays = 10, LogRetentionDays = 20, MetricRetentionDays = 60 }
        }, now);

        Assert.Equal(now.AddDays(-10), policy.CutoffFor(RetentionSignal.Traces, 1));
        Assert.Equal(now.AddDays(-20), policy.CutoffFor(RetentionSignal.Logs, 1));
        Assert.Equal(now.AddDays(-60), policy.CutoffFor(RetentionSignal.Metrics, 1));
        Assert.Equal(now.AddDays(-RetentionDefaults.TraceDays), policy.CutoffFor(RetentionSignal.Traces, 99));
        Assert.True(policy.IsKnown(1));
        Assert.False(policy.IsKnown(99));
    }

    [Fact]
    public void The_distinct_and_longest_windows_always_include_the_built_in_one()
    {
        var policy = RetentionPolicies.For((1, 10), (2, 10), (3, 90));
        Assert.Equal([TimeSpan.FromDays(10), TimeSpan.FromDays(30), TimeSpan.FromDays(90)], policy.DistinctWindows(RetentionSignal.Traces));
        Assert.Equal(TimeSpan.FromDays(90), policy.LongestWindow(RetentionSignal.Traces));
        Assert.Equal([1L, 2L], policy.TenantsWithWindow(RetentionSignal.Traces, TimeSpan.FromDays(10)));

        // Every known tenant shorter than the built-in one: the built-in window is still the longest (orphans may exist).
        var shorter = RetentionPolicies.For((1, 5));
        Assert.Equal(TimeSpan.FromDays(30), shorter.LongestWindow(RetentionSignal.Logs));
        Assert.Empty(shorter.TenantsWithWindow(RetentionSignal.Logs, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void A_negative_window_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPolicies.For((1, -1)));
}
