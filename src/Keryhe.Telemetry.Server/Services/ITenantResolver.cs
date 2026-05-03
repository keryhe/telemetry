using Grpc.Core;
using System.Security.Cryptography;
using System.Text;
using Keryhe.Telemetry.Data.Access;
using Microsoft.EntityFrameworkCore;

namespace Keryhe.Telemetry.Server.Services;

public interface ITenantResolver
{
    Task<long> ResolveTenantIdAsync(ServerCallContext context, CancellationToken cancellationToken);
}

internal sealed class ApiKeyTenantResolver : ITenantResolver
{
    private const string AuthorizationHeaderName = "authorization";
    private const string BearerPrefix = "Bearer ";
    private readonly TelemetryWriteDbContext _dbContext;

    public ApiKeyTenantResolver(TelemetryWriteDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<long> ResolveTenantIdAsync(ServerCallContext context, CancellationToken cancellationToken)
    {
        var authorizationHeader = context.RequestHeaders.GetValue(AuthorizationHeaderName);
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
        var tenantIdRows = await _dbContext.Database.SqlQueryRaw<long>(
                @"SELECT ""tenant_id""
                  FROM api_keys
                  WHERE ""key_hash"" = {0}
                    AND ""is_active"" = TRUE
                  LIMIT 1;",
                keyHash)
            .ToListAsync(cancellationToken);

        var tenantId = tenantIdRows.SingleOrDefault();
        if (tenantId <= 0)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid API key."));
        }

        await _dbContext.Database.ExecuteSqlRawAsync(
            @"UPDATE api_keys
              SET ""last_used_at"" = NOW()
              WHERE ""key_hash"" = {0};",
            [keyHash],
            cancellationToken);

        return tenantId;
    }

    private static string ComputeApiKeyHash(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}