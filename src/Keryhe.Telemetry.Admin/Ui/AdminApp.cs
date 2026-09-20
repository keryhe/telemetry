using Keryhe.Telemetry.Admin.Data;

namespace Keryhe.Telemetry.Admin.Ui;

/// <summary>Entry point into the screen loop, once configuration is validated and connectivity is
/// confirmed. See plans/admin-tui.md, section 6.</summary>
public static class AdminApp
{
    public static Task RunAsync(string providerName, IAdminRepository repository, CancellationToken ct) =>
        TenantScreens.RunAsync(providerName, repository, ct);
}
