namespace Keryhe.Telemetry.Sqlite;

/// <summary>Options of the SQLite control plane on both hosts (configuration section <c>Telemetry:ControlPlane:Sqlite</c>).</summary>
public sealed class SqliteControlPlaneOptions
{
    public const string SectionName = "Telemetry:ControlPlane:Sqlite";

    /// <summary>
    /// True (the default): the host (collector or API) creates the database and applies any migrations at startup. False: it only
    /// checks, for an operator who wants to create or migrate deliberately (for example after taking their own backup, with
    /// <c>schema/apply-schema.sh</c> or <see cref="SqliteControlPlaneSchema.EnsureCurrentAsync"/>); it then creates no tenant either.
    /// </summary>
    public bool ApplySchema { get; set; } = true;
}
