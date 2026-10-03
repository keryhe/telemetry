using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Keryhe.Telemetry.Admin.Security;

/// <summary>
/// DELIBERATE DUPLICATE of the hash computation in
/// <c>Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash</c>. This project
/// is intentionally self-contained and references nothing, so the algorithm is copied rather than
/// shared (see plans/admin-tui.md, sections 1 and 4.1). The two MUST stay byte-identical: SHA-256
/// over the UTF-8 bytes of the key, hex-encoded, lowercased. If they diverge, keys issued here hash
/// to something the collector never matches, and the only symptom is an Unauthenticated gRPC status
/// at ingestion time — far from the tool that caused it. If you ever change one, change both.
/// </summary>
public static class ApiKeyHashing
{
    public const string KeyPrefix = "ktel_";

    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="apiKey"/>, as 64 lowercase hex chars.</summary>
    public static string ComputeHash(string apiKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a new plaintext API key: <c>ktel_</c> + 32 random bytes base64url-encoded with padding
    /// stripped (43 characters). The prefix exists so leaked keys are findable by a secret scanner
    /// (<c>ktel_[A-Za-z0-9_-]{43}</c>); the collector does not require it, so un-prefixed keys issued
    /// earlier (e.g. the TestDataGenerator's) keep working. Collector-authentication plan, decision 13.
    /// </summary>
    public static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return KeyPrefix + Base64Url.EncodeToString(bytes);
    }
}
