using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Keryhe.Telemetry.Parquet;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Parquet provider registration. The host selects it with <c>Database:Provider = "Parquet"</c> and configures
/// <c>Telemetry:Parquet</c> (<see cref="ParquetOptions"/>); there is no connection string. It has no control plane:
/// a Parquet deployment runs that on PostgreSQL, SQL Server or MySQL (<c>ControlPlane:Provider</c>).
/// </summary>
public static class ParquetServiceCollectionExtensions
{
    /// <summary>Write-side services for the gRPC ingestion collector.</summary>
    public static IServiceCollection AddParquetCollectorServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddCommon(services, configuration);

        // Buffers are resident until the linger seals them, so the shared gate defaults (200,000 records, 256 MB) cap ingest near
        // 200,000 / linger per signal. Default each gate to 1,000,000 records (or two buffers if that is more) and the byte budget
        // to 2 GB: with the 5 s linger and 50,000-record buffers that measured about 97,000 records/s write-only on the reference
        // machine (stress-results p8-I; 30 s and 500,000 gave about 64,000). A value set in configuration still wins.
        services.AddOptions<TelemetryIngestionOptions>().PostConfigure<IOptions<ParquetOptions>>((o, pq) =>
        {
            var records = Math.Max(1_000_000, 2 * pq.Value.MaxBufferRecords);
            string Key(string name) => $"{TelemetryIngestionOptions.SectionName}:{name}";
            if (configuration[Key(nameof(o.MaxQueuedSpans))] is null) o.MaxQueuedSpans = records;
            if (configuration[Key(nameof(o.MaxQueuedLogRecords))] is null) o.MaxQueuedLogRecords = records;
            if (configuration[Key(nameof(o.MaxQueuedMetrics))] is null) o.MaxQueuedMetrics = records;
            if (configuration[Key(nameof(o.MaxQueuedBytesPerSignal))] is null && o.MaxQueuedBytesPerSignal != 0)
                o.MaxQueuedBytesPerSignal = 2L * 1024 * 1024 * 1024;
        });

        // The identity creates the root and _schema.json, verifies them, and takes the writer's exclusive lock; it is
        // created at startup (ParquetStartupCheck) so a mismatch or a duplicate writer id fails the host, not a flush.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ParquetOptions>>().Value;
            ParquetSchemaFile.EnsureAndVerify(options.RootPath);
            return ParquetWriterIdentity.Acquire(options.RootPath, options.WriterId);
        });
        services.AddSingleton(sp => new ManifestWriter(sp.GetRequiredService<IParquetFileStore>(), sp.GetRequiredService<ParquetWriterIdentity>().Id));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ParquetOptions>>().Value;
            return new ParquetFileWriter(options, Path.Combine(options.RootPath, "_tmp", Environment.MachineName));
        });
        services.AddSingleton<ParquetBulkWriter>();
        services.AddSingleton<ITelemetryBulkWriter>(sp => sp.GetRequiredService<ParquetBulkWriter>());
        services.AddSingleton<IParquetTokenWriter>(sp => sp.GetRequiredService<ParquetBulkWriter>());
        services.AddHostedService<ParquetCollectorStartup>();

        // Parquet batches for itself (plans/parquet-duckdb phase 2): swap the shared worker for ParquetIngestionWorker.
        // AddKeryheTelemetryCollector must have run first (the order Collector.Server uses) for the swap to find the
        // shared worker; a container that never registered it (tests that write through ITelemetryBulkWriter) just gets ours.
        // A full queue frees room a whole buffer at a time, so the default bounded wait before refusing an export is the
        // linger plus the usual 2 s (an explicit Telemetry:Ingestion:MaxGateWaitMilliseconds still wins).
        services.AddOptions<TelemetryIngestionOptions>().PostConfigure<IOptions<ParquetOptions>>((o, parquet) =>
            o.MaxGateWaitMilliseconds ??= parquet.Value.LingerMilliseconds + TelemetryIngestionOptions.DefaultMaxGateWaitMilliseconds);
        services.AddSingleton<IFlushErrorClassifier, ParquetFlushErrorClassifier>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRetentionWindows>(sp => new RetentionWindowCache(
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IOptions<ParquetOptions>>().Value.RetentionRefreshSeconds,
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<RetentionWindowCache>>()));
        var shared = services.FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TelemetryIngestionWorker));
        if (shared is not null) services.Remove(shared);
        else if (services.Any(d => d.ServiceType == typeof(TelemetryIngestionChannel)))
            throw new InvalidOperationException("The shared TelemetryIngestionWorker registration was not found; call AddKeryheTelemetryCollector before AddParquetCollectorServices.");
        services.AddHostedService<ParquetIngestionWorker>();
        services.AddScoped<IMetricTouchStore, ParquetMetricTouchStore>();
        services.AddScoped<IRollupStore, ParquetRollupStore>();
        return services;
    }

    /// <summary>
    /// Read-side services for the API: the DuckDB read engine, the trace, rollup and retention services and the compaction worker. The log, metric and
    /// resource repositories are placeholders until phase 5 of plans/parquet-duckdb.
    /// </summary>
    public static IServiceCollection AddParquetApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        AddCommon(services, configuration);
        services.AddSingleton<ParquetFileResolver>();
        services.AddSingleton<ParquetQueryEngine>();
        services.AddScoped<ITraceReadRepository, ParquetTraceReadRepository>();
        services.AddScoped<IRollupReadRepository, ParquetRollupReadRepository>();
        services.AddScoped<ILogReadRepository, ParquetLogReadRepository>();
        services.AddScoped<IMetricReadRepository, ParquetMetricReadRepository>();
        services.AddScoped<IResourceReadRepository, ParquetResourceReadRepository>();
        services.AddScoped<IRetentionSweeper, ParquetRetentionSweeper>();
        services.AddSingleton(ProviderCapabilities.FromConfiguration(ProviderCapabilities.Default(), configuration));
        services.AddHostedService<ParquetApiStartup>();

        // The compactor runs here, one instance at a time under a lease (README U3, plans/parquet-duckdb phase 4).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ParquetMetrics>();
        services.AddSingleton<ParquetCompactor>();
        services.AddSingleton<ParquetCompactionLease>();
        services.AddHostedService<ParquetCompactionWorker>();
        return services;
    }

    private static void AddCommon(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ParquetOptions>()
            .Bind(configuration.GetSection(ParquetOptions.SectionName))
            .Validate(o => { o.Validate(); return true; })
            .ValidateOnStart();
        services.AddSingleton<IParquetFileStore>(sp => new LocalParquetFileStore(sp.GetRequiredService<IOptions<ParquetOptions>>().Value.RootPath));
    }
}

/// <summary>Collector startup: the root, its schema file and the writer lock; clears this writer's leftover temp files.</summary>
internal sealed class ParquetCollectorStartup(
    ParquetWriterIdentity identity, IParquetFileStore store, IOptions<ParquetOptions> options,
    IOptions<TelemetryIngestionOptions> ingestion, ILogger<ParquetCollectorStartup> logger) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        // The gate bounds resident records, buffers included: it must hold at least one buffer, or the size seal never
        // fires and a full gate waits out the linger on every export.
        var gate = ingestion.Value;
        var buffer = options.Value.MaxBufferRecords;
        if (Math.Min(gate.MaxQueuedSpans, Math.Min(gate.MaxQueuedLogRecords, gate.MaxQueuedMetrics)) < buffer)
            logger.LogWarning(
                "Telemetry:Ingestion:MaxQueued* ({Spans} spans, {Logs} logs, {Metrics} metrics) is below Telemetry:Parquet:MaxBufferRecords ({Buffer}): " +
                "the queue holds less than a full buffer, so buffers are written by the linger ({Linger} ms) and exports are refused sooner under load; raise the gate sizes",
                gate.MaxQueuedSpans, gate.MaxQueuedLogRecords, gate.MaxQueuedMetrics, buffer, options.Value.LingerMilliseconds);

        var removed = store.DeleteTempFiles(identity.Id);
        logger.LogInformation("Parquet collector {WriterId} writing to {Root}; removed {Removed} leftover temp files", identity.Id, store.Root, removed);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        identity.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>API startup: the root must exist and carry the schema file this build expects.</summary>
internal sealed class ParquetApiStartup(IOptions<ParquetOptions> options) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        var root = options.Value.RootPath;
        if (!Directory.Exists(root))
            throw new InvalidOperationException($"{ParquetOptions.SectionName}:RootPath '{root}' does not exist; start the collector first (it creates it).");
        ParquetSchemaFile.Verify(root);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
