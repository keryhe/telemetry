using System.Data.Common;
using Dapper;

namespace Keryhe.Telemetry.Core.Data.Read;

/// <summary>
/// Shared base for the control-plane Dapper repositories (tenants, alert rules, retention settings):
/// the per-provider connection to <c>ConnectionStrings:ControlPlane</c> and Dapper's column-name
/// mapping. Deliberately separate from <see cref="DapperReadRepository"/>, which carries the
/// telemetry-read machinery (the <c>asOf</c> pin, id parameters, dialect hooks) the control plane
/// has no use for.
/// </summary>
public abstract class ControlPlaneRepositoryBase
{
    static ControlPlaneRepositoryBase()
    {
        // Map snake_case result columns (e.g. webhook_url) onto PascalCase row DTOs.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <summary>Opens a provider-specific connection to the control-plane database.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}
