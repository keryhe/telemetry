using System.Security.Cryptography;
using Keryhe.Telemetry.TestInfrastructure.Containers;

namespace Keryhe.Telemetry.TestInfrastructure.Seeding;

public static class TenantSeeder
{
    /// <summary>
    /// Seeds <paramref name="count"/> tenants, one API key each, and returns the plaintext keys.
    /// Keys are random per call (the database only stores hashes), so a caller must hold on to
    /// the returned list.
    /// </summary>
    public static async Task<IReadOnlyList<SeededTenant>> SeedAsync(
        ProviderContainer container,
        int count,
        string namePrefix = "stress-tenant",
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        var tenants = new List<SeededTenant>(count);
        for (var i = 1; i <= count; i++)
        {
            var apiKey = $"{namePrefix}-key-{i}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";
            tenants.Add(await container.SeedTenantAsync($"{namePrefix}-{i}", $"{namePrefix}-key-{i}", apiKey, cancellationToken));
        }
        return tenants;
    }
}
