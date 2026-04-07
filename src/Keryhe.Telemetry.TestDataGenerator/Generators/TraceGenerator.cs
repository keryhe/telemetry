using System.Diagnostics;
using Keryhe.Telemetry.Core.Models;

namespace Keryhe.Telemetry.TestDataGenerator.Generators;

/// <summary>
/// Generates diverse trace patterns including different span kinds, status codes, events, and links.
/// </summary>
public class TraceGenerator
{
    private readonly ActivitySource _activitySource;
    private readonly Random _random;
    private static readonly string[] OperationNames = new[]
    {
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
    };

    private static readonly string[] ErrorMessages = new[]
    {
        "Database connection timeout",
        "Invalid API response",
        "Authentication failed",
        "Resource not found",
        "Rate limit exceeded",
        "Parsing error",
        "Validation failed",
        "Permission denied"
    };

    public TraceGenerator(ActivitySource activitySource)
    {
        _activitySource = activitySource;
        _random = new Random();
    }

    /// <summary>
    /// Generate a batch of spans with diverse patterns.
    /// </summary>
    public void GenerateBatch(int spanCount)
    {
        for (int i = 0; i < spanCount; i++)
        {
            GenerateSpan();
        }
    }

    private void GenerateSpan()
    {
        var kind = (ActivityKind)(_random.Next(0, 6)); // 0=Unspecified, 1=Internal, 2=Server, 3=Client, 4=Producer, 5=Consumer
        var operationName = OperationNames[_random.Next(OperationNames.Length)];
        var shouldError = _random.Next(0, 10) < 2; // 20% error rate

        using (var activity = _activitySource.StartActivity(operationName, kind))
        {
            if (activity == null) return;

            // Add random attributes
            activity.SetTag("service.component", "test-generator");
            activity.SetTag("environment", "testing");
            activity.SetTag("user.id", $"user_{_random.Next(100, 200)}");
            activity.SetTag("request.path", $"/api/endpoint/{_random.Next(1, 50)}");
            activity.SetTag("http.method", new[] { "GET", "POST", "PUT", "DELETE" }[_random.Next(4)]);

            // Simulate processing
            var processingMs = _random.Next(10, 500);
            Thread.Sleep(processingMs);

            // Add span events
            if (_random.Next(0, 10) < 3) // 30% chance of an event
            {
                activity.AddEvent(new ActivityEvent("cache_miss"));
            }

            if (_random.Next(0, 10) < 4) // 40% chance of another event
            {
                activity.AddEvent(new ActivityEvent("network_call", tags: new ActivityTagsCollection
                {
                    { "network.peer.address", "10.0.0.1" },
                    { "network.protocol.version", "1.1" }
                }));
            }

            // Set status
            if (shouldError)
            {
                var errorMsg = ErrorMessages[_random.Next(ErrorMessages.Length)];
                activity.SetStatus(ActivityStatusCode.Error, errorMsg);
                activity.SetTag("error.type", "Exception");
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }

            // Create child span for some operations (simulating call hierarchy)
            if (_random.Next(0, 10) < 5) // 50% chance
            {
                CreateChildSpan(activity);
            }
        }
    }

    private void CreateChildSpan(Activity parentActivity)
    {
        var childOp = OperationNames[_random.Next(OperationNames.Length)];
        
        using (var childActivity = _activitySource.StartActivity(childOp, ActivityKind.Internal, parentActivity.Context))
        {
            if (childActivity == null) return;

            childActivity.SetTag("span.level", "child");
            childActivity.SetTag("database.operation", new[] { "SELECT", "INSERT", "UPDATE", "DELETE" }[_random.Next(4)]);
            childActivity.SetTag("database.rows_affected", _random.Next(1, 1000));

            Thread.Sleep(_random.Next(5, 200));

            // Occasional child errors
            if (_random.Next(0, 100) < 10)
            {
                childActivity.SetStatus(ActivityStatusCode.Error, "Query execution failed");
            }
            else
            {
                childActivity.SetStatus(ActivityStatusCode.Ok);
            }
        }
    }
}
