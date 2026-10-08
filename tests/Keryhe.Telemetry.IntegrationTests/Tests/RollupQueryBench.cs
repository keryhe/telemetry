using System.Diagnostics;
using System.Text;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Models;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Opt-in lab benchmark for the summary rollups (plans/summary-rollups.md, "Measurement gate"): seeds minute rows for seven
/// days at the stress profile's shape (twelve services, three partial rows per service-minute -- two collectors writing
/// closed minutes plus late rows; logs over four severities) and times the request and log summary reads at 7d, 7d for one
/// service, 24h and 1h through the repository the API uses, writing one table to the file named by <c>ROLLUP_BENCH_OUT</c>
/// (a no-op unless set). The gate: Phase 4 (the hour tier) is built on a provider only if its 7d unscoped request summary
/// exceeds 1 s p95. <c>ROLLUP_BENCH_RUNS</c> sets the timed runs per read (default 20, the p95 is the 19th of 20).
///
/// Run providers one at a time (xUnit starts every provider's collection in parallel, which contaminates timings):
/// <c>ROLLUP_BENCH_OUT=/tmp/r.txt dotnet test tests/Keryhe.Telemetry.IntegrationTests --no-build --filter "Provider=PostgreSQL&amp;FullyQualifiedName~RollupQueryBench"</c>.
/// </summary>
public abstract class RollupQueryBenchBase(ProviderFixture fixture) : IAsyncLifetime
{
    private static readonly string[] Services =
    [
        "web-frontend", "api-gateway", "user-service", "catalog-service", "cart-service", "order-service",
        "inventory-service", "payment-service", "email-service", "search-service", "recommendation-service", "auth-service"
    ];
    private static readonly int[] Severities = [5, 9, 13, 17];
    private const long Minute = 60_000_000_000L;
    private const int PartialsPerMinute = 3;

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Appends seeded rows by the provider's own path.</summary>
    protected abstract Task AppendAsync(IReadOnlyList<RequestRollupRow> requests, IReadOnlyList<LogRollupRow> logs);

    /// <summary>Provider hook after the seed (statistics, merges) so plans match a settled database.</summary>
    protected virtual Task AfterSeedAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bench()
    {
        var output = Environment.GetEnvironmentVariable("ROLLUP_BENCH_OUT");
        if (string.IsNullOrEmpty(output)) return;
        var runs = int.TryParse(Environment.GetEnvironmentVariable("ROLLUP_BENCH_RUNS"), out var r) ? r : 20;

        var end = DateTime.UtcNow;
        var lastMinute = TimeConversion.DateTimeToUnixNano(end) / Minute * Minute - 2 * Minute;
        var firstMinute = lastMinute - (7 * 1440 - 1) * Minute;

        var seedWatch = Stopwatch.StartNew();
        var rng = new Random(42);
        var requests = new List<RequestRollupRow>(20_000);
        var logs = new List<LogRollupRow>(20_000);
        long requestRows = 0, logRows = 0;
        async Task FlushAsync()
        {
            if (requests.Count + logs.Count == 0) return;
            await AppendAsync(requests, logs);
            requestRows += requests.Count; logRows += logs.Count;
            requests.Clear(); logs.Clear();
        }
        for (var minute = firstMinute; minute <= lastMinute; minute += Minute)
        {
            foreach (var service in Services)
            {
                for (var p = 0; p < PartialsPerMinute; p++)
                {
                    var row = new RequestRollupRow { TenantId = fixture.TenantId, ServiceName = service, BucketStartUnixNano = minute };
                    var count = rng.Next(1, 40);
                    row.RequestCount = count;
                    row.ErrorCount = rng.Next(0, 3);
                    for (var i = 0; i < count; i++)
                    {
                        var band = Math.Min(DurationBands.Count - 1, (int)Math.Abs(rng.NextDouble() * rng.NextDouble() * 14));
                        row.Bands[band]++;
                        row.SumDurationNanos += DurationBands.LowerEdgeNanos(band) + 1;
                    }
                    row.MaxDurationNanos = row.SumDurationNanos / count * 3;
                    requests.Add(row);
                    foreach (var severity in Severities)
                        logs.Add(new LogRollupRow
                        {
                            TenantId = fixture.TenantId, ServiceName = service, SeverityNumber = severity,
                            BucketStartUnixNano = minute, RecordCount = rng.Next(1, 60)
                        });
                }
            }
            if (requests.Count >= 20_000) await FlushAsync();
        }
        await FlushAsync();
        await AfterSeedAsync();
        var seedSeconds = seedWatch.Elapsed.TotalSeconds;

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRollupReadRepository>();
        var rows = new List<(string Op, double P50, double P95, double Max, string Note)>();

        async Task Time(string op, Func<Task<string>> call)
        {
            await call(); // warm-up: plan cache, buffer pool
            var times = new List<double>(runs);
            var note = "";
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                note = await call();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            rows.Add((op, times[times.Count / 2], times[(int)Math.Ceiling(times.Count * 0.95) - 1], times[^1], note));
        }

        var written = DateTime.UtcNow.AddMinutes(-2);
        foreach (var (name, window) in new[] { ("7d", TimeSpan.FromDays(7)), ("24h", TimeSpan.FromHours(24)), ("1h", TimeSpan.FromHours(1)) })
        {
            foreach (var service in new string?[] { null, "order-service" })
            {
                if (service != null && name != "7d") continue;
                var label = service == null ? name : $"{name} service";
                var plan = RollupSummaryBuilder.PlanWindow(end - window, end, written, 60);
                var query = new RollupQuery { StartNano = plan.StartNano, EndNano = plan.EndNano, BucketSeconds = plan.BucketSeconds, Service = service };
                await Time($"requests {label}", async () =>
                {
                    var result = await repo.GetRequestRollupAsync(query);
                    return result.TimedOut ? "TIMED OUT" : $"{result.Rows.Count} rows, {plan.BucketSeconds}s buckets";
                });
                await Time($"logs {label}", async () =>
                {
                    var result = await repo.GetLogRollupAsync(query);
                    return result.TimedOut ? "TIMED OUT" : $"{result.Rows.Count} rows";
                });
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"==== {fixture.ProviderName} rollup bench: {Services.Length} services x 7d x {PartialsPerMinute} partials = {requestRows:N0} request rows, {logRows:N0} log rows, seed={seedSeconds:0}s runs={runs} (ms: p50 / p95 / max)");
        foreach (var (op, p50, p95, max, note) in rows)
            sb.AppendLine($"  {op,-22} {p50,9:0.0} / {p95,9:0.0} / {max,9:0.0}   {note}");
        var gate = rows.First(x => x.Op == "requests 7d");
        sb.AppendLine($"  GATE (7d unscoped request summary p95 <= 1000 ms): {(gate.P95 <= 1000 ? "MET" : "MISSED")}");
        sb.AppendLine();
        await File.AppendAllTextAsync(output, sb.ToString());
    }
}

[Collection(ProviderNames.PostgreSql)]
[Trait("Provider", ProviderNames.PostgreSql)]
public sealed class PostgreSqlRollupQueryBench(PostgreSqlFixture fixture) : RollupQueryBenchBase(fixture)
{
    protected override async Task AppendAsync(IReadOnlyList<RequestRollupRow> requests, IReadOnlyList<LogRollupRow> logs)
    {
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
        await store.AppendRequestsAsync(requests, default);
        await store.AppendLogsAsync(logs, default);
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "request_rollup_minute", "log_rollup_minute" })
        {
            await using var cmd = new Npgsql.NpgsqlCommand($"VACUUM (ANALYZE) {table}", conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

[Collection(ProviderNames.SqlServer)]
[Trait("Provider", ProviderNames.SqlServer)]
public sealed class SqlServerRollupQueryBench(SqlServerFixture fixture) : RollupQueryBenchBase(fixture)
{
    protected override async Task AppendAsync(IReadOnlyList<RequestRollupRow> requests, IReadOnlyList<LogRollupRow> logs)
    {
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
        await store.AppendRequestsAsync(requests, default);
        await store.AppendLogsAsync(logs, default);
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "UPDATE STATISTICS request_rollup_minute WITH FULLSCAN; UPDATE STATISTICS log_rollup_minute WITH FULLSCAN", conn) { CommandTimeout = 600 };
        await cmd.ExecuteNonQueryAsync();
    }
}

[Collection(ProviderNames.MySql)]
[Trait("Provider", ProviderNames.MySql)]
public sealed class MySqlRollupQueryBench(MySqlFixture fixture) : RollupQueryBenchBase(fixture)
{
    protected override async Task AppendAsync(IReadOnlyList<RequestRollupRow> requests, IReadOnlyList<LogRollupRow> logs)
    {
        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRollupStore>();
        await store.AppendRequestsAsync(requests, default);
        await store.AppendLogsAsync(logs, default);
    }

    protected override async Task AfterSeedAsync()
    {
        // MySQL's hour tier (Phase 4): the week of minute rows is folded into hour rows first, as the API host's worker would.
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IRollupCompactor>().CompactAsync(DateTime.UtcNow);

        await using var conn = new MySqlConnector.MySqlConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "request_rollup_minute", "log_rollup_minute", "request_rollup_hour", "log_rollup_hour" })
        {
            await using var cmd = new MySqlConnector.MySqlCommand($"ANALYZE TABLE {table}", conn) { CommandTimeout = 600 };
            await using var reader = await cmd.ExecuteReaderAsync();
        }
    }
}

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseRollupQueryBench(ClickHouseFixture fixture) : RollupQueryBenchBase(fixture)
{
    // The rollup tables are fed by materialized views on spans/log_records in production; the bench writes the target tables directly.
    protected override async Task AppendAsync(IReadOnlyList<RequestRollupRow> requests, IReadOnlyList<LogRollupRow> logs)
    {
        await using var conn = new global::ClickHouse.Client.ADO.ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();

        var requestColumns = new List<string> { "tenant_id", "service_name", "bucket_start_unix_nano", "request_count", "error_count", "sum_duration_nanos", "max_duration_nanos" };
        requestColumns.AddRange(Enumerable.Range(0, DurationBands.Count).Select(i => $"h{i:00}"));
        using (var copy = new global::ClickHouse.Client.Copy.ClickHouseBulkCopy(conn)
               { DestinationTableName = "request_rollup_minute", ColumnNames = requestColumns, BatchSize = 100_000 })
        {
            await copy.InitAsync();
            await copy.WriteToServerAsync(requests.Select(r =>
            {
                var values = new List<object> { r.TenantId, r.ServiceName, r.BucketStartUnixNano, r.RequestCount, r.ErrorCount, r.SumDurationNanos, r.MaxDurationNanos };
                values.AddRange(r.Bands.Cast<object>());
                return values.ToArray();
            }), default);
        }

        using var logCopy = new global::ClickHouse.Client.Copy.ClickHouseBulkCopy(conn)
        {
            DestinationTableName = "log_rollup_minute",
            ColumnNames = ["tenant_id", "service_name", "severity_number", "bucket_start_unix_nano", "record_count"],
            BatchSize = 100_000
        };
        await logCopy.InitAsync();
        await logCopy.WriteToServerAsync(logs.Select(r => new object[] { r.TenantId, r.ServiceName, r.SeverityNumber, r.BucketStartUnixNano, r.RecordCount }), default);
    }

    protected override async Task AfterSeedAsync()
    {
        await using var conn = new global::ClickHouse.Client.ADO.ClickHouseConnection(fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        foreach (var table in new[] { "request_rollup_minute", "log_rollup_minute" })
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"OPTIMIZE TABLE {table} FINAL";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
