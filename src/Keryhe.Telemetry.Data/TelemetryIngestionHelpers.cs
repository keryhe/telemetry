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
