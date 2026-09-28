using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Phase 7 search parity checks (list-pages-server-side plan, Verification item 6): a fixed set
/// of search queries must return exactly the same matching rows before and after this phase's
/// indexes existed. Since the indexes are baked into the schema once applied, "before" is
/// approximated by hand-computing the expected match set directly from
/// <see cref="SeededDataBuilder.BasicLogWindow"/>'s own deterministic formula (every attribute
/// value is derived from the loop index, so the expected set is knowable without querying the
/// database at all) and asserting the repository's actual output -- running through whichever
/// predicate form this phase wired up for this provider (typed containment on the analytics
/// tier, the unchanged phase 1 text form on the standard tier) -- equals it exactly. This
/// exercises the real indexed code path end-to-end (real SQL against a real database), not a
/// mock, so a wrong index OR a wrong predicate rewrite both show up as a mismatch.
///
/// Covers the exact set the plan's Verification item 6 asks for: a numeric attribute
/// (`http.status_code:500`), a boolean attribute (`retry:true`), a negated term (`-retry:true`,
/// where the un-negated rows all carry `retry`, so this is a real "value differs" case, not an
/// "absent key" case -- BasicLogWindow gives every row a `retry` key), a dotted key
/// (`k8s.pod.name:...`), and a plain free-text substring.
/// </summary>
public abstract class SearchParityTestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected SearchParityTestsBase(ProviderFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime WindowStart = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    /// <summary>
    /// Seeds <see cref="SeededDataBuilder.BasicLogWindow"/> (300 rows: `BodyValue` = "phase0 log
    /// #{i} from {service}", `http.status_code` = 500 when `i % 4 == 3` else 200, `retry` = true
    /// when `i % 4 == 3` else false, `k8s.pod.name` = "{service}-pod-{i % 5}") and returns every
    /// matching log's `BodyValue` set for <paramref name="search"/> -- `BodyValue` is unique per
    /// row in this fixture, so it stands in for an id the read model doesn't expose.
    /// </summary>
    private async Task<HashSet<string>> SearchAsync(string search, int count = 300)
    {
        var logs = SeededDataBuilder.BasicLogWindow(_fixture.TenantId, WindowStart, count);
        using (var writeScope = Scope())
            await writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>().FlushLogsAsync(logs);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<ILogReadRepository>();
        var page = await repo.GetLogPageAsync(new LogQuery
        {
            Start = WindowStart,
            End = WindowStart.AddSeconds(count + 5),
            Search = search,
            Size = 500,
            AsOf = DateTime.UtcNow.AddMinutes(1)
        });

        Assert.Null(page.NextCursor); // 300 rows fit in one 500-row page -- sanity check the fixture stays inside this test's own assumptions
        return page.Items.Select(l => l.BodyValue!).ToHashSet();
    }

    private static HashSet<string> Expected(int count, Func<int, bool> predicate)
    {
        var services = new[] { "checkout-api", "payments-worker", "inventory-api" };
        var result = new HashSet<string>();
        for (var i = 0; i < count; i++)
            if (predicate(i))
                result.Add($"phase0 log #{i} from {services[i % services.Length]}");
        return result;
    }

    [Fact]
    public async Task NumericAttribute_MatchesExactSet()
    {
        var actual = await SearchAsync("http.status_code:500");
        var expected = Expected(300, i => i % 4 == 3);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task BooleanAttribute_MatchesExactSet()
    {
        var actual = await SearchAsync("retry:true");
        var expected = Expected(300, i => i % 4 == 3);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every seeded row carries a `retry` key (true or false), so `-retry:true` is a real
    /// "value differs" negation, not an "absent key" one -- it must return every row where
    /// `retry` is false, decision 10's null-tolerant negation semantics never come into play here
    /// (that path is covered by <see cref="RawSearchWindowGuardTests"/>'s unit-level coverage of
    /// the parser/guard, and by TraceReadRepositoryBase's own span-absent-key behavior).
    /// </summary>
    [Fact]
    public async Task NegatedAttribute_ReturnsRowsWithDifferentValue()
    {
        var actual = await SearchAsync("-retry:true");
        var expected = Expected(300, i => i % 4 != 3);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task DottedKey_MatchesExactSet()
    {
        // k8s.pod.name is "{service}-pod-{i % 5}" -- pick pod index 2 on checkout-api (index 0 mod 3).
        var actual = await SearchAsync("k8s.pod.name:checkout-api-pod-2");
        var expected = Expected(300, i => i % 3 == 0 && i % 5 == 2);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FreeText_SubstringMatchesExactSet()
    {
        var actual = await SearchAsync("payments-worker");
        var expected = Expected(300, i => i % 3 == 1);
        Assert.Equal(expected, actual);
    }
}
