namespace Keryhe.Telemetry.TestInfrastructure.Containers;

public sealed class TimescaleProviderContainer : PostgresFamilyContainer
{
    public override string ProviderName => "Timescale";
    protected override string Image => "timescale/timescaledb:latest-pg16";
    protected override string SchemaFileName => "Timescale-Schema.sql";
    protected override string DiagnosticPreloadLibraries => "timescaledb,pg_stat_statements";
}
