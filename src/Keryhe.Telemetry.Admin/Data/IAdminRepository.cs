namespace Keryhe.Telemetry.Admin.Data;

/// <summary>
/// The six control-plane operations this tool performs, plus a startup connectivity check and a
/// safe-to-display description of the connection target. See plans/admin-tui.md, section 5.
/// </summary>
public interface IAdminRepository
{
    /// <summary>Host/database only — never credentials. Shown in the banner (section 6.1).</summary>
    (string Server, string Database) DescribeTarget();

    /// <summary>
    /// "Created (UTC)" for providers that return UTC-kind timestamps, plain "Created" for ones
    /// that don't (SQL Server's SYSDATETIME() default is server-local, Kind = Unspecified). See
    /// plans/admin-tui.md, section 5.4 — never converted, only labeled.
    /// </summary>
    string CreatedAtColumnHeader { get; }

    Task CheckConnectivityAsync(CancellationToken ct);

    Task<IReadOnlyList<TenantRow>> GetTenantsAsync(CancellationToken ct);

    /// <summary>Throws <see cref="UniqueConstraintViolationException"/> if the name is already taken.</summary>
    Task<long> CreateTenantAsync(string name, CancellationToken ct);

    Task<IReadOnlyList<ApiKeyRow>> GetApiKeysAsync(long tenantId, CancellationToken ct);

    Task<long> CreateApiKeyAsync(long tenantId, string name, string keyHash, CancellationToken ct);

    Task<bool> SetApiKeyActiveAsync(long apiKeyId, bool active, CancellationToken ct);

    Task<bool> DeleteApiKeyAsync(long apiKeyId, CancellationToken ct);
}
