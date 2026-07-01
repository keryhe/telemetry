namespace Keryhe.Telemetry.Api;

/// <summary>
/// Configuration for the Keryhe Telemetry API library. Bound by the host from
/// configuration and/or overridden via the <c>configure</c> delegate on
/// <c>AddKeryheTelemetryApi</c>.
/// </summary>
public sealed class TelemetryApiOptions
{
    /// <summary>
    /// The database provider whose read services are registered.
    /// Expected values: <c>SqlServer</c>, <c>PostgreSQL</c>, or <c>Timescale</c>.
    /// </summary>
    public string? Provider { get; set; }
}
