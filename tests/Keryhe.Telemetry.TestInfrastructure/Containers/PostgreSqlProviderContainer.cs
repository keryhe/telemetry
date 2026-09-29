namespace Keryhe.Telemetry.TestInfrastructure.Containers;

public sealed class PostgreSqlProviderContainer : PostgresFamilyContainer
{
    public override string ProviderName => "PostgreSQL";
    protected override string Image => "postgres:16-alpine";
    protected override string SchemaFileName => "PostgreSQL-Schema.sql";
    protected override string DiagnosticPreloadLibraries => "pg_stat_statements";
}
