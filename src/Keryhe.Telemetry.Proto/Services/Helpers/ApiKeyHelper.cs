using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;

namespace Keryhe.Telemetry.Proto.Services.Helpers;

public class ApiKeyHelper
{
    private static readonly string AuthorizationHeaderName = "authorization";
    private static readonly string BearerPrefix = "Bearer ";

    public static string GetKeyHash(ServerCallContext context)
    {
        var authorizationHeader = context.RequestHeaders.FirstOrDefault(e => e.Key.Equals(AuthorizationHeaderName, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing or invalid Authorization header."));
        }

        var apiKey = authorizationHeader[BearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "API key is missing."));
        }

        var keyHash = ComputeApiKeyHash(apiKey);
        return keyHash ?? "";
    }

    private static string ComputeApiKeyHash(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
