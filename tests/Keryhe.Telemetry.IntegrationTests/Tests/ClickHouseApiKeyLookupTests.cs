using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

[Collection(ProviderNames.ClickHouse)]
[Trait("Provider", ProviderNames.ClickHouse)]
public sealed class ClickHouseApiKeyLookupTests(ClickHouseFixture fixture) : ApiKeyLookupTestsBase(fixture)
{
    // The README's revoke SQL (ALTER ... UPDATE with mutations_sync) must be visible to the next uncached lookup: the
    // lookup reads FINAL, so a mutated row cannot linger (collector-authentication plan, decision 10).
    [Fact]
    public async Task A_revoke_through_ALTER_UPDATE_is_visible_to_the_next_lookup()
    {
        var key = $"revoke-{Guid.NewGuid():N}";
        await Fixture.SeedTenantAsync($"t-{Guid.NewGuid():N}", "k", key);
        var hash = Keryhe.Telemetry.Collector.Authentication.ApiKeyAuthenticationHandler.ComputeKeyHash(key);
        using var scope = Fixture.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<Keryhe.Telemetry.Core.IApiKeyLookup>();
        Assert.NotNull(await lookup.LookupAsync(hash, CancellationToken.None));

        await using var conn = new global::ClickHouse.Client.ADO.ClickHouseConnection(Fixture.DatabaseConnectionString);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE api_keys UPDATE is_active = 0 WHERE key_hash = '{hash}' SETTINGS mutations_sync = 1";
        await cmd.ExecuteNonQueryAsync();

        Assert.Null(await lookup.LookupAsync(hash, CancellationToken.None));
    }
}
