using System.Globalization;
using System.Text;

namespace Keryhe.Telemetry.TestDataGenerator.Flows;

/// <summary>Text helpers: logger categories, plausible stack traces, status reasons and SQL.</summary>
public static class Names
{
    /// <summary><c>order-service</c> becomes <c>Shop.OrderService</c>.</summary>
    public static string Category(string service)
    {
        var sb = new StringBuilder("Shop.");
        foreach (var part in service.Split('-'))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        return sb.ToString();
    }

    public static string Reason(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => status >= 500 ? "Server Error" : "Error",
    };

    public static string Sql(string operation, string table) => operation switch
    {
        "SELECT" => $"SELECT * FROM {table} WHERE id = $1",
        "INSERT" => $"INSERT INTO {table} (id, created_at) VALUES ($1, $2)",
        "UPDATE" => $"UPDATE {table} SET updated_at = $2 WHERE id = $1",
        "DELETE" => $"DELETE FROM {table} WHERE id = $1",
        _ => $"{operation} {table}",
    };

    /// <summary>A short, .NET-shaped stack trace in the service's own namespace.</summary>
    public static string StackTrace(string service, string exceptionType)
    {
        var ns = Category(service);
        var shortType = exceptionType[(exceptionType.LastIndexOf('.') + 1)..];
        var file = ns.Replace('.', '/');
        return string.Create(CultureInfo.InvariantCulture,
            $"{exceptionType}: {shortType}\n" +
            $"   at {ns}.Data.Repository.ExecuteAsync(CancellationToken ct) in /src/{file}/Data/Repository.cs:line 88\n" +
            $"   at {ns}.Handlers.RequestHandler.HandleAsync(HttpContext context) in /src/{file}/Handlers/RequestHandler.cs:line 41\n" +
            $"   at Microsoft.AspNetCore.Routing.EndpointMiddleware.<Invoke>g__AwaitRequestTask|7_0(Endpoint endpoint, Task requestTask, ILogger logger)\n" +
            $"   at Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddlewareImpl.Invoke(HttpContext context)");
    }
}
