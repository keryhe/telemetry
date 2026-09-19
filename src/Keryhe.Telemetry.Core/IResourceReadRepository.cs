namespace Keryhe.Telemetry.Core;

// =============================================================================
// RESOURCE READ REPOSITORY INTERFACE
// =============================================================================

/// <summary>
/// Signal-agnostic reads over <c>resources</c> directly, not joined through any signal table.
/// </summary>
public interface IResourceReadRepository
{
    /// <summary>All distinct <c>service.name</c> values for the current tenant's resources, regardless of signal type or time range.</summary>
    Task<List<string>> GetDistinctServicesAsync(CancellationToken cancellationToken = default);
}
