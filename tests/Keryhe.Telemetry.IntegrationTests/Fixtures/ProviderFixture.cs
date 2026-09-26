using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Core.Data.Read;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Fixtures;

/// <summary>
/// One per provider, started once per test run (xUnit collection fixture, see
/// <see cref="Collections"/>) and shared by every test class in that provider's collection.
///
/// Builds a bare <see cref="ServiceCollection"/> through the provider's own
/// <c>Add&lt;Provider&gt;CollectorServices</c>/<c>Add&lt;Provider&gt;ApiServices</c> — the same
/// registration path the real hosts use — rather than hand-built repositories, per the plan's
/// Phase 0 section. It deliberately does NOT call <c>AddKeryheTelemetryCollector</c>/
/// <c>AddKeryheTelemetryApi</c>: those also wire gRPC, MVC controllers and the background
/// ingestion worker, none of which this harness needs since it writes through
/// <see cref="ITelemetryBulkWriter"/> directly. It does still register the handful of
/// provider-agnostic singletons the bulk writers and trace read repository depend on directly
/// (<see cref="ResourceScopeCache"/>, <see cref="TraceQueryCache"/>, logging) — see each
/// provider's own <c>*BulkWriter</c>/<c>*TraceReadRepository</c> constructor.
/// </summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    public abstract string ProviderName { get; }

    public const string ApiKeyPlainText = "phase0-integration-test-key";

    public ServiceProvider Services { get; private set; } = null!;
    public TestTenantContext TenantContext { get; } = new();
    public long TenantId { get; private set; }

    protected abstract Task StartContainerAsync(CancellationToken cancellationToken);
    protected abstract Task StopContainerAsync();

    /// <summary>Connection string for the write side (<c>ConnectionStrings:Collector</c>).</summary>
    protected abstract string CollectorConnectionString { get; }

    /// <summary>Connection string for the read side (<c>ConnectionStrings:Api</c>). Same database as the collector string — one test database.</summary>
    protected abstract string ApiConnectionString { get; }

    /// <summary>Applies the raw schema script for this provider, exactly as <c>apply-schema.sh</c> would.</summary>
    protected abstract Task ApplySchemaAsync(CancellationToken cancellationToken);

    /// <summary>Calls this provider's own <c>Add&lt;Provider&gt;CollectorServices</c>/<c>Add&lt;Provider&gt;ApiServices</c>.</summary>
    protected abstract void AddProviderServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Inserts a fixed tenant + API key row directly and returns the tenant id.</summary>
    protected abstract Task<long> SeedTenantAndApiKeyAsync(string keyHash, CancellationToken cancellationToken);

    /// <summary>Truncates the signal tables (not the container) so test classes in the same collection start clean.</summary>
    public abstract Task ResetAsync();

    public async Task InitializeAsync()
    {
        await StartContainerAsync(CancellationToken.None);
        await ApplySchemaAsync(CancellationToken.None);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Collector"] = CollectorConnectionString,
                ["ConnectionStrings:Api"] = ApiConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);

        // Depended on directly by every provider's *BulkWriter and *TraceReadRepository — normally
        // registered by AddKeryheTelemetryCollector/AddKeryheTelemetryApi, which this harness
        // deliberately does not call (see class doc comment).
        services.AddSingleton<ResourceScopeCache>();
        services.Configure<TraceQueryCacheOptions>(configuration.GetSection(TraceQueryCacheOptions.SectionName));
        services.AddSingleton<TraceQueryCache>();

        services.AddSingleton<ITenantContext>(TenantContext);

        AddProviderServices(services, configuration);

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiKeyPlainText))).ToLowerInvariant();
        TenantId = await SeedTenantAndApiKeyAsync(keyHash, CancellationToken.None);
        TenantContext.SetTenantId(TenantId);
    }

    public async Task DisposeAsync()
    {
        if (Services != null)
            await Services.DisposeAsync();
        await StopContainerAsync();
    }

    /// <summary>
    /// Splits a schema script into individually-executable statements for providers whose driver
    /// can't run a whole multi-statement script through one command (SQL Server's <c>GO</c>
    /// batches, MySQL, ClickHouse). Statements are ended by a line that, once trimmed, ends with
    /// <c>;</c> — adequate for these DDL-only scripts, none of which embed a literal semicolon
    /// inside a string value.
    /// </summary>
    protected static List<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith("--") || trimmed.Length == 0)
                continue;
            current.AppendLine(line);
            if (trimmed.EndsWith(';'))
            {
                var statement = current.ToString().Trim();
                if (statement.Length > 0)
                    statements.Add(statement);
                current.Clear();
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0)
            statements.Add(tail);
        return statements;
    }

    /// <summary>Splits a SQL Server script on lines that are exactly <c>GO</c> (case-insensitive), the batch separator <c>SqlCommand</c> doesn't understand.</summary>
    protected static List<string> SplitGoBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in script.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = current.ToString().Trim();
                if (batch.Length > 0)
                    batches.Add(batch);
                current.Clear();
            }
            else
            {
                current.AppendLine(line);
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0)
            batches.Add(tail);
        return batches;
    }
}
