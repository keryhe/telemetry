using System.Diagnostics;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Generates diverse trace patterns including different span kinds, status codes, events, and links.
/// Supports multi-service distributed traces by propagating <see cref="ActivityContext"/> across
/// simulated service boundaries.
/// </summary>
public class TraceGenerator
{
    private readonly IReadOnlyList<ServiceSource> _services;
    private readonly Random _random;

    private static readonly string[] DefaultOperationNames =
    [
        "ProcessRequest",
        "QueryDatabase",
        "CallExternalAPI",
        "ValidateInput",
        "GenerateReport",
        "SendNotification",
        "AuthenticateUser",
        "CacheHit",
        "EncryptData",
        "ParseJSON"
    ];

    private static readonly string[] ErrorMessages =
    [
        "Database connection timeout",
        "Invalid API response",
        "Authentication failed",
        "Resource not found",
        "Rate limit exceeded",
        "Parsing error",
        "Validation failed",
        "Permission denied"
    ];

    public TraceGenerator(IReadOnlyList<ServiceSource> services)
    {
        _services = services;
        _random = new Random();
    }

    /// <summary>
    /// Generate a batch of traces with diverse patterns.
    /// </summary>
    public void GenerateBatch(int spanCount)
    {
        for (int i = 0; i < spanCount; i++)
        {
            GenerateTrace();
        }
    }

    private void GenerateTrace()
    {
        var rootCandidates = _services.Where(s => s.Definition.CanBeRootService).ToList();
        var rootService = rootCandidates[_random.Next(rootCandidates.Count)];

        var operationName = PickOperation(rootService);
        var shouldError = _random.Next(0, 10) < 2; // 20% error rate

        using var rootSpan = rootService.ActivitySource.StartActivity(operationName, ActivityKind.Server);
        if (rootSpan == null) return;

        AddCommonTags(rootSpan, rootService.Definition.Name);

        var processingMs = _random.Next(10, 200);
        Thread.Sleep(processingMs);

        AddRandomEvents(rootSpan);

        if (shouldError)
        {
            var errorMsg = ErrorMessages[_random.Next(ErrorMessages.Length)];
            rootSpan.SetStatus(ActivityStatusCode.Error, errorMsg);
            rootSpan.SetTag("error.type", "Exception");
        }
        else
        {
            rootSpan.SetStatus(ActivityStatusCode.Ok);
        }

        // Chain downstream service calls (50% chance when multiple services exist)
        if (_services.Count > 1 && _random.Next(0, 10) < 5)
        {
            var downstream = PickDifferentService(rootService);
            CreateDownstreamCall(rootService, rootSpan, downstream, depth: 0);
        }
        else if (_random.Next(0, 10) < 5) // single-service: fall back to internal child span
        {
            CreateInternalChildSpan(rootService, rootSpan);
        }
    }

    private void CreateDownstreamCall(ServiceSource caller, Activity callerSpan, ServiceSource callee, int depth)
    {
        // Caller emits a Client span for the outbound call
        var outboundOp = $"HTTP {callee.Definition.Name}";
        using var clientSpan = caller.ActivitySource.StartActivity(outboundOp, ActivityKind.Client, callerSpan.Context);
        if (clientSpan == null) return;

        clientSpan.SetTag("server.address", callee.Definition.Name);
        clientSpan.SetTag("http.method", new[] { "GET", "POST", "PUT", "DELETE" }[_random.Next(4)]);
        clientSpan.SetTag("url.path", $"/api/{PickOperation(callee).ToLower().Replace(" ", "-")}");

        // Callee emits a Server span, parented to the Client span
        var calleeOp = PickOperation(callee);
        using var serverSpan = callee.ActivitySource.StartActivity(calleeOp, ActivityKind.Server, clientSpan.Context);
        if (serverSpan == null) return;

        AddCommonTags(serverSpan, callee.Definition.Name);
        Thread.Sleep(_random.Next(5, 150));

        AddRandomEvents(serverSpan);

        // 10% error rate on downstream calls
        if (_random.Next(0, 100) < 10)
        {
            serverSpan.SetStatus(ActivityStatusCode.Error, "Downstream call failed");
            clientSpan.SetStatus(ActivityStatusCode.Error, "Remote error");
        }
        else
        {
            serverSpan.SetStatus(ActivityStatusCode.Ok);
            clientSpan.SetStatus(ActivityStatusCode.Ok);
        }

        // Optionally chain one more hop (max depth 2)
        if (depth < 2 && _services.Count > 1 && _random.Next(0, 10) < 4)
        {
            var next = PickDifferentService(callee);
            CreateDownstreamCall(callee, serverSpan, next, depth + 1);
        }
        else if (_random.Next(0, 10) < 4)
        {
            CreateInternalChildSpan(callee, serverSpan);
        }
    }

    private void CreateInternalChildSpan(ServiceSource service, Activity parentSpan)
    {
        var childOp = PickOperation(service);
        using var childSpan = service.ActivitySource.StartActivity(childOp, ActivityKind.Internal, parentSpan.Context);
        if (childSpan == null) return;

        childSpan.SetTag("span.level", "child");
        childSpan.SetTag("database.operation", new[] { "SELECT", "INSERT", "UPDATE", "DELETE" }[_random.Next(4)]);
        childSpan.SetTag("database.rows_affected", _random.Next(1, 1000));

        Thread.Sleep(_random.Next(5, 100));

        if (_random.Next(0, 100) < 10)
        {
            childSpan.SetStatus(ActivityStatusCode.Error, "Query execution failed");
        }
        else
        {
            childSpan.SetStatus(ActivityStatusCode.Ok);
        }
    }

    private void AddCommonTags(Activity span, string serviceName)
    {
        span.SetTag("service.component", serviceName);
        span.SetTag("environment", "testing");
        span.SetTag("user.id", $"user_{_random.Next(100, 200)}");
        span.SetTag("request.path", $"/api/endpoint/{_random.Next(1, 50)}");
        span.SetTag("http.method", new[] { "GET", "POST", "PUT", "DELETE" }[_random.Next(4)]);
    }

    private void AddRandomEvents(Activity span)
    {
        if (_random.Next(0, 10) < 3)
        {
            span.AddEvent(new ActivityEvent("cache_miss"));
        }

        if (_random.Next(0, 10) < 4)
        {
            span.AddEvent(new ActivityEvent("network_call", tags: new ActivityTagsCollection
            {
                { "network.peer.address", "10.0.0.1" },
                { "network.protocol.version", "1.1" }
            }));
        }
    }

    private string PickOperation(ServiceSource service)
    {
        var ops = service.Definition.OperationNames.Length > 0
            ? service.Definition.OperationNames
            : DefaultOperationNames;
        return ops[_random.Next(ops.Length)];
    }

    private ServiceSource PickDifferentService(ServiceSource exclude)
    {
        var candidates = _services.Where(s => s != exclude).ToList();
        return candidates.Count > 0
            ? candidates[_random.Next(candidates.Count)]
            : exclude;
    }
}
