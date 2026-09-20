using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Keryhe.Telemetry.Admin.Security;

/// <summary>
/// DELIBERATE DUPLICATE of the hash computation in
/// <c>Keryhe.Telemetry.Collector.Services.Helpers.ApiKeyHelper.ComputeApiKeyHash</c>. This project
/// is intentionally self-contained and references nothing, so the algorithm is copied rather than
/// shared (see plans/admin-tui.md, sections 1 and 4.1). The two MUST stay byte-identical: SHA-256
/// over the UTF-8 bytes of the key, hex-encoded, lowercased. If they diverge, keys issued here hash
/// to something the collector never matches, and the only symptom is an Unauthenticated gRPC status
/// at ingestion time — far from the tool that caused it. If you ever change one, change both.
/// </summary>
public static class ApiKeyHashing
{
    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="apiKey"/>, as 64 lowercase hex chars.</summary>
    public static string ComputeHash(string apiKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a new plaintext API key: 32 random bytes, base64url-encoded with padding
    /// stripped (43 characters), no prefix. This matches the format already in use by
    /// src/Keryhe.Telemetry.TestDataGenerator/appsettings.json's <c>OtlpHeaders</c> value — not an
    /// invented format, see plans/admin-tui.md, section 4.2.
    /// </summary>
    public static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url.EncodeToString(bytes);
    }
}
