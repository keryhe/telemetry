namespace Keryhe.Telemetry.Admin;

/// <summary>
/// The two values this tool needs: which provider to talk to, and how. <c>Provider</c> comes from
/// <c>appsettings.json</c> (<c>Database:Provider</c>); <c>ConnectionString</c> comes from this
/// project's own User Secrets store (<c>ConnectionStrings:Admin</c>) or an environment variable
/// (<c>ConnectionStrings__Admin</c>) — never from a committed file. See plans/admin-tui.md, section 3.
/// </summary>
public sealed class AdminOptions
{
    public string? Provider { get; set; }
    public string? ConnectionString { get; set; }
}

/// <summary>Providers this tool can administer. See plans/admin-tui.md, section 3.3.</summary>
public enum AdminProvider
{
    PostgreSql,
    Timescale,
    SqlServer
}
