using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector.Authentication;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates an OTLP export by its per-tenant API key (<c>Authorization: Bearer &lt;key&gt;</c>)
/// before the request reaches a gRPC service, so a bad key is rejected without deserializing the
/// protobuf body. Acts only on collector endpoints (<see cref="CollectorEndpointMetadata"/>).
///
/// The key's SHA-256 (lowercase hex) is resolved through <see cref="ITenantResolver"/>, which keeps the
/// caching, negative caching, expiry and <c>last_used_at</c> behavior. A rejection is written as a
/// trailers-only gRPC response (HTTP 200 + <c>grpc-status</c>), because a bare HTTP 401 carries no
/// message and a lookup failure must be <c>UNAVAILABLE</c> (retryable) rather than a 500/<c>UNKNOWN</c>.
///
/// The hash computation lives here; <c>ApiKeyHashing</c> in the Admin TUI and <c>ApiKeyHasher</c> in
/// the test infrastructure are deliberate copies and must stay byte-identical to
/// <see cref="ComputeKeyHash"/>.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ITenantResolver tenantResolver,
    IngestionMetrics metrics)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, loggerFactory, encoder)
{
    private const string BearerPrefix = "Bearer ";
    private const string FailureItemKey = "keryhe.telemetry.auth.failure";

    // One Warning per distinct key-hash prefix per minute; Debug otherwise.
    private static readonly ConcurrentDictionary<string, long> LastWarned = new();
    private static readonly long WarnIntervalMs = 60_000;

    private sealed record Failure(string Reason, string? KeyPrefix, Exception? Error);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<CollectorEndpointMetadata>() is null)
            return AuthenticateResult.NoResult();

        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return Reject("missing", null, null, "Missing Authorization header.");
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Reject("malformed", null, null, "Authorization header must be 'Bearer <key>'.");

        var apiKey = header[BearerPrefix.Length..].Trim();
        if (apiKey.Length == 0)
            return Reject("malformed", null, null, "API key is empty.");

        var keyHash = ComputeKeyHash(apiKey);
        var prefix = keyHash[..8];

        var resolution = await tenantResolver.ResolveAsync(keyHash, Context.RequestAborted);
        switch (resolution.Failure)
        {
            case ApiKeyFailure.None when resolution.TenantId > 0:
                var identity = new ClaimsIdentity(
                [
                    new Claim(TelemetryClaimTypes.TenantId, resolution.TenantId.ToString(CultureInfo.InvariantCulture)),
                    new Claim(TelemetryClaimTypes.ApiKeyId, resolution.ApiKeyId.ToString(CultureInfo.InvariantCulture)),
                ], Scheme.Name);
                return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
            case ApiKeyFailure.Expired:
                return Reject("expired", prefix, null, "API key has expired.");
            case ApiKeyFailure.Unavailable:
                return Reject("unavailable", prefix, resolution.Error, "API key lookup is temporarily unavailable.");
            default:
                return Reject("invalid", prefix, null, "Invalid API key.");
        }
    }

    private AuthenticateResult Reject(string reason, string? keyPrefix, Exception? error, string message)
    {
        Context.Items[FailureItemKey] = new Failure(reason, keyPrefix, error);
        // NoResult for "no usable credential at all" and Fail for a presented-but-refused key; either
        // way the policy challenges and HandleChallengeAsync writes the gRPC response.
        return reason is "missing" or "malformed"
            ? AuthenticateResult.NoResult()
            : AuthenticateResult.Fail(message);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = Context.Items[FailureItemKey] as Failure ?? new Failure("missing", null, null);
        var signal = SignalOf(Request.Path);

        metrics.RecordAuthFailure(signal, failure.Reason);
        LogFailure(signal, failure);

        var unavailable = failure.Reason == "unavailable";
        var message = failure.Reason switch
        {
            "missing" => "Unauthenticated: missing Authorization header.",
            "malformed" => "Unauthenticated: malformed Authorization header (expected 'Bearer <key>').",
            "expired" => "Unauthenticated: API key has expired.",
            "unavailable" => "API key lookup is temporarily unavailable; retry.",
            _ => "Unauthenticated: invalid API key.",
        };
        await WriteGrpcStatusAsync(unavailable ? 14 : 16, message);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteGrpcStatusAsync(7, "Permission denied.");

    // Trailers-only: HTTP 200, no body, grpc-status in the response headers.
    private Task WriteGrpcStatusAsync(int grpcStatus, string message)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/grpc";
        Response.Headers["grpc-status"] = grpcStatus.ToString(CultureInfo.InvariantCulture);
        Response.Headers["grpc-message"] = Uri.EscapeDataString(message);
        return Task.CompletedTask;
    }

    private void LogFailure(string signal, Failure f)
    {
        if (f.Error is not null)
            Logger.LogError(f.Error, "API key lookup failed for {Signal} export (key {KeyPrefix}); responding UNAVAILABLE", signal, f.KeyPrefix);

        var warn = false;
        if (f.KeyPrefix is not null)
        {
            var now = Environment.TickCount64;
            if (LastWarned.Count > 1000) LastWarned.Clear();
            warn = LastWarned.AddOrUpdate(f.KeyPrefix, now, (_, last) => now - last >= WarnIntervalMs ? now : last) == now;
        }

        if (warn)
            Logger.LogWarning("Rejected {Signal} export: {Reason} (key hash prefix {KeyPrefix})", signal, f.Reason, f.KeyPrefix);
        else
            Logger.LogDebug("Rejected {Signal} export: {Reason} (key hash prefix {KeyPrefix})", signal, f.Reason, f.KeyPrefix);
    }

    private static string SignalOf(PathString path)
    {
        var p = path.Value ?? "";
        if (p.Contains("trace", StringComparison.OrdinalIgnoreCase)) return "traces";
        if (p.Contains("logs", StringComparison.OrdinalIgnoreCase)) return "logs";
        if (p.Contains("metrics", StringComparison.OrdinalIgnoreCase)) return "metrics";
        return "unknown";
    }

    /// <summary>
    /// SHA-256 of the key's UTF-8 bytes as lowercase hex: the value stored in <c>api_keys.key_hash</c>.
    /// Copies exist in the Admin TUI (<c>ApiKeyHashing</c>) and the test infrastructure (<c>ApiKeyHasher</c>).
    /// </summary>
    public static string ComputeKeyHash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
}
