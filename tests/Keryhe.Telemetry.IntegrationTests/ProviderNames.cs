namespace Keryhe.Telemetry.IntegrationTests;

/// <summary>
/// The <c>Database:Provider</c> config values every host switches on (see CLAUDE.md's Provider
/// abstraction section), reused here as xUnit collection names and <c>[Trait("Provider", …)]</c>
/// values so <c>dotnet test --filter Provider=SqlServer</c> works.
/// </summary>
public static class ProviderNames
{
    public const string PostgreSql = "PostgreSQL";
    public const string Timescale = "Timescale";
    public const string SqlServer = "SqlServer";
    public const string MySql = "MySql";
    public const string ClickHouse = "ClickHouse";
}
