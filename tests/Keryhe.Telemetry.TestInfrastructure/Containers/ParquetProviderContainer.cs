using Keryhe.Telemetry.TestInfrastructure.Sqlite;

namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>Which database holds the Parquet provider's control plane.</summary>
public enum ParquetControlPlane
{
    /// <summary>A SQLite file: no server and no container (the default, so Parquet tests and runs need no Docker).</summary>
    Sqlite,
    /// <summary>A PostgreSQL container composed here, as ClickHouse has.</summary>
    PostgreSql
}

/// <summary>
/// The Parquet provider has no database server: its "database" is a temp directory (<see cref="ConnectionString"/> is its
/// path, which the fixtures hand to <c>Telemetry:Parquet:RootPath</c>). Like ClickHouse it has no control plane of its own;
/// by default that is a SQLite file (<see cref="ParquetControlPlane.Sqlite"/>), so nothing here needs Docker. The stress
/// harness's <c>--control-plane postgresql</c> composes a PostgreSQL container instead, started and disposed with this one.
/// The tenants and keys are seeded into whichever it is. The root's <c>_schema.json</c> is created by the collector
/// registration on first use, as it is in production.
/// </summary>
public sealed class ParquetProviderContainer : ProviderContainer
{
    private readonly PostgreSqlProviderContainer? _postgres;
    private readonly SqliteControlPlane? _sqlite;
    private string? _root;

    /// <param name="controlPlane">Which database the control plane runs on.</param>
    /// <param name="sqliteDirectory">For a SQLite control plane, the directory its file lives in (kept after the run); null uses a temp directory that is deleted.</param>
    public ParquetProviderContainer(ParquetControlPlane controlPlane = ParquetControlPlane.Sqlite, string? sqliteDirectory = null)
    {
        ControlPlane = controlPlane;
        if (controlPlane == ParquetControlPlane.PostgreSql) _postgres = new PostgreSqlProviderContainer(controlPlaneOnly: true);
        else _sqlite = new SqliteControlPlane(sqliteDirectory);
    }

    public ParquetControlPlane ControlPlane { get; }

    public override string ProviderName => "Parquet";
    public override string ImageName => "(local Parquet files)";
    public override string ConnectionString => _root!;
    protected override string DefaultControlPlaneConnectionString => _postgres?.ConnectionString ?? _sqlite!.ConnectionString;
    protected override string DefaultControlPlaneProviderName => _postgres?.ProviderName ?? SqliteControlPlane.ProviderName;

    /// <summary>The PostgreSQL control plane's container; empty for SQLite, which has none (and the files have none of their own).</summary>
    public override string ContainerId => _postgres?.ContainerId ?? string.Empty;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        if (_postgres is not null) await _postgres.StartAsync(ContainerOptions.Default, cancellationToken);
        else await _sqlite!.StartAsync(cancellationToken);
        _root = Path.Combine(Path.GetTempPath(), "keryhe-parquet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    protected override Task ApplySchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task<(string Stdout, string Stderr)> ReadLogsAsync(DateTime since, CancellationToken cancellationToken)
        => Task.FromResult((string.Empty, string.Empty));

    protected override Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
        => _postgres is not null
            ? _postgres.InsertTenantWithHashedKeyAsync(tenantName, apiKeyName, keyHash, expiresAtUtc, cancellationToken)
            : _sqlite!.InsertTenantWithHashedKeyAsync(tenantName, apiKeyName, keyHash, expiresAtUtc, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        if (_sqlite is not null) await _sqlite.DisposeAsync();
        if (_root is not null)
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
