using Keryhe.Telemetry.TestInfrastructure.Containers;

namespace Keryhe.Telemetry.StressTests.Observers.Database;

public static class DatabaseObserverFactory
{
    /// <summary>Creates the observer for a <c>Database:Provider</c> value over the container's own connection string and logs.</summary>
    public static DatabaseObserverBase Create(string provider, ProviderContainer container) =>
        Create(provider, container.ConnectionString, container.GetLogsAsync);

    public static DatabaseObserverBase Create(string provider, string connectionString, Func<DateTime, CancellationToken, Task<string>>? readLogs = null) => provider switch
    {
        "PostgreSQL" => new PostgresObserver(connectionString, timescale: false, readLogs),
        "Timescale" => new PostgresObserver(connectionString, timescale: true, readLogs),
        "SqlServer" => new SqlServerObserver(connectionString, readLogs),
        "MySql" => new MySqlObserver(connectionString, readLogs),
        "ClickHouse" => new ClickHouseObserver(connectionString, readLogs),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider.")
    };
}
