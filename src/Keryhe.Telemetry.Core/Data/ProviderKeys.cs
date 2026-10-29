namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// The configuration keys each host selects its two providers with: the telemetry database (<c>PostgreSQL</c>, <c>SqlServer</c>,
/// <c>MySql</c>, <c>ClickHouse</c>, <c>Parquet</c>) and the control plane (<c>PostgreSQL</c>, <c>SqlServer</c>, <c>MySql</c>,
/// <c>Sqlite</c>). Each host has its own pair under its own section, so the two can sit in one configuration source.
/// </summary>
public static class ProviderKeys
{
    /// <summary>The API host's telemetry database provider.</summary>
    public const string ApiDatabase = "Telemetry:Api:Provider:Database";

    /// <summary>The API host's control-plane provider.</summary>
    public const string ApiControlPlane = "Telemetry:Api:Provider:ControlPlane";

    /// <summary>The collector host's telemetry database provider.</summary>
    public const string CollectorDatabase = "Telemetry:Collector:Provider:Database";

    /// <summary>The collector host's control-plane provider.</summary>
    public const string CollectorControlPlane = "Telemetry:Collector:Provider:ControlPlane";
}
