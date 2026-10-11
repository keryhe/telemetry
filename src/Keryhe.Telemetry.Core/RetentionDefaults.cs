namespace Keryhe.Telemetry.Core;

/// <summary>
/// The built-in retention windows, in days: what telemetry expires on when its tenant has no row in the
/// control plane (a deleted tenant, data written before the row existed, a different control-plane
/// database). A new tenant gets the same values from the <c>tenants</c> column defaults in the three
/// <c>schema/*-ControlPlane.sql</c> scripts; a test keeps the two in step.
/// </summary>
public static class RetentionDefaults
{
    public const int TraceDays = 30;
    public const int LogDays = 30;
    public const int MetricDays = 30;
}
