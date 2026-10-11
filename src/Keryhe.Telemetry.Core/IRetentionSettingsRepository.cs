using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// RETENTION SETTINGS REPOSITORY INTERFACE
// =============================================================================

/// <summary>
/// Owns the DB-backed retention policy (<see cref="RetentionSettings"/>): each tenant's three windows,
/// stored as columns on its <c>tenants</c> row in the control plane (<c>ConnectionStrings:ControlPlane</c>),
/// alongside alert-rule CRUD (<see cref="IAlertRuleRepository"/>). There is no global default row. The
/// sweeps that enforce the policy are <see cref="IRetentionSweeper"/>, which runs against the telemetry
/// database: <c>RetentionWorker</c> (in <c>Keryhe.Telemetry.Api</c>) reads every tenant's windows with
/// <see cref="GetAllAsync"/>, builds a <see cref="RetentionPolicy"/> and hands it to the sweeper, and the
/// tenant settings API resolves this same scoped instance.
/// </summary>
public interface IRetentionSettingsRepository
{
    /// <summary>Reads one tenant's retention windows; null when the tenant has no row.</summary>
    Task<RetentionSettings?> GetSettingsAsync(long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Updates one tenant's windows (and <c>retention_updated_at</c>, written back to
    /// <paramref name="settings"/>). Always an <c>UPDATE</c>: false when the tenant has no row.
    /// </summary>
    Task<bool> UpdateSettingsAsync(long tenantId, RetentionSettings settings, CancellationToken ct = default);

    /// <summary>Every tenant's windows, keyed by tenant id, for the retention worker.</summary>
    Task<IReadOnlyDictionary<long, RetentionSettings>> GetAllAsync(CancellationToken ct = default);
}
