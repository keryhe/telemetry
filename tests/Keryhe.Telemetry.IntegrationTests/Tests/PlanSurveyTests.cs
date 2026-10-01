using System.Reflection;
using System.Text;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// One-off plan survey (schema-simplification Phase 2 verification): seeds a realistic-ish volume, builds the real
/// anchor SQL through the repository's own <c>AnchorsSql</c> and writes each provider's execution plan for the
/// trace page (unscoped and service-scoped), trace detail and log page, to the file named by
/// <c>PLAN_SURVEY_OUT</c> (a no-op unless that variable is set), e.g. <c>PLAN_SURVEY_OUT=/tmp/plans.txt dotnet test --filter PlanSurvey</c>.
/// Re-run it after a change to the anchor SQL, an index or a sort key to see which index each provider actually uses.
/// </summary>
public abstract class PlanSurveyBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected PlanSurveyBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    protected abstract string Limit(int n);
    protected abstract Task PrepareAsync();
    protected abstract Task<string> ExplainAsync(string sql, Dictionary<string, object> parameters);

    protected ProviderFixture Fixture => _fixture;

    [Fact]
    public async Task Survey()
    {
        var output = Environment.GetEnvironmentVariable("PLAN_SURVEY_OUT");
        if (string.IsNullOrEmpty(output)) return;

        using (var write = _fixture.Services.CreateScope())
        {
            var writer = write.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
            for (var chunk = 0; chunk < 60; chunk++)
                await writer.FlushTracesAsync(SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart.AddSeconds(chunk * 1000), traceCount: 1000, seedOffset: chunk * 10_000));
            for (var chunk = 0; chunk < 60; chunk++)
                await writer.FlushLogsAsync(SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart.AddSeconds(chunk * 1000), count: 1000));
        }
        await PrepareAsync();

        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITraceReadRepository>();
        string Anchors(bool service, bool pin) => (string)repo.GetType()
            .GetMethod("AnchorsSql", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repo, [service, pin, false])!;

        // The last 1000 s of 60,000 s of data: a selective window, the shape a real trace-list page asks for.
        var start = SeededDataBuilder.ToUnixNano(WindowStart.AddSeconds(59_000));
        var end = SeededDataBuilder.ToUnixNano(WindowStart.AddSeconds(60_000));
        var parameters = new Dictionary<string, object>
        {
            ["tenantId"] = _fixture.TenantId, ["start"] = start, ["end"] = end, ["anchorFrom"] = start - 300_000_000_000L,
            ["service"] = "checkout-api", ["asOf"] = DateTime.UtcNow.AddMinutes(5)
        };

        var sb = new StringBuilder();
        async Task Section(string title, string sql, Dictionary<string, object>? p = null)
        {
            sb.AppendLine($"==== {_fixture.ProviderName}: {title}");
            sb.AppendLine(await ExplainAsync(sql, p ?? parameters));
            sb.AppendLine();
        }

        string Page(bool service) => $"""
            SELECT a.trace_id, a.anchor_span_pk, a.anchor_span_id, a.service_name, a.root_name, a.anchor_kind, a.anchor_start, a.anchor_end, a.has_error
            FROM {Anchors(service, pin: true)} a
            ORDER BY a.anchor_start DESC, a.anchor_span_pk DESC
            {Limit(51)}
            """;
        await Section("trace page, unscoped (first page)", Page(false));
        await Section("trace page, service-scoped (first page)", Page(true));
        await Section("trace summary rows, unscoped, inbound only", $"""
            SELECT a.trace_id, a.anchor_start, a.anchor_end, a.service_name, a.has_error
            FROM {Anchors(false, pin: false)} a WHERE a.anchor_kind IN ('SERVER', 'CONSUMER')
            """);

        var anyTrace = SeededDataBuilder.BasicTraceWindow(_fixture.TenantId, WindowStart, traceCount: 1)[0].TraceIdHex;
        await Section("trace detail", """
            SELECT s.id FROM spans s WHERE s.tenant_id = @tenantId AND s.trace_id = @traceId ORDER BY s.start_time_unix_nano
            """, new() { ["tenantId"] = _fixture.TenantId, ["traceId"] = anyTrace });
        if (_fixture.ProviderName == ProviderNames.ClickHouse)
        {
            // The repository narrows a by-trace read with the bounds it reads from trace_index (ResolveTraceTimeBoundsAsync).
            var traceStart = SeededDataBuilder.ToUnixNano(WindowStart);
            await Section("trace detail with trace_index time bounds", """
                SELECT s.id FROM spans s
                WHERE s.tenant_id = @tenantId AND s.trace_id = @traceId AND s.start_time_unix_nano >= @traceStart AND s.start_time_unix_nano <= @traceEnd
                ORDER BY s.start_time_unix_nano
                """, new() { ["tenantId"] = _fixture.TenantId, ["traceId"] = anyTrace, ["traceStart"] = traceStart, ["traceEnd"] = traceStart + 10_000_000L });
        }
        await Section("log page (first page)", $"""
            SELECT lr.id, lr.time_unix_nano FROM log_records lr
            WHERE lr.tenant_id = @tenantId AND lr.time_unix_nano >= @start AND lr.time_unix_nano <= @end AND lr.service_name = @service
            ORDER BY lr.time_unix_nano DESC, lr.id DESC
            {Limit(101)}
            """);
        await Section("logs for a trace", "SELECT lr.id FROM log_records lr WHERE lr.tenant_id = @tenantId AND lr.trace_id = @traceId",
            new() { ["tenantId"] = _fixture.TenantId, ["traceId"] = anyTrace });
        await Section("tenant retention candidate rows (spans)", """
            SELECT s.start_time_unix_nano FROM spans s WHERE s.tenant_id = @tenantId AND s.start_time_unix_nano < @start
            """, new() { ["tenantId"] = _fixture.TenantId, ["start"] = start });

        await File.AppendAllTextAsync(output, sb.ToString());
    }
}
