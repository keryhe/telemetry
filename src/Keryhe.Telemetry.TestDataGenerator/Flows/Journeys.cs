using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;
using static Keryhe.Telemetry.TestDataGenerator.Topology.Services;

namespace Keryhe.Telemetry.TestDataGenerator.Flows;

/// <summary>
/// The user journeys of the simulated shop. Each builds one trace (plus, for checkout, the consumer trace
/// it triggers) through the browser-facing frontend, the gateway and the backing services.
/// </summary>
public static class Journeys
{
    private static readonly string[] UserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15",
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36",
    ];

    public static readonly IReadOnlyList<(string Name, double Weight, Action<FlowTrace, DateTimeOffset> Run)> All =
    [
        ("home", 22, Home),
        ("product", 30, Product),
        ("search", 18, Search),
        ("add-to-cart", 10, AddToCart),
        ("login", 7, Login),
        ("checkout", 5, Checkout),
        ("order-status", 8, OrderStatus),
    ];

    // ---------------------------------------------------------------- entry points

    private static void Home(FlowTrace tr, DateTimeOffset start) =>
        Entry(tr, start, "GET", "/", "/", authed: false, f =>
        {
            f.Work(3);
            f.Parallel(
                b => Gateway(b, authed: false, "GET", "/api/catalog/products", "/api/catalog/products?featured=true", CatalogList),
                b => Gateway(b, authed: false, "GET", "/api/catalog/categories", "/api/catalog/categories", CatalogCategories));
            if (f.Failed) return;
            f.Work(7);
        });

    private static void Product(FlowTrace tr, DateTimeOffset start)
    {
        var id = ProductId(tr.Rng);
        var authed = tr.Rng.Chance(0.4);
        Entry(tr, start, "GET", "/products/{id}", $"/products/{id}", authed, f =>
        {
            f.Work(3);
            f.Parallel(
                b => Gateway(b, authed: false, "GET", "/api/catalog/products/{id}", $"/api/catalog/products/{id}", c => CatalogProduct(c, id)),
                b =>
                {
                    if (authed) Gateway(b, authed: true, "GET", "/api/cart", "/api/cart", CartGet);
                });
            if (f.Failed) return;
            f.Work(9);
        });
    }

    private static void Search(FlowTrace tr, DateTimeOffset start)
    {
        var term = tr.Rng.Pick("headphones", "backpack", "desk lamp", "water bottle", "keyboard", "notebook", "sneakers");
        Entry(tr, start, "GET", "/search", $"/search?q={Uri.EscapeDataString(term)}", authed: false, f =>
        {
            f.Work(3);
            Gateway(f, authed: false, "GET", "/api/catalog/search", $"/api/catalog/search?q={Uri.EscapeDataString(term)}", c => CatalogSearch(c, term));
            if (f.Failed) return;
            f.Work(6);
        });
    }

    private static void AddToCart(FlowTrace tr, DateTimeOffset start)
    {
        var id = ProductId(tr.Rng);
        Entry(tr, start, "POST", "/cart/items", "/cart/items", authed: true, f =>
        {
            f.Work(2);
            Gateway(f, authed: true, "POST", "/api/cart/items", "/api/cart/items", c => CartAdd(c, id));
        });
    }

    private static void Login(FlowTrace tr, DateTimeOffset start) =>
        Entry(tr, start, "POST", "/login", "/login", authed: false, f =>
        {
            f.Work(2);
            Gateway(f, authed: false, "POST", "/api/auth/login", "/api/auth/login", UserLogin);
        });

    private static void Checkout(FlowTrace tr, DateTimeOffset start) =>
        Entry(tr, start, "POST", "/checkout", "/checkout", authed: true, f =>
        {
            f.Work(4);
            Gateway(f, authed: true, "POST", "/api/orders", "/api/orders", OrderCreate);
        });

    private static void OrderStatus(FlowTrace tr, DateTimeOffset start)
    {
        var orderId = 100_000 + tr.Rng.Next(900_000);
        Entry(tr, start, "GET", "/orders/{id}", $"/orders/{orderId}", authed: true, f =>
        {
            f.Work(3);
            Gateway(f, authed: true, "GET", "/api/orders/{id}", $"/api/orders/{orderId}", o => OrderGet(o, orderId));
            if (f.Failed) return;
            f.Work(6);
        });
    }

    // ---------------------------------------------------------------- frontend and gateway

    private static void Entry(FlowTrace tr, DateTimeOffset start, string method, string route, string path, bool authed, Action<SpanScope> body)
    {
        var rng = tr.Rng;
        var extra = new Tags { { "user_agent.original", rng.Pick(UserAgents) } };
        if (authed) extra.Add("enduser.id", $"user_{rng.Next(1000, 9000)}");
        Flow.Server(tr, null, tr.Env.Instances.Pick(Services.Frontend, start, rng), method, route, path, start, body, extra);
    }


    /// <summary>The frontend's call to the gateway, with the gateway's session check and rate limit in front of <paramref name="upstream"/>.</summary>
    private static void Gateway(SpanScope from, bool authed, string method, string route, string path, Action<SpanScope> upstream)
    {
        var status = from.Call(Services.Gateway, method, route, g =>
        {
            g.Work(1.2);
            g.Cache("INCR", "ratelimit:client:?", 0.6);
            if (authed)
            {
                var hit = g.Rng.Chance(0.85);
                g.Cache("GET", "session:?", 0.6);
                if (!hit)
                {
                    var validate = g.Call(User, "GET", "/users/validate-token", ValidateToken);
                    if (g.Failed) return;
                    if (validate == 401) { g.Respond(401); return; }
                    g.Cache("SET", "session:?", 0.7);
                }
            }
            if (g.Failed) return;
            upstream(g);
            if (g.Failed)
            {
                g.Log(SimSeverity.Warn, "Upstream request failed", new Tags { { "http.route", route } });
            }
        }, path, tolerant: true);

        // The frontend answers the user with the gateway's verdict; a 5xx renders an error page.
        if (status >= 500)
            from.Fail("System.Net.Http.HttpRequestException", $"The gateway returned {status} ({Names.Reason(status)}).");
        else if (status >= 400)
            from.Respond(status);
    }

    /// <summary>Calls <paramref name="service"/> from inside the gateway and passes a 4xx back to the caller.</summary>
    private static void Proxy(SpanScope g, string service, string method, string route, string path, Action<SpanScope> body)
    {
        var status = g.Call(service, method, route, body, path);
        if (!g.Failed && status >= 400) g.Respond(status);
    }

    // ---------------------------------------------------------------- user-service

    private static void ValidateToken(SpanScope s)
    {
        s.Work(1.5);
        s.Db("SELECT", "sessions", 2.5, "SELECT user_id, expires_at FROM sessions WHERE token_hash = $1");
        if (s.Failed) return;
        if (s.Rng.Chance(0.005)) s.Respond(401);
    }

    private static void UserLogin(SpanScope g)
    {
        Proxy(g, User, "POST", "/login", "/login", s =>
        {
            s.Work(2);
            var found = s.Db("SELECT", "users", 3.2, "SELECT id, password_hash FROM users WHERE email = $1");
            if (s.Failed) return;
            s.Work(70, 0.2); // password hash verification
            if (s.Rng.Chance(0.05))
            {
                s.Log(SimSeverity.Warn, "Failed sign-in attempt for user***@example.com",
                    new Tags { { "event.name", "auth.login_failed" } });
                s.Respond(401);
                return;
            }
            s.Db("INSERT", "sessions", 3.0);
            if (s.Failed) return;
            s.Cache("SET", "session:?", 0.7);
            s.Log(SimSeverity.Info, $"User user_{s.Rng.Next(1000, 9000)} signed in",
                new Tags { { "event.name", "auth.login" } });
            s.Respond(200);
        });
    }

    // ---------------------------------------------------------------- catalog-service

    private static void CatalogList(SpanScope g) =>
        Proxy(g, Catalog, "GET", "/products", "/products?featured=true", s =>
        {
            s.Work(2);
            CachedRead(s, "products:featured", () => s.Db("SELECT", "products", 9, "SELECT id, name, price FROM products WHERE featured ORDER BY rank LIMIT 24"), 0.8);
            if (s.Failed) return;
            s.Work(1.5);
        });

    private static void CatalogCategories(SpanScope g) =>
        Proxy(g, Catalog, "GET", "/categories", "/categories", s =>
        {
            s.Work(1.5);
            CachedRead(s, "categories:all", () => s.Db("SELECT", "categories", 3), 0.95);
        });

    private static void CatalogProduct(SpanScope g, int id) =>
        Proxy(g, Catalog, "GET", "/products/{id}", $"/products/{id}", s =>
        {
            s.Work(1.8);
            if (s.Rng.Chance(0.005)) { s.Db("SELECT", "products", 2.5); s.Respond(404); return; }
            CachedRead(s, $"product:{id}", () => s.Db("SELECT", "products", 4, "SELECT * FROM products WHERE id = $1"), 0.7);
            if (s.Failed) return;
            s.Work(1);
        });

    private static void CatalogSearch(SpanScope g, string term) =>
        Proxy(g, Catalog, "GET", "/search", $"/search?q={Uri.EscapeDataString(term)}", s =>
        {
            s.Work(2.5);
            CachedRead(s, $"search:{term}", () => s.Db("SELECT", "products", 38,
                "SELECT id, name, price FROM products WHERE name ILIKE '%' || $1 || '%' ORDER BY rank LIMIT 48"), 0.3);
            if (s.Failed) return;
            s.Work(3);
        });

    /// <summary>Redis read-through: a hit costs one GET; a miss runs <paramref name="load"/> and writes the result back.</summary>
    private static void CachedRead(SpanScope s, string key, Func<bool> load, double hitRate)
    {
        var cacheKey = key.Length > 24 ? key[..24] : key;
        var cacheOk = s.Cache("GET", cacheKey, 0.6);
        if (cacheOk && s.Rng.Chance(hitRate)) return;
        s.Log(SimSeverity.Debug, $"Cache miss for key {cacheKey}", new Tags { { "cache.key", cacheKey } });
        if (!load()) return;
        s.Cache("SET", cacheKey, 0.8);
    }

    // ---------------------------------------------------------------- cart-service

    private static void CartGet(SpanScope g) =>
        Proxy(g, Cart, "GET", "/cart", "/cart", s =>
        {
            s.Work(1.2);
            s.Cache("HGETALL", "cart:?", 0.7, propagate: true);
        });

    private static void CartAdd(SpanScope g, int productId) =>
        Proxy(g, Cart, "POST", "/cart/items", "/cart/items", s =>
        {
            s.Work(1.5);
            var status = s.Call(Catalog, "GET", "/products/{id}", c => { c.Work(1.5); c.Cache("GET", $"product:{productId}", 0.6); c.Work(0.8); }, $"/products/{productId}");
            if (s.Failed) return;
            if (status >= 400) { s.Respond(status); return; }
            s.Cache("HSET", "cart:?", 0.8, propagate: true);
            if (s.Failed) return;
            s.Log(SimSeverity.Info, $"Added SKU SKU-{productId:D5} to cart", new Tags { { "event.name", "cart.item_added" }, { "product.id", productId } });
            s.Respond(201);
        });

    // ---------------------------------------------------------------- order, inventory, payment

    private static void OrderGet(SpanScope g, int orderId) =>
        Proxy(g, Order, "GET", "/orders/{id}", $"/orders/{orderId}", s =>
        {
            s.Work(2);
            s.Db("SELECT", "orders", 3.5, "SELECT * FROM orders WHERE id = $1");
            if (s.Failed) return;
            s.Db("SELECT", "order_items", 3.0, "SELECT * FROM order_items WHERE order_id = $1");
            if (s.Failed) return;
            if (s.Rng.Chance(0.01)) { s.Respond(404); return; }
            s.Work(1.5);
        });

    private static void OrderCreate(SpanScope g) =>
        Proxy(g, Order, "POST", "/orders", "/orders", s =>
        {
            var rng = s.Rng;
            var orderId = 100_000 + rng.Next(900_000);
            var customer = $"cust_{rng.Next(1000, 9000)}";
            var items = rng.Next(1, 6);
            var total = Math.Round(rng.Uniform(12, 480), 2);

            s.Work(3);
            s.Call(Cart, "GET", "/cart", c => { c.Work(1.2); c.Cache("HGETALL", "cart:?", 0.7, propagate: true); });
            if (s.Failed) return;

            var reserveStatus = 0;
            s.Parallel(
                b => reserveStatus = b.Call(Inventory, "POST", "/reservations", Reserve),
                b => b.Call(User, "GET", "/users/{id}/address", u => { u.Work(1.2); u.Db("SELECT", "addresses", 2.2, propagate: true); }, "/users/1234/address"));
            if (s.Failed) return;
            if (reserveStatus == 409)
            {
                s.Log(SimSeverity.Warn, $"Order {orderId} rejected: item out of stock", new Tags { { "order.id", orderId }, { "event.name", "order.out_of_stock" } });
                s.Respond(409);
                return;
            }

            s.Work(2);
            var payStatus = s.Call(Payment, "POST", "/payments", p => Pay(p, orderId, total), tolerant: false);
            if (s.Failed) return;
            if (payStatus == 402)
            {
                // Compensate: give the stock back.
                s.Call(Inventory, "DELETE", "/reservations/{id}", r => { r.Work(1.2); r.Db("DELETE", "reservations", 3.0); }, $"/reservations/{orderId}", tolerant: true);
                s.Log(SimSeverity.Warn, $"Order {orderId} payment declined", new Tags { { "order.id", orderId }, { "event.name", "order.payment_declined" } });
                s.Respond(402);
                return;
            }

            s.Db("INSERT", "orders", 5.5, "INSERT INTO orders (id, customer_id, total, status) VALUES ($1, $2, $3, 'placed')");
            if (s.Failed) return;
            s.Db("INSERT", "order_items", 4.5, "INSERT INTO order_items (order_id, sku, quantity) SELECT $1, sku, quantity FROM UNNEST($2, $3)");
            if (s.Failed) return;

            var published = s.Publish("orders", $"msg-{orderId}");
            s.Measure(Instruments.OrdersPlaced, 1, new Tags { { "shop.order.items", items } });
            s.Log(SimSeverity.Info, $"Order {orderId} created for customer {customer} with {items} items totaling {total:0.00} USD",
                new Tags { { "order.id", orderId }, { "customer.id", customer }, { "order.total", total }, { "event.name", "order.created" } });

            s.Call(Cart, "DELETE", "/cart", c => { c.Work(1.0); c.Cache("DEL", "cart:?", 0.6); }, tolerant: true);
            s.Work(1.5);
            s.Respond(201);

            ConsumeOrderConfirmation(s.Trace.NewTrace(), published, orderId, customer);
        });

    private static void Reserve(SpanScope s)
    {
        s.Work(2);
        var qty = s.Rng.Next(1, 4);
        s.Db("SELECT", "inventory", 5.5, "SELECT quantity FROM inventory WHERE sku = $1 FOR UPDATE", extraErrorRate: 0.004);
        if (s.Failed) return;
        if (s.Rng.Chance(0.03))
        {
            s.Log(SimSeverity.Warn, "Insufficient stock to reserve requested quantity", new Tags { { "event.name", "inventory.insufficient" } });
            s.Respond(409);
            return;
        }
        s.Db("UPDATE", "inventory", 4.5, "UPDATE inventory SET quantity = quantity - $2 WHERE sku = $1", extraErrorRate: 0.004);
        if (s.Failed) return;
        s.Db("INSERT", "reservations", 3.0);
        if (s.Failed) return;
        s.Log(SimSeverity.Info, $"Reserved {qty} units of SKU SKU-{s.Rng.Next(1, 2000):D5}", new Tags { { "event.name", "inventory.reserved" } });
        s.Respond(201);
    }

    private static void Pay(SpanScope s, int orderId, double total)
    {
        var rng = s.Rng;
        s.Work(3);
        var attempt = 1;
        var status = s.External(Dependencies.Stripe, "POST", "/v1/payment_intents", 210, 0.006);
        while (status is 0 or >= 500 && attempt < 2)
        {
            attempt++;
            s.Log(SimSeverity.Warn, $"Stripe request failed ({(status == 0 ? "timeout" : status.ToString())}), retrying (attempt {attempt})",
                new Tags { { "order.id", orderId }, { "event.name", "payment.retry" } });
            s.Wait(rng.Uniform(100, 300));
            status = s.External(Dependencies.Stripe, "POST", "/v1/payment_intents", 210, 0.006);
        }

        if (status is 0 or >= 500)
        {
            s.Measure(Instruments.PaymentsProcessed, 1, new Tags { { "shop.payment.result", "error" } });
            s.Fail("System.Net.Http.HttpRequestException", status == 0
                ? "The request was canceled due to the configured HttpClient.Timeout of 3 seconds elapsing."
                : $"Response status code does not indicate success: {status} ({Names.Reason(status)}).", 502);
            return;
        }

        if (rng.Chance(0.06))
        {
            var reason = rng.Pick("card_declined", "insufficient_funds", "expired_card");
            s.Measure(Instruments.PaymentsProcessed, 1, new Tags { { "shop.payment.result", "declined" } });
            s.Log(SimSeverity.Warn, $"Payment declined for order {orderId}: {reason}",
                new Tags { { "order.id", orderId }, { "payment.decline_reason", reason }, { "event.name", "payment.declined" } });
            s.Respond(402);
            return;
        }

        s.Db("INSERT", "payments", 4.0, "INSERT INTO payments (id, order_id, amount, status) VALUES ($1, $2, $3, 'captured')");
        if (s.Failed) return;
        s.Measure(Instruments.PaymentsProcessed, 1, new Tags { { "shop.payment.result", "approved" } });
        s.Log(SimSeverity.Info, $"Payment approved for order {orderId} ({total:0.00} USD)",
            new Tags { { "order.id", orderId }, { "event.name", "payment.approved" } });
        s.Respond(201);
    }

    // ---------------------------------------------------------------- notification-service consumer

    private static void ConsumeOrderConfirmation(FlowTrace tr, SimSpan producer, int orderId, string customer)
    {
        var rng = tr.Rng;
        var delivered = producer.End + SpanScope.Ms(Math.Min(3000, rng.LogNormal(120, 0.7)) + 15);
        var instance = tr.Env.Instances.Pick(Notification, delivered, rng);
        var scope = Flow.Consumer(tr, producer, instance, "orders", delivered, c =>
        {
            c.Work(2);
            c.Db("SELECT", "notification_preferences", 2.5, "SELECT email_enabled FROM notification_preferences WHERE customer_id = $1");
            if (c.Failed) return;
            c.Work(4);
            var status = c.External(Dependencies.Email, "POST", "/v3/mail/send", 170, 0.015);
            if (status is 0 or >= 500)
            {
                var (type, message, stack) = c.RecordException("System.Net.Http.HttpRequestException", status == 0
                    ? "The request was canceled due to the configured HttpClient.Timeout."
                    : $"Response status code does not indicate success: {status} ({Names.Reason(status)}).");
                c.Log(SimSeverity.Error, $"Failed to send confirmation email for order {orderId}",
                    new Tags { { "order.id", orderId }, { "exception.type", type }, { "exception.message", message }, { "exception.stacktrace", stack } },
                    "Shop.NotificationService.Consumers.OrderConsumer");
                c.Failed = true;
                return;
            }
            c.Db("INSERT", "notifications", 3.0);
            if (c.Failed) return;
            c.Log(SimSeverity.Info, $"Sent order confirmation for order {orderId} to customer {customer}",
                new Tags { { "order.id", orderId }, { "event.name", "notification.sent" } },
                "Shop.NotificationService.Consumers.OrderConsumer");
        });
        _ = scope;
    }

    // ---------------------------------------------------------------- helpers

    private static int ProductId(SimRandom rng) => 1 + (int)Math.Min(1999, rng.LogNormal(60, 1.1));
}
