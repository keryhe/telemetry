using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core;

// =============================================================================
// RETENTION SETTINGS REPOSITORY INTERFACE
// =============================================================================

/// <summary>
/// Owns the DB-backed retention policy (<see cref="RetentionSettings"/>): the single, global
/// settings row in the control plane (<c>ConnectionStrings:ControlPlane</c>), alongside alert-rule
/// CRUD (<see cref="IAlertRuleRepository"/>). The sweeps that enforce the policy are
/// <see cref="IRetentionSweeper"/>, which runs against the telemetry database: <c>RetentionWorker</c>
/// (in <c>Keryhe.Telemetry.Api</c>) reads the windows here and hands them to the sweeper, and the
/// settings API resolves this same scoped instance.
/// </summary>
public interface IRetentionSettingsRepository
{
    /// <summary>Reads the single, global retention settings row.</summary>
    Task<RetentionSettings> GetSettingsAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the single, global retention settings row. Always an <c>UPDATE</c>, never an
    /// <c>INSERT</c> — the row is seeded by the schema install, so two concurrent saves race to
    /// overwrite the same row rather than risk creating a second one.
    /// </summary>
    Task UpdateSettingsAsync(RetentionSettings settings, CancellationToken ct = default);
}
