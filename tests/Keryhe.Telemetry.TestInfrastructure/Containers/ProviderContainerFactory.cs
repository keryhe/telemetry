namespace Keryhe.Telemetry.TestInfrastructure.Containers;

/// <summary>Creates the container for a <c>Database:Provider</c> value.</summary>
public static class ProviderContainerFactory
{
    public static readonly IReadOnlyList<string> ProviderNames = ["PostgreSQL", "SqlServer", "MySql", "ClickHouse", "Parquet"];

    /// <summary>The values of the stress harness's <c>--control-plane</c> option.</summary>
    public static readonly IReadOnlyList<string> ControlPlaneChoices = ["postgresql", "sqlite"];

    /// <summary>
    /// The container for <paramref name="provider"/>. <paramref name="controlPlane"/> (<c>postgresql</c> or <c>sqlite</c>, null = the provider's default)
    /// picks the control plane's database: Parquet defaults to a SQLite file, ClickHouse to a PostgreSQL container, and the relational providers keep
    /// theirs in their own database. <c>sqlite</c> works for every provider; <c>postgresql</c> only for ClickHouse, Parquet and PostgreSQL itself.
    /// A SQLite file lives in <paramref name="sqliteDirectory"/> (kept after the run); null uses a temp directory for Parquet and is an error elsewhere.
    /// </summary>
    public static ProviderContainer Create(string provider, string? controlPlane = null, string? sqliteDirectory = null)
    {
        var choice = ValidateControlPlane(provider, controlPlane);
        if (provider == "Parquet")
            return new ParquetProviderContainer(choice == "postgresql" ? ParquetControlPlane.PostgreSql : ParquetControlPlane.Sqlite, sqliteDirectory);

        ProviderContainer container = provider switch
        {
            "PostgreSQL" => new PostgreSqlProviderContainer(),
            "SqlServer" => new SqlServerProviderContainer(),
            "MySql" => new MySqlProviderContainer(),
            "ClickHouse" => new ClickHouseProviderContainer(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider,
                $"Unknown provider (expected {string.Join(", ", ProviderNames)}).")
        };
        if (choice == "sqlite")
        {
            if (sqliteDirectory is null)
                throw new ArgumentException($"A SQLite control plane for {provider} needs a directory to keep the file in.", nameof(sqliteDirectory));
            container.UseSqliteControlPlane(sqliteDirectory);
        }
        return container;
    }

    /// <summary>
    /// Where the control plane runs for <paramref name="provider"/> given a (validated) <c>--control-plane</c> choice: <c>sqlite</c>,
    /// <c>postgresql</c> (a PostgreSQL container, or the PostgreSQL provider's own database), or null when it is the provider's own database
    /// (SQL Server, MySQL). Parquet defaults to SQLite and ClickHouse to PostgreSQL.
    /// </summary>
    public static string? EffectiveControlPlane(string provider, string? choice) => choice ?? provider switch
    {
        "Parquet" => "sqlite",
        "ClickHouse" => "postgresql",
        "PostgreSQL" => "postgresql",
        _ => null
    };

    /// <summary>True when <paramref name="choice"/> leaves the provider on the control plane it has by default (so a run is comparable with earlier ones).</summary>
    public static bool IsDefaultControlPlane(string provider, string? choice) =>
        choice is null || EffectiveControlPlane(provider, choice) == EffectiveControlPlane(provider, null);

    /// <summary>The normalized <c>--control-plane</c> choice (null when not given); throws for an unknown value or one that does not apply to <paramref name="provider"/>.</summary>
    public static string? ValidateControlPlane(string provider, string? controlPlane)
    {
        if (controlPlane is null) return null;
        var choice = ControlPlaneChoices.FirstOrDefault(c => c.Equals(controlPlane, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown control plane '{controlPlane}' (expected {string.Join(" or ", ControlPlaneChoices)}).");
        if (choice == "postgresql" && provider is "SqlServer" or "MySql")
            throw new ArgumentException(
                $"--control-plane postgresql applies to ClickHouse, Parquet and PostgreSQL; {provider} keeps its control plane in its own database (use sqlite to move it to a file).");
        return choice;
    }
}
