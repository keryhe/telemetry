using System.Collections.Concurrent;

namespace Keryhe.Telemetry.Data;

/// <summary>
/// Process-lifetime identity cache for the deduplicated reference entities: resources (keyed by
/// <see cref="TelemetryIngestionHelpers.ResourceKey"/>), instrumentation scopes (by their SHA-256
/// hash) and metrics (by <see cref="TelemetryIngestionHelpers.MetricKey"/>). A warm process
/// resolves all three without a database round trip.
///
/// Every key here mirrors the uniqueness constraint the database enforces for that entity, and the
/// asymmetry between resources and scopes is deliberate rather than an oversight:
///
/// Resources are keyed by (tenant, hash), matching
/// <c>uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash)</c>. The tenant is not optional. Two
/// tenants running a service with the same name and attributes produce the same hash, so caching on
/// the hash alone would hand the second tenant the first tenant's <c>resources.id</c> — and since
/// spans, metrics and log records reach their owner only through <c>resource_id</c>, all of the
/// second tenant's telemetry would be stored as, and readable by, the first. The methods take the
/// tenant as a parameter precisely so that cannot be forgotten.
///
/// Scopes are keyed by hash alone, matching <c>uk_scope_hash UNIQUE (scope_hash)</c> — a scope is an
/// instrumentation library (name, version), not tenant data, and sharing one row across tenants is
/// intended. Do NOT "fix" this into a per-tenant key; it would fragment the scope table for nothing.
///
/// Entries are never evicted and need no invalidation: nothing deletes a resource, scope or
/// metrics catalog row. <c>ITelemetryWriteStore</c> offers retention only, and its metric sweep
/// prunes data-point rows while deliberately leaving the catalog intact, so a cached id cannot go
/// stale.
///
/// That is a precondition, not a permanent property. If a delete that removes catalog rows is ever
/// reintroduced, it MUST clear the affected entries here: a cached id whose row has been deleted
/// would be handed to the next flush, every data point insert would fail its foreign key, and
/// <c>TelemetryIngestionWorker</c> drops the whole batch on any exception -- so ingestion would
/// fail identically on every subsequent batch until the process restarted.
/// </summary>
public sealed class ResourceScopeCache
{
    private readonly ConcurrentDictionary<string, long> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _scopes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _metrics = new(StringComparer.Ordinal);

    public bool TryGetResource(long tenantId, string hash, out long id)
        => _resources.TryGetValue(TelemetryIngestionHelpers.ResourceKey(tenantId, hash), out id);

    public void SetResource(long tenantId, string hash, long id)
        => _resources.TryAdd(TelemetryIngestionHelpers.ResourceKey(tenantId, hash), id);

    public bool TryGetScope(string hash, out long id) => _scopes.TryGetValue(hash, out id);
    public void SetScope(string hash, long id) => _scopes.TryAdd(hash, id);

    public bool TryGetMetric(string key, out long id) => _metrics.TryGetValue(key, out id);
    public void SetMetric(string key, long id) => _metrics.TryAdd(key, id);
}
