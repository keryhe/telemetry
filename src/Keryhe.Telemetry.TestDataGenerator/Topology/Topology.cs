namespace Keryhe.Telemetry.TestDataGenerator.Topology;

/// <summary>A backing system a service talks to (not instrumented itself, so it has no resource of its own).</summary>
public sealed record Dependency(string PeerService, string System, string Host, int Port);

public static class Services
{
    public const string Frontend = "web-frontend";
    public const string Gateway = "api-gateway";
    public const string User = "user-service";
    public const string Catalog = "catalog-service";
    public const string Cart = "cart-service";
    public const string Order = "order-service";
    public const string Inventory = "inventory-service";
    public const string Payment = "payment-service";
    public const string Notification = "notification-service";

    /// <summary>Every service and the version it runs when no incident says otherwise.</summary>
    public static readonly IReadOnlyDictionary<string, string> BaselineVersions = new Dictionary<string, string>
    {
        [Frontend] = "3.4.1",
        [Gateway] = "2.9.0",
        [User] = "1.12.3",
        [Catalog] = "2.7.4",
        [Cart] = "1.5.2",
        [Order] = "4.1.0",
        [Inventory] = "2.3.8",
        [Payment] = "3.0.2",
        [Notification] = "1.8.0",
    };

    public static IEnumerable<string> All => BaselineVersions.Keys;

    /// <summary>Services that hold a PostgreSQL connection pool (and so report <c>db.client.connection.count</c>).</summary>
    public static readonly IReadOnlySet<string> UsingPostgres = new HashSet<string> { User, Catalog, Order, Inventory, Payment, Notification };
}

public static class Dependencies
{
    public static readonly Dependency Postgres = new("shop-db", "postgresql", "shop-db.internal", 5432);
    public static readonly Dependency Redis = new("redis", "redis", "shop-cache.internal", 6379);
    public static readonly Dependency Queue = new("rabbitmq", "rabbitmq", "rabbitmq.internal", 5672);
    public static readonly Dependency Stripe = new("stripe", "http", "api.stripe.com", 443);
    public static readonly Dependency Email = new("sendgrid", "http", "api.sendgrid.com", 443);
}
