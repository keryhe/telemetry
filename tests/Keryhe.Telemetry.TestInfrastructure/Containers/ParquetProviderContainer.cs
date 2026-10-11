namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>
/// The Parquet provider has no database server: its "database" is a temp directory (<see cref="ConnectionString"/> is its
/// path, which the fixtures hand to <c>Telemetry:Parquet:RootPath</c>). Like ClickHouse it has no control plane of its own, so
/// one runs on a PostgreSQL container composed here, started and disposed with it; the tenants and keys are seeded there. The
/// root's <c>_schema.json</c> is created by the collector registration on first use, as it is in production.
/// </summary>
public sealed class ParquetProviderContainer : ProviderContainer
{
    private readonly PostgreSqlProviderContainer _controlPlane = new(controlPlaneOnly: true);
    private string? _root;

    public override string ProviderName => "Parquet";
    public override string ImageName => "(local Parquet files)";
    public override string ConnectionString => _root!;
    public override string ControlPlaneConnectionString => _controlPlane.ConnectionString;
    public override string ControlPlaneProviderName => _controlPlane.ProviderName;

    /// <summary>The control plane's container: the files have no container of their own.</summary>
    public override string ContainerId => _controlPlane.ContainerId;

    protected override async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await _controlPlane.StartAsync(ContainerOptions.Default, cancellationToken);
        _root = Path.Combine(Path.GetTempPath(), "keryhe-parquet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    protected override Task ApplySchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task ConfigureDiagnosticsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override Task<(string Stdout, string Stderr)> ReadLogsAsync(DateTime since, CancellationToken cancellationToken)
        => Task.FromResult((string.Empty, string.Empty));

    protected override Task<long> InsertTenantAndApiKeyAsync(string tenantName, string apiKeyName, string keyHash, DateTime? expiresAtUtc, CancellationToken cancellationToken)
        => _controlPlane.InsertTenantWithHashedKeyAsync(tenantName, apiKeyName, keyHash, expiresAtUtc, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        await _controlPlane.DisposeAsync();
        if (_root is not null)
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
