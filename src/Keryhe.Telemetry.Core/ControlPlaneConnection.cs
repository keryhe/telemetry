using Microsoft.Extensions.Configuration;

namespace Keryhe.Telemetry.Core;

/// <summary>
/// The control-plane connection string (<c>ConnectionStrings:ControlPlane</c>), read once at
/// registration so a missing value fails at startup with the key named rather than on first use.
/// Each provider's <c>Add&lt;Provider&gt;ControlPlane*Services</c> registers one as a singleton and its
/// control-plane repositories take it from DI.
/// </summary>
public sealed class ControlPlaneConnection
{
    public const string ConnectionStringName = "ControlPlane";

    public ControlPlaneConnection(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    /// <summary>Reads <c>ConnectionStrings:ControlPlane</c>; throws, naming the key, when it is missing or blank.</summary>
    public static ControlPlaneConnection FromConfiguration(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "Missing required configuration ConnectionStrings:ControlPlane (the database holding tenants, API keys, alert rules and retention settings).");
        return new ControlPlaneConnection(value);
    }
}
