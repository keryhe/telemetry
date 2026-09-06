using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.Data;

/// <summary>
/// Provider-agnostic helpers shared by the per-provider bulk writers: resource/scope
/// normalization, deterministic hashing for deduplication, and JSON serialization of
/// attribute bags. These are identical across providers and must stay in lockstep so
/// the <c>resource_hash</c> / <c>scope_hash</c> dedup columns line up across databases.
/// </summary>
public static class TelemetryIngestionHelpers
{
    /// <summary>
    /// Every table the metric retention sweep prunes: the five data-point tables plus
    /// <c>exemplars</c>, each on its own <c>time_unix_nano</c>. Kept in one place so all five
    /// providers agree -- a sixth table added later without updating five separate copies is
    /// exactly how orphaned rows appear.
    ///
    /// <c>exemplars</c> belongs here even though it is not a data-point table. It is only
    /// soft-referenced (data points carry an <c>exemplar_id</c> with no foreign key), so no
    /// cascade or metric-scoped delete has ever been able to reach it -- but it carries its own
    /// timestamp, so retention can prune it, and must, or it grows without bound while everything
    /// around it is trimmed.
    /// </summary>
    public static readonly string[] TimePrunedMetricTables =
    [
        "gauge_data_points",
        "sum_data_points",
        "histogram_data_points",
        "exponential_histogram_data_points",
        "summary_data_points",
        "exemplars"
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

    public static string HashResource(ResourceModel model)
    {
        var content = $"{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    public static string HashScope(InstrumentationScopeModel model)
    {
        var content = $"{model.Name}__{model.Version ?? ""}__{model.SchemaUrl ?? ""}__{SerializeDeterministicJson(model.Attributes)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
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
        var ordered = (attributes ?? new Dictionary<string, object>())
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonSerializer.Serialize(ordered);
    }

    public static string? SerializeJsonOrNull(object? value)
        => value == null ? null : JsonSerializer.Serialize(value);
}
