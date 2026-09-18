using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Core.Data;

/// <summary>
/// Provider-agnostic helpers shared by the per-provider bulk writers: resource/scope
/// normalization, deterministic hashing for deduplication, and JSON serialization of
/// attribute bags. These are identical across providers and must stay in lockstep so
/// the <c>resource_hash</c> / <c>scope_hash</c> dedup columns line up across databases.
/// </summary>
public static class TelemetryIngestionHelpers
{
    /// <summary>
    /// Every table the metric retention sweep prunes: the five data-point tables, each on its own
    /// <c>time_unix_nano</c>. Kept in one place so all five providers agree -- a table added later
    /// without updating five separate copies is exactly how orphaned rows appear.
    ///
    /// Exemplars need no entry of their own. Since schema 2.9.0 they live in an
    /// <c>exemplars_json</c> column on the data point that owns them rather than in a separate,
    /// soft-referenced <c>exemplars</c> table, so pruning the data point takes its exemplars with
    /// it and there is nothing left to orphan.
    /// </summary>
    public static readonly string[] TimePrunedMetricTables =
    [
        "gauge_data_points",
        "sum_data_points",
        "histogram_data_points",
        "exponential_histogram_data_points",
        "summary_data_points"
    ];

    /// <summary>
    /// Fills in the defaults a resource needs before it can be hashed or stored.
    ///
    /// The null branch is a last-resort guard, not a supported path. It runs inside the bulk writer,
    /// which has no idea who authenticated, so it can only fall back to tenant 1 — filing the export
    /// under the wrong owner. The gRPC services therefore never hand a null resource down: they
    /// synthesize the fallback themselves, where the authenticated tenant is still in scope. Reaching
    /// this branch means the tenant was lost upstream.
    /// </summary>
    public static ResourceModel NormalizeResource(ResourceModel? model)
    {
        if (model == null)
            return new ResourceModel { Attributes = new() { { "service.name", "unknown" } } };
        if (model.TenantId <= 0)
            model.TenantId = ResourceModel.DefaultTenantId;
        return model;
    }

    public static InstrumentationScopeModel NormalizeScope(InstrumentationScopeModel? model)
        => model ?? new InstrumentationScopeModel { Name = "unknown" };

    /// <summary>
    /// Hashes a resource's schema URL + attributes. Memoized on the instance
    /// (<see cref="ResourceModel.CachedHash"/>): a bulk writer hashes the same model twice per
    /// row (once resolving the batch's distinct resources, once per row rebuilding the key), and
    /// the gRPC services hand down one shared instance per <c>ResourceLogs</c>/<c>ResourceSpans</c>
    /// block, so the second call on any given instance is a field read, not a re-hash. The cache
    /// only ever holds this exact algorithm's output, so it cannot make a resource hash diverge
    /// from what it would have been uncached.
    /// </summary>
    public static string HashResource(ResourceModel model)
    {
        if (model.CachedHash != null) return model.CachedHash;
        var content = $"{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        model.CachedHash = hash;
        return hash;
    }

    /// <summary>Memoized the same way as <see cref="HashResource"/> -- see its doc comment.</summary>
    public static string HashScope(InstrumentationScopeModel model)
    {
        if (model.CachedHash != null) return model.CachedHash;
        var content = $"{model.Name}__{model.Version ?? ""}__{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        model.CachedHash = hash;
        return hash;
    }

    /// <summary>
    /// Canonical in-process key for a metric's identity, matching the <c>uk_metric_identity</c>
    /// UNIQUE constraint on the metrics table.
    ///
    /// Deliberately NOT hashed, unlike <see cref="HashResource"/> / <see cref="HashScope"/>:
    /// those dedup on an unbounded attribute map and so need a hash column to constrain, while a
    /// metric is identified by four bounded scalar columns that already exist on the row. There is
    /// no metric_hash column for this to line up with.
    ///
    /// Injective because resourceId and scopeId are digits-only and cannot themselves contain the
    /// "__" separator, so a left-to-right scan parses unambiguously even when the metric name
    /// contains "__".
    ///
    /// ClickHouse feeds this same string to ClickHouseIds.FromKey to derive the row's deterministic
    /// Int64 surrogate key, so the cache key and the ClickHouse id can never drift apart.
    /// </summary>
    /// <summary>
    /// Canonical in-process key for a resource's identity, matching the
    /// <c>uk_resource_tenant_hash UNIQUE (tenant_id, resource_hash)</c> constraint every schema declares.
    ///
    /// The tenant is deliberately NOT folded into <see cref="HashResource"/>. That hash describes the
    /// resource itself and is stored in the <c>resource_hash</c> column; the database models the tenant
    /// as a separate column of a composite key, and this key does the same. Hashing the tenant in would
    /// change every existing <c>resource_hash</c> on every provider and duplicate every stored resource
    /// row for no gain.
    ///
    /// Everything that identifies a resource in memory MUST go through here. A bare hash is not an
    /// identity: two tenants running the same service with the same attributes share one, and treating
    /// that as the same resource files one tenant's telemetry under the other.
    ///
    /// Injective because tenantId is digits and the hash is 64 hex chars — neither can contain "__".
    /// ClickHouse feeds this same string to ClickHouseIds.FromKey for the row's surrogate id, so the
    /// cache key and the ClickHouse id can never disagree about what a resource is.
    /// </summary>
    public static string ResourceKey(long tenantId, string resourceHash)
        => $"{tenantId}__{resourceHash}";

    /// <summary>Resource key straight from a model — normalizes, hashes and tenant-qualifies in one step.</summary>
    public static string ResourceKey(ResourceModel? model)
    {
        var normalized = NormalizeResource(model);
        return ResourceKey(normalized.TenantId, HashResource(normalized));
    }

    public static string MetricKey(long resourceId, long scopeId, string name, string type)
        => $"{resourceId}__{scopeId}__{name}__{type}";

    public static string SerializeDeterministicJson(Dictionary<string, object>? attributes)
    {
        // SortedDictionary keeps the same Ordinal key order as the OrderBy+ToDictionary this
        // replaced -- same enumeration order in, same JSON out -- without LINQ's intermediate
        // allocations for every hash computed.
        var ordered = new SortedDictionary<string, object>(attributes ?? new Dictionary<string, object>(), StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    public static string? SerializeJsonOrNull(object? value)
        => value == null ? null : JsonSerializer.Serialize(value);

    /// <summary>
    /// Serializes a child collection to JSON, or to <c>null</c> when it is absent or empty.
    ///
    /// This is the null-for-empty convention exemplars already use (<c>MetricService.ConvertExemplars</c>
    /// returns null rather than an empty list) and that spans' <c>events_json</c>/<c>links_json</c>
    /// columns follow since schema 2.11.0: the overwhelming majority of spans carry no events and no
    /// links, and writing the literal string <c>"[]"</c> on every one of those rows would cost real
    /// bytes on the largest table in the system for no information at all.
    /// </summary>
    public static string? SerializeListOrNull<T>(List<T>? items)
        => items is { Count: > 0 } ? JsonSerializer.Serialize(items) : null;
}
