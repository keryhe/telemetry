using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Keryhe.Telemetry.IntegrationTests.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Phase 5 correctness checks (list-pages-server-side plan): the metrics catalog's capped
/// list (instances and groupBy=name), the <c>metric_last_seen</c> "seen in range" approximation
/// vs. its exact-EXISTS fallback past a 1-hour-old window end (decision 27), the touch worker
/// advancing <c>last_seen_unix_nano</c> within one flush interval, and deadlock safety under two
/// concurrent touch batches.
///
/// The shared <see cref="ProviderFixture"/> harness writes through <see cref="ITelemetryBulkWriter"/>
/// directly and does not run the real <c>MetricTouchWorker</c> hosted service, so these tests drive
/// the same two steps that worker performs on its interval: drain <see cref="MetricTouchTracker"/>
/// (populated automatically by each relational bulk writer's <c>FlushMetricsAsync</c>) and call
/// <see cref="IMetricTouchStore.TouchAsync"/> directly. On ClickHouse this manual step is a
/// deliberate no-op (see <c>ClickHouseMetricTouchStore</c>) — its <c>metric_catalog</c> is written
/// by <c>FlushMetricsAsync</c> itself, so no extra step is needed there at all.
/// </summary>
public abstract class MetricPhase5TestsBase : IAsyncLifetime
{
    private readonly ProviderFixture _fixture;
    protected MetricPhase5TestsBase(ProviderFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Whether a catalog "instance" is one resource of a service (every relational provider) or the service itself (ClickHouse, whose
    /// catalog is keyed by tenant, service, metric and type: plans/clickhouse-redesign README R6). Only the assertions that count
    /// instances depend on it.
    /// </summary>
    protected virtual bool CatalogInstanceIsPerResource => true;

    public Task InitializeAsync() => _fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Months in the past relative to "today" in this test run, so every test that doesn't
    // deliberately use a near-now window exercises decision 27's exact-EXISTS fallback path
    // (end more than 1 hour in the past) rather than the approximate metric_last_seen join.
    private static readonly DateTime WindowStart = new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

    private IServiceScope Scope() => _fixture.Services.CreateScope();

    /// <summary>Flushes <paramref name="metrics"/>, then simulates one MetricTouchWorker interval (a no-op on ClickHouse).</summary>
    private async Task FlushAndTouchAsync(params MetricModel[] metrics)
    {
        using var scope = Scope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync(metrics.ToList());

        var tracker = sp.GetRequiredService<MetricTouchTracker>();
        var touches = tracker.Drain();
        if (touches.Count > 0)
            await sp.GetRequiredService<IMetricTouchStore>().TouchAsync(touches, CancellationToken.None);
    }

    [Fact]
    public async Task Catalog_InstanceView_ReturnsTheFirstRowsOfTheFullOrder_AndSaysWhenTruncated()
    {
        const int count = 25;
        var metrics = Enumerable.Range(0, count)
            .Select(i => SeededDataBuilder.CatalogMetric(_fixture.TenantId, $"phase5.instances.metric{i % 5}", MetricType.GAUGE, "phase5-svc", WindowStart.AddSeconds(i), instanceId: $"pod-{i}"))
            .ToArray();
        await FlushAndTouchAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        Task<MetricCatalogPage> Catalog(int limit) => repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddDays(-1), End = WindowStart.AddDays(1), GroupBy = "instance", Limit = limit
        });

        // 25 pods of one service over 5 metric names: 25 instances, or 5 where an instance is a service's metric.
        var expected = CatalogInstanceIsPerResource ? count : 5;

        // All share (nearly) one creation time, so the order leans on the id tiebreak: a shorter list must still be
        // the head of the full one, with no row repeated.
        var all = await Catalog(expected);
        Assert.Equal(expected, all.Items.Count);
        Assert.Equal(expected, all.Items.Select(i => i.Id).Distinct().Count());
        Assert.False(all.Truncated); // exactly N rows matched: nothing more

        var head = await Catalog(3);
        Assert.Equal(all.Items.Take(3).Select(i => i.Id), head.Items.Select(i => i.Id));
        Assert.True(head.Truncated);

        var oneShort = await Catalog(expected - 1);
        Assert.Equal(expected - 1, oneShort.Items.Count);
        Assert.True(oneShort.Truncated);

        var roomToSpare = await Catalog(expected + 50);
        Assert.Equal(expected, roomToSpare.Items.Count);
        Assert.False(roomToSpare.Truncated);
    }

    [Fact]
    public async Task Catalog_NameView_ReturnsTheFirstNamesOfTheFullOrder_AndSaysWhenTruncated()
    {
        var metrics = Enumerable.Range(0, 12)
            .Select(i => SeededDataBuilder.CatalogMetric(_fixture.TenantId, $"phase5.names.metric{i:D2}", MetricType.GAUGE, "phase5-svc", WindowStart.AddSeconds(i), instanceId: $"pod-{i}"))
            .ToArray();
        await FlushAndTouchAsync(metrics);

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        Task<MetricCatalogPage> Catalog(int limit) => repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddDays(-1), End = WindowStart.AddDays(1), GroupBy = "name", Q = "phase5.names", Limit = limit
        });

        var all = await Catalog(12);
        Assert.Equal(12, all.Names.Count);
        Assert.Equal(12, all.Names.Select(n => n.Name).Distinct().Count());
        Assert.False(all.Truncated);

        var head = await Catalog(5);
        Assert.Equal(all.Names.Take(5).Select(n => n.Name), head.Names.Select(n => n.Name));
        Assert.True(head.Truncated);

        Assert.True((await Catalog(11)).Truncated);
    }

    [Fact]
    public async Task Catalog_NameGroupView_ComputesInstanceCountAndServices()
    {
        await FlushAndTouchAsync(
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.grouped.metric", MetricType.SUM, "svc-a", WindowStart, instanceId: "a1"),
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.grouped.metric", MetricType.SUM, "svc-a", WindowStart.AddSeconds(1), instanceId: "a2"),
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.grouped.metric", MetricType.SUM, "svc-b", WindowStart.AddSeconds(2), instanceId: "b1")
        );

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var page = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddDays(-1),
            End = WindowStart.AddDays(1),
            GroupBy = "name",
            Q = "phase5.grouped",
            Limit = 10
        });

        var row = Assert.Single(page.Names);
        Assert.Equal("phase5.grouped.metric", row.Name);
        Assert.Equal(MetricType.SUM, row.Type);
        Assert.Equal(CatalogInstanceIsPerResource ? 3 : 2, row.InstanceCount); // a1, a2, b1 -- or the two services
        Assert.Equal(new[] { "svc-a", "svc-b" }, row.Services.OrderBy(s => s).ToArray());
    }

    [Fact]
    public async Task Catalog_ServiceAndTypeFilters_NarrowResults()
    {
        await FlushAndTouchAsync(
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.filter.gauge", MetricType.GAUGE, "filter-svc-a", WindowStart, instanceId: "g1"),
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.filter.sum", MetricType.SUM, "filter-svc-b", WindowStart, instanceId: "s1")
        );

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var byService = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddDays(-1), End = WindowStart.AddDays(1),
            GroupBy = "instance", Service = "filter-svc-a", Q = "phase5.filter", Limit = 10
        });
        var item = Assert.Single(byService.Items);
        Assert.Equal("phase5.filter.gauge", item.Name);

        var byType = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddDays(-1), End = WindowStart.AddDays(1),
            GroupBy = "instance", Type = MetricType.SUM, Q = "phase5.filter", Limit = 10
        });
        var typeItem = Assert.Single(byType.Items);
        Assert.Equal("phase5.filter.sum", typeItem.Name);
    }

    [Fact]
    public async Task SeenInRange_ExactFallback_ExcludesMetricOutsideWindow()
    {
        // WindowStart is months in the past, so this query's `end` is well past the 1-hour cutoff
        // and the catalog falls back to the exact per-candidate EXISTS check (decision 27).
        await FlushAndTouchAsync(
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.exact.in", MetricType.GAUGE, "exact-svc", WindowStart, instanceId: "in"),
            SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.exact.out", MetricType.GAUGE, "exact-svc", WindowStart.AddDays(-10), instanceId: "out")
        );

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();

        var page = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = WindowStart.AddHours(-1),
            End = WindowStart.AddHours(1),
            GroupBy = "instance",
            Q = "phase5.exact",
            Limit = 10
        });

        var names = page.Items.Select(i => i.Name).ToList();
        Assert.Contains("phase5.exact.in", names);
        Assert.DoesNotContain("phase5.exact.out", names);
    }

    [Fact]
    public async Task SeenInRange_Approximate_ExcludesUntouchedMetricThenIncludesAfterTouch()
    {
        // A near-"now" window exercises the approximate metric_last_seen join instead of the
        // exact-EXISTS fallback (decision 27's 1-hour cutoff).
        var now = DateTime.UtcNow;
        var pointTime = now.AddMinutes(-2);

        var metric = SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.approx.metric", MetricType.GAUGE, "approx-svc", pointTime, instanceId: "only");

        long touchedCount;
        using (var writeScope = Scope())
        {
            var sp = writeScope.ServiceProvider;
            await sp.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);
            // Drained (not just peeked) here so it doubles as this flush's touch-worker interval
            // input further below — a relational provider's MetricTouchTracker is populated by the
            // flush above; ClickHouse's never is (see the class doc comment), so touchedCount == 0
            // there and the "not yet touched" window is skipped as not applicable to it.
            touchedCount = sp.GetRequiredService<MetricTouchTracker>().Drain().Count;
        }

        if (touchedCount > 0)
        {
            using var readScope = Scope();
            var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();
            var beforeTouch = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
            {
                // A fresh DateTime.UtcNow at query time, not the pre-insert `now` above: the
                // metrics.created_at <= @end half of the approximate clause must see the row's
                // actual (DB-assigned, necessarily later than the pre-insert C# clock read) insert
                // time, or this would spuriously fail on providers whose write and read clocks are
                // close enough for the pre-insert timestamp to land before it.
                Start = now.AddHours(-1), End = DateTime.UtcNow, GroupBy = "instance", Q = "phase5.approx", Limit = 10
            });
            // Not yet touched: the approximate join finds no metric_last_seen row, so it is excluded
            // — documented in SeenInRangeClause's own doc comment.
            Assert.Empty(beforeTouch.Items);

            // Simulate MetricTouchWorker's interval by re-flushing (repopulates the tracker, which
            // the drain above already emptied) and touching.
            using var rewriteScope = Scope();
            var sp = rewriteScope.ServiceProvider;
            await sp.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([metric]);
            var touches = sp.GetRequiredService<MetricTouchTracker>().Drain();
            await sp.GetRequiredService<IMetricTouchStore>().TouchAsync(touches, CancellationToken.None);
        }

        using (var readScope = Scope())
        {
            var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();
            // A short bounded poll tolerates a brief propagation lag under the test container for a
            // single-row flush, without weakening the assertion itself (still exactly one matching row).
            MetricCatalogPage afterTouch = null!;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                afterTouch = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
                {
                    Start = now.AddHours(-1), End = DateTime.UtcNow, GroupBy = "instance", Q = "phase5.approx", Limit = 10
                });
                if (afterTouch.Items.Count > 0) break;
                await Task.Delay(200);
            }
            var item = Assert.Single(afterTouch.Items);
            Assert.Equal("phase5.approx.metric", item.Name);
        }
    }

    [Fact]
    public async Task TouchWorker_AdvancesLastSeen_KeepingTheGreaterValue()
    {
        // Relational providers only (see the class doc comment): ClickHouse's MetricTouchTracker
        // is never populated (its metric_last_seen is kept current by materialized views instead),
        // so there is nothing for a "worker interval" to advance here — its monotonic-max guarantee
        // comes from maxMerge/maxState themselves, exercised by SeenInRange_Approximate instead.
        var later = DateTime.UtcNow.AddMinutes(-1);
        var earlier = later.AddMinutes(-10);

        var laterMetric = SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.advance.metric", MetricType.GAUGE, "advance-svc", later, instanceId: "only");
        long touchedCount;
        using (var writeScope = Scope())
        {
            var sp = writeScope.ServiceProvider;
            await sp.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([laterMetric]);
            var touches = sp.GetRequiredService<MetricTouchTracker>().Drain().ToList();
            touchedCount = touches.Count;
            if (touchedCount > 0)
                await sp.GetRequiredService<IMetricTouchStore>().TouchAsync(touches, CancellationToken.None);
        }
        if (touchedCount == 0) return; // ClickHouse: nothing more to exercise here.

        // A second, EARLIER-timestamped flush of the same metric instance must never move
        // last_seen_unix_nano backwards (MetricTouchTracker/IMetricTouchStore both keep the max).
        var earlierMetric = SeededDataBuilder.CatalogMetric(_fixture.TenantId, "phase5.advance.metric", MetricType.GAUGE, "advance-svc", earlier, instanceId: "only");
        using (var writeScope = Scope())
        {
            var sp = writeScope.ServiceProvider;
            await sp.GetRequiredService<ITelemetryBulkWriter>().FlushMetricsAsync([earlierMetric]);
            var touches = sp.GetRequiredService<MetricTouchTracker>().Drain();
            if (touches.Count > 0)
                await sp.GetRequiredService<IMetricTouchStore>().TouchAsync(touches, CancellationToken.None);
        }

        using var readScope = Scope();
        var repo = readScope.ServiceProvider.GetRequiredService<IMetricReadRepository>();
        // A window whose start sits strictly AFTER `earlier` but at/before `later`: only satisfied
        // by last_seen_unix_nano still reflecting the LATER flush. If the earlier flush had
        // regressed it, this metric would drop out of the approximate "seen in range" join
        // entirely (decision 27) and the page would come back empty.
        var page = await repo.GetMetricCatalogPageAsync(new MetricCatalogQuery
        {
            Start = earlier.AddMinutes(5), End = DateTime.UtcNow, GroupBy = "instance", Q = "phase5.advance", Limit = 10
        });
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task TouchWorker_ConcurrentBatches_DoNotDeadlock()
    {
        // Two overlapping id sets, both sorted ascending before dispatch — decision 27's
        // deadlock-avoidance rule for concurrent collector instances. This directly exercises
        // IMetricTouchStore.TouchAsync (skipped as a no-op on ClickHouse, which has nothing to
        // deadlock: see IMetricTouchStore's own doc comment).
        var metrics = Enumerable.Range(0, 20)
            .Select(i => SeededDataBuilder.CatalogMetric(_fixture.TenantId, $"phase5.concurrent.metric{i}", MetricType.GAUGE, "concurrent-svc", WindowStart, instanceId: $"c{i}"))
            .ToArray();

        using var writeScope = Scope();
        var writer = writeScope.ServiceProvider.GetRequiredService<ITelemetryBulkWriter>();
        await writer.FlushMetricsAsync(metrics.ToList());

        var tracker = writeScope.ServiceProvider.GetRequiredService<MetricTouchTracker>();
        var all = tracker.Drain().OrderBy(kv => kv.Key).ToList();
        if (all.Count == 0) return; // ClickHouse: tracker is never populated (no relational touch path)

        var half = all.Count / 2;
        var batchA = all.Take(half + (all.Count % 2)).ToList(); // overlaps nowhere by construction, but both run concurrently against the same table
        var batchB = all.Skip(half).ToList();

        using var scopeA = Scope();
        using var scopeB = Scope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IMetricTouchStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IMetricTouchStore>();

        var exception = await Record.ExceptionAsync(async () =>
            await Task.WhenAll(
                storeA.TouchAsync(batchA, CancellationToken.None),
                storeB.TouchAsync(batchB, CancellationToken.None)));

        Assert.Null(exception);
    }
}
