using System.Security.Cryptography;
using System.Text;

namespace Keryhe.Telemetry.TestInfrastructure.Seeding;

/// <summary>
/// Deliberate copy of <c>Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash</c>
/// (and the Admin TUI's <c>ApiKeyHashing</c>); the three must stay byte-identical.
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Lower-case hex SHA-256, the format <c>api_keys.key_hash</c> holds and the tenant resolver looks up.</summary>
    public static string Hash(string plainText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainText))).ToLowerInvariant();
}
