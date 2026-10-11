namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>Creates the container for a <c>Database:Provider</c> value.</summary>
public static class ProviderContainerFactory
{
    public static readonly IReadOnlyList<string> ProviderNames = ["PostgreSQL", "SqlServer", "MySql", "ClickHouse", "Parquet"];

    public static ProviderContainer Create(string provider) => provider switch
    {
        "PostgreSQL" => new PostgreSqlProviderContainer(),
        "SqlServer" => new SqlServerProviderContainer(),
        "MySql" => new MySqlProviderContainer(),
        "ClickHouse" => new ClickHouseProviderContainer(),
        "Parquet" => new ParquetProviderContainer(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider,
            $"Unknown provider (expected {string.Join(", ", ProviderNames)}).")
    };
}
