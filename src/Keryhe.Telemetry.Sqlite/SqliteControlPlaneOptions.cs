namespace Keryhe.Telemetry.Sqlite;

/// <summary>Options of the API-side SQLite control plane (configuration section <c>Telemetry:ControlPlane:Sqlite</c>).</summary>
public sealed class SqliteControlPlaneOptions
{
    public const string SectionName = "Telemetry:ControlPlane:Sqlite";

    /// <summary>
    /// True (the default): the API host creates the database and applies any migrations at startup. False: it only checks, as the
    /// collector does, for an operator who wants to create or migrate deliberately (for example after taking their own backup, with
    /// <c>schema/apply-schema.sh</c> or <see cref="SqliteControlPlaneSchema.EnsureCurrentAsync"/>).
    /// </summary>
    public bool ApplySchema { get; set; } = true;
}
