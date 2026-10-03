using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Keryhe.Telemetry.IntegrationTests.Tests;

/// <summary>
/// Each provider's <c>IApiKeyLookup</c> against the real <c>api_keys</c> table (collector-authentication plan, decisions 11 and 12):
/// <c>expires_at</c> comes back as the UTC instant that was stored, null stays null, and an unknown key is null. An hour either
/// side of "now" catches a local-offset conversion on a non-UTC machine. Expiry itself is judged by
/// <c>CachingTenantResolver</c> (see <c>CollectorAuthTests</c>), not by the SQL.
/// </summary>
public abstract class ApiKeyLookupTestsBase(ProviderFixture fixture)
{
    protected ProviderFixture Fixture { get; } = fixture;

    private async Task<ApiKeyLookupResult?> LookupAsync(string plainKey)
    {
        using var scope = Fixture.Services.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IApiKeyLookup>();
        return await lookup.LookupAsync(ApiKeyAuthenticationHandler.ComputeKeyHash(plainKey), CancellationToken.None);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Expires_at_round_trips_as_the_stored_utc_instant()
    {
        var now = DateTime.UtcNow;
        var never = Unique("never"); var future = Unique("future"); var past = Unique("past");
        var tNever = await Fixture.SeedTenantAsync(Unique("t"), "k", never);
        var tFuture = await Fixture.SeedTenantAsync(Unique("t"), "k", future, now.AddHours(1));
        var tPast = await Fixture.SeedTenantAsync(Unique("t"), "k", past, now.AddHours(-1));

        var a = await LookupAsync(never);
        Assert.NotNull(a);
        Assert.Equal(tNever.Id, a.TenantId);
        Assert.True(a.ApiKeyId > 0);
        Assert.Null(a.ExpiresAt);

        var b = await LookupAsync(future);
        Assert.Equal(tFuture.Id, b!.TenantId);
        Assert.InRange((b.ExpiresAt!.Value.UtcDateTime - now.AddHours(1)).TotalSeconds, -5, 5);
        Assert.Equal(TimeSpan.Zero, b.ExpiresAt.Value.Offset);

        // Returned, not filtered: the resolver decides.
        var c = await LookupAsync(past);
        Assert.Equal(tPast.Id, c!.TenantId);
        Assert.InRange((c.ExpiresAt!.Value.UtcDateTime - now.AddHours(-1)).TotalSeconds, -5, 5);
    }

    [Fact]
    public async Task An_unknown_key_is_null()
    {
        Assert.Null(await LookupAsync(Unique("not-a-key")));
    }
}
