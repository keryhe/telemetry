using Keryhe.Telemetry.TestDataGenerator.Clock;
using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;

namespace Keryhe.Telemetry.TestDataGenerator.Flows;

/// <summary>
/// The open span a flow is currently building, plus a cursor (<see cref="Now"/>) that moves forward as the
/// span does work or waits for a call. Child spans start at the cursor and end before the parent does, so
/// durations nest by construction; <see cref="Parallel"/> runs branches from the same instant.
/// </summary>
public sealed class SpanScope
{
    internal SpanScope(FlowTrace trace, SimSpan span, DateTimeOffset now, string route, IncidentEffect effect)
    {
        Trace = trace;
        Span = span;
        Now = now;
        Route = route;
        Effect = effect;
    }

    public FlowTrace Trace { get; }
    public SimSpan Span { get; }
    public DateTimeOffset Now { get; set; }
    public string Route { get; }
    public IncidentEffect Effect { get; }

    /// <summary>An unhandled failure has occurred; later steps should stop and the span ends as a 5xx / error.</summary>
    public bool Failed { get; set; }

    /// <summary>The HTTP status to answer with; 0 means "decide at the end" (200, or 500 when failed).</summary>
    public int Status { get; set; }

    public SimRandom Rng => Trace.Rng;
    public string Service => Span.Instance.Service;

    // ---------------------------------------------------------------- time

    public static TimeSpan Ms(double ms) => TimeSpan.FromTicks((long)(ms * TimeSpan.TicksPerMillisecond));

    /// <summary>Spend log-normally distributed CPU time around <paramref name="medianMs"/>, stretched by any incident.</summary>
    public void Work(double medianMs, double sigma = 0.35) =>
        Now += Ms(Math.Max(0.05, Rng.LogNormal(medianMs * Effect.LatencyMultiplier, sigma)));

    /// <summary>Wait without incident scaling (network, fixed delays).</summary>
    public void Wait(double ms) => Now += Ms(ms);

    // ---------------------------------------------------------------- results

    public void Respond(int status) => Status = status;

    public void Measure(string instrument, double value, Tags attrs) =>
        Span.Measurements.Add(new SimMeasurement { Instrument = instrument, Value = value, Attributes = attrs });

    public void Log(SimSeverity severity, string body, Tags? attrs = null, string? category = null) =>
        Span.Logs.Add(new SimLog
        {
            Time = Now,
            Severity = severity,
            Body = body,
            Instance = Span.Instance,
            Category = category ?? Names.Category(Service),
            Attributes = attrs ?? [],
            Span = Span,
        });

    /// <summary>Adds the OTel <c>exception</c> event and returns the exception details for logging.</summary>
    public (string Type, string Message, string StackTrace) RecordException(string type, string message)
    {
        var stack = Names.StackTrace(Service, type);
        Span.Events.Add(new SimEvent
        {
            Time = Now,
            Name = "exception",
            Attributes = new Tags
            {
                { "exception.type", type },
                { "exception.message", message },
                { "exception.stacktrace", stack },
            },
        });
        return (type, message, stack);
    }

    /// <summary>An unhandled exception: records it, logs it the way the host would, and marks the scope failed.</summary>
    public void Fail(string exceptionType, string message, int status = 500)
    {
        if (Failed) return;
        var (type, msg, stack) = RecordException(exceptionType, message);
        Log(SimSeverity.Error, "An unhandled exception has occurred while executing the request.",
            new Tags { { "exception.type", type }, { "exception.message", msg }, { "exception.stacktrace", stack } },
            "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        Failed = true;
        if (Status == 0) Status = status;
    }

    // ---------------------------------------------------------------- parallelism

    /// <summary>Runs branches that all start now; the cursor moves to the end of the slowest.</summary>
    public void Parallel(params Action<SpanScope>[] branches)
    {
        var start = Now;
        var end = Now;
        foreach (var run in branches)
        {
            var branch = new SpanScope(Trace, Span, start, Route, Effect);
            run(branch);
            if (branch.Now > end) end = branch.Now;
            if (branch.Failed) Failed = true;
        }
        Now = end;
    }

    // ---------------------------------------------------------------- service-to-service HTTP

    /// <summary>
    /// A synchronous HTTP call to another service: a Client span here and a Server span there, parented to it.
    /// Returns the response status. A 5xx makes this scope fail unless <paramref name="tolerant"/>; a 4xx is
    /// returned for the caller to handle.
    /// </summary>
    public int Call(string callee, string method, string route, Action<SpanScope> body, string? path = null, bool tolerant = false)
    {
        path ??= route;
        var client = NewChild($"{method} {route}", SimSpanKind.Client, Now);
        client.Attributes.Add("http.request.method", method);
        client.Attributes.Add("server.address", $"{callee}.shop.svc");
        client.Attributes.Add("server.port", 8080);
        client.Attributes.Add("url.full", $"http://{callee}.shop.svc:8080{path}");
        client.Attributes.Add("peer.service", callee);
        client.Attributes.Add("network.protocol.version", "1.1");

        var net = Ms(Rng.LogNormal(0.8, 0.4));
        var calleeInstance = Trace.Env.Instances.Pick(callee, Now, Rng);
        var server = Flow.Server(Trace, client, calleeInstance, method, route, path, Now + net, body);
        client.End = server.Span.End + net;
        Now = client.End;

        var status = server.StatusCode;
        client.Attributes.Add("http.response.status_code", status);
        var attrs = new Tags
        {
            { "http.request.method", method },
            { "server.address", $"{callee}.shop.svc" },
            { "server.port", 8080 },
            { "http.response.status_code", status },
        };
        if (status >= 400)
        {
            client.Status = SimStatus.Error;
            client.Attributes.Add("error.type", status.ToString());
            attrs.Add("error.type", status.ToString());
        }
        Measure(Instruments.HttpClientDuration, (client.End - client.Start).TotalSeconds, attrs, client);

        if (status >= 500 && !tolerant)
        {
            Fail("System.Net.Http.HttpRequestException",
                $"Response status code does not indicate success: {status} ({Names.Reason(status)}).",
                Service == Services.Gateway ? 502 : 500);
        }
        return status;
    }

    // ---------------------------------------------------------------- backing systems

    private const double DbErrorRate = 0.001;
    private const double CacheErrorRate = 0.0015;

    /// <summary>A PostgreSQL call. Returns false (and fails the scope when <paramref name="propagate"/>) on error.</summary>
    public bool Db(string operation, string table, double medianMs, string? statement = null, double extraErrorRate = 0, bool propagate = true)
    {
        var dep = Dependencies.Postgres;
        var span = NewChild($"{operation} {table}", SimSpanKind.Client, Now);
        span.Attributes.Add("db.system.name", "postgresql");
        span.Attributes.Add("db.namespace", "shop");
        span.Attributes.Add("db.operation.name", operation);
        span.Attributes.Add("db.collection.name", table);
        span.Attributes.Add("db.query.text", statement ?? Names.Sql(operation, table));
        span.Attributes.Add("server.address", dep.Host);
        span.Attributes.Add("server.port", dep.Port);
        span.Attributes.Add("peer.service", dep.PeerService);

        var failed = Rng.Chance(DbErrorRate + extraErrorRate);
        var ms = Math.Max(0.2, Rng.LogNormal(medianMs * Effect.LatencyMultiplier, 0.5)) * (failed ? Rng.Uniform(2, 6) : 1);
        span.End = span.Start + Ms(ms);
        Now = span.End;

        var attrs = new Tags
        {
            { "db.system.name", "postgresql" },
            { "db.operation.name", operation },
            { "db.collection.name", table },
        };
        if (failed)
        {
            var (type, message) = Rng.Pick(
                ("Npgsql.PostgresException", "40P01: deadlock detected"),
                ("Npgsql.NpgsqlException", "Exception while reading from stream"),
                ("System.TimeoutException", "Timeout during reading attempt"));
            MarkLeafError(span, attrs, type, message);
            Log(SimSeverity.Error, $"Failed executing DbCommand ({ms:0}ms) [{operation} {table}]",
                new Tags { { "db.system.name", "postgresql" }, { "exception.type", type }, { "exception.message", message } },
                "Microsoft.EntityFrameworkCore.Database.Command");
            if (propagate) Fail(type, message);
        }
        else if (ms > 250)
        {
            Log(SimSeverity.Warn, $"Executed DbCommand ({ms:0}ms) [{operation} {table}] exceeded the slow-query threshold",
                new Tags { { "db.system.name", "postgresql" }, { "db.operation.name", operation } },
                "Microsoft.EntityFrameworkCore.Database.Command");
        }
        Measure(Instruments.DbClientDuration, (span.End - span.Start).TotalSeconds, attrs, span);
        return !failed;
    }

    /// <summary>A Redis call. <paramref name="hit"/> is reported through the span's <c>db.response.returned_rows</c>.</summary>
    public bool Cache(string operation, string key, double medianMs = 0.6, bool propagate = false)
    {
        var dep = Dependencies.Redis;
        var span = NewChild(operation, SimSpanKind.Client, Now);
        span.Attributes.Add("db.system.name", "redis");
        span.Attributes.Add("db.operation.name", operation);
        span.Attributes.Add("db.query.text", $"{operation} {key}");
        span.Attributes.Add("server.address", dep.Host);
        span.Attributes.Add("server.port", dep.Port);
        span.Attributes.Add("peer.service", dep.PeerService);

        var failed = Rng.Chance(CacheErrorRate);
        var ms = Math.Max(0.1, Rng.LogNormal(medianMs, 0.5)) * (failed ? Rng.Uniform(10, 40) : 1);
        span.End = span.Start + Ms(ms);
        Now = span.End;

        var attrs = new Tags { { "db.system.name", "redis" }, { "db.operation.name", operation } };
        if (failed)
        {
            const string type = "StackExchange.Redis.RedisTimeoutException";
            const string message = "Timeout awaiting response (outbound=0KiB, inbound=0KiB, 5000ms elapsed)";
            MarkLeafError(span, attrs, type, message);
            Log(SimSeverity.Warn, $"Redis {operation} timed out", new Tags { { "exception.type", type } });
            if (propagate) Fail(type, message);
        }
        Measure(Instruments.DbClientDuration, (span.End - span.Start).TotalSeconds, attrs, span);
        return !failed;
    }

    /// <summary>
    /// An HTTP call to an external API. Returns the status; 0 means the call failed without a response (a timeout).
    /// </summary>
    public int External(Dependency dep, string method, string path, double medianMs, double failureRate, string? declineRoll = null)
    {
        var span = NewChild($"{method} {dep.Host}", SimSpanKind.Client, Now);
        span.Attributes.Add("http.request.method", method);
        span.Attributes.Add("server.address", dep.Host);
        span.Attributes.Add("server.port", dep.Port);
        span.Attributes.Add("url.full", $"https://{dep.Host}{path}");
        span.Attributes.Add("peer.service", dep.PeerService);
        span.Attributes.Add("network.protocol.version", "2");

        var status = 200;
        var timedOut = false;
        if (Rng.Chance(failureRate))
        {
            if (Rng.Chance(0.5)) timedOut = true; else status = 503;
        }
        var ms = Math.Max(5, Rng.LogNormal(medianMs * Effect.LatencyMultiplier, 0.4));
        if (timedOut) ms = Rng.Uniform(2500, 4000);
        span.End = span.Start + Ms(ms);
        Now = span.End;

        var attrs = new Tags
        {
            { "http.request.method", method },
            { "server.address", dep.Host },
            { "server.port", dep.Port },
        };
        if (timedOut)
        {
            MarkLeafError(span, attrs, "System.Threading.Tasks.TaskCanceledException", "The request was canceled due to the configured HttpClient.Timeout.");
            Measure(Instruments.HttpClientDuration, (span.End - span.Start).TotalSeconds, attrs, span);
            return 0;
        }
        span.Attributes.Add("http.response.status_code", status);
        attrs.Add("http.response.status_code", status);
        if (status >= 400)
        {
            span.Status = SimStatus.Error;
            span.Attributes.Add("error.type", status.ToString());
            attrs.Add("error.type", status.ToString());
        }
        Measure(Instruments.HttpClientDuration, (span.End - span.Start).TotalSeconds, attrs, span);
        return status;
    }

    /// <summary>Publishes a message; returns the Producer span so a consumer trace can link to it.</summary>
    public SimSpan Publish(string queue, string messageId)
    {
        var dep = Dependencies.Queue;
        var span = NewChild($"{queue} publish", SimSpanKind.Producer, Now);
        span.Attributes.Add("messaging.system", dep.System);
        span.Attributes.Add("messaging.destination.name", queue);
        span.Attributes.Add("messaging.operation.type", "publish");
        span.Attributes.Add("messaging.message.id", messageId);
        span.Attributes.Add("server.address", dep.Host);
        span.Attributes.Add("server.port", dep.Port);
        span.Attributes.Add("peer.service", dep.PeerService);
        span.End = span.Start + Ms(Math.Max(0.3, Rng.LogNormal(2.5 * Effect.LatencyMultiplier, 0.4)));
        Now = span.End;
        Measure(Instruments.MessagingPublishDuration, (span.End - span.Start).TotalSeconds, new Tags
        {
            { "messaging.system", dep.System },
            { "messaging.operation.name", "publish" },
            { "messaging.destination.name", queue },
        }, span);
        return span;
    }

    // ---------------------------------------------------------------- internals

    private SimSpan NewChild(string name, SimSpanKind kind, DateTimeOffset start)
    {
        var span = new SimSpan
        {
            TraceId = Trace.TraceId,
            SpanId = Rng.SpanId(),
            ParentSpanId = Span.SpanId,
            Instance = Span.Instance,
            Name = name,
            Kind = kind,
            Start = start,
        };
        Span.Children.Add(span);
        return span;
    }

    private void Measure(string instrument, double value, Tags attrs, SimSpan on) =>
        on.Measurements.Add(new SimMeasurement { Instrument = instrument, Value = value, Attributes = attrs });

    private static void MarkLeafError(SimSpan span, Tags metricAttrs, string type, string message)
    {
        span.Status = SimStatus.Error;
        span.StatusMessage = message;
        span.Attributes.Add("error.type", type);
        metricAttrs.Add("error.type", type);
        span.Events.Add(new SimEvent
        {
            Time = span.End,
            Name = "exception",
            Attributes = new Tags
            {
                { "exception.type", type },
                { "exception.message", message },
                { "exception.stacktrace", Names.StackTrace(span.Instance.Service, type) },
            },
        });
    }
}
