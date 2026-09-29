namespace Keryhe.Telemetry.TestInfrastructure.Seeding;

/// <summary>A seeded tenant and the plaintext of its one API key (the database only holds the hash).</summary>
public sealed record SeededTenant(long Id, string Name, string ApiKeyName, string ApiKey);
