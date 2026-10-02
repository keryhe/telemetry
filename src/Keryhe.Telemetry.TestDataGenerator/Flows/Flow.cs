using Keryhe.Telemetry.TestDataGenerator.Model;
using Keryhe.Telemetry.TestDataGenerator.Topology;

namespace Keryhe.Telemetry.TestDataGenerator.Flows;

/// <summary>The result of running a server span: the finished span and the status it answered with.</summary>
public readonly record struct ServerResult(SimSpan Span, int StatusCode);

public static class Flow
{
    /// <summary>
    /// Runs one HTTP server span (a request handled by <paramref name="instance"/>). Everything in
    /// <paramref name="body"/> happens inside it. <paramref name="parent"/> is the caller's Client span, or
    /// null for a request entering from outside, in which case the span is a trace root.
    /// </summary>
    public static ServerResult Server(
        FlowTrace trace, SimSpan? parent, ServiceInstance instance, string method, string route, string path,
        DateTimeOffset start, Action<SpanScope> body, Tags? extra = null)
    {
        var rng = trace.Rng;
        var span = new SimSpan
        {
            TraceId = trace.TraceId,
            SpanId = rng.SpanId(),
            ParentSpanId = parent?.SpanId,
            Instance = instance,
            Name = $"{method} {route}",
            Kind = SimSpanKind.Server,
            Start = start,
        };
        if (parent is null) trace.Roots.Add(span); else parent.Children.Add(span);

        span.Attributes.Add("http.request.method", method);
        span.Attributes.Add("http.route", route);
        span.Attributes.Add("url.path", path);
        span.Attributes.Add("url.scheme", "http");
        span.Attributes.Add("server.address", $"{instance.Service}.shop.svc");
        span.Attributes.Add("server.port", 8080);
        span.Attributes.Add("network.protocol.version", "1.1");
        if (extra is not null) foreach (var kv in extra) span.Attributes.Add(kv);

        var effect = trace.Env.Incidents.Effect(instance.Service, $"{method} {route}", start);
        var scope = new SpanScope(trace, span, start + SpanScope.Ms(rng.Uniform(0.1, 0.6)), $"{method} {route}", effect);

        if (rng.Chance(effect.ExtraErrorRate))
        {
            // The incident makes this endpoint fail outright, after the time a stalled call would take.
            scope.Work(40);
            scope.Fail("System.InvalidOperationException", "The service is overloaded and cannot process the request.", 503);
        }
        else
        {
            body(scope);
        }

        var status = scope.Status != 0 ? scope.Status : scope.Failed ? (instance.Service == Services.Gateway ? 502 : 500) : 200;
        span.End = scope.Now + SpanScope.Ms(rng.Uniform(0.1, 0.5));
        span.Attributes.Add("http.response.status_code", status);

        var metric = new Tags
        {
            { "http.request.method", method },
            { "http.route", route },
            { "http.response.status_code", status },
            { "url.scheme", "http" },
        };
        if (status >= 500)
        {
            span.Status = SimStatus.Error;
            span.Attributes.Add("error.type", status.ToString());
            metric.Add("error.type", status.ToString());
        }
        span.Measurements.Add(new SimMeasurement
        {
            Instrument = Instruments.HttpServerDuration,
            Value = (span.End - span.Start).TotalSeconds,
            Attributes = metric,
        });

        // The host's request-finished log line, written at the end of the request.
        var elapsedMs = (span.End - span.Start).TotalMilliseconds;
        span.Logs.Add(new SimLog
        {
            Time = span.End,
            Severity = SimSeverity.Info,
            Body = $"Request finished HTTP/1.1 {method} {path} - {status} - application/json {elapsedMs:0.0000}ms",
            Instance = instance,
            Category = "Microsoft.AspNetCore.Hosting.Diagnostics",
            Attributes = new Tags
            {
                { "http.request.method", method },
                { "url.path", path },
                { "http.response.status_code", status },
            },
            Span = span,
        });
        return new ServerResult(span, status);
    }

    /// <summary>
    /// A message consumer: the root of a new trace that links back to the producer span that sent the message.
    /// </summary>
    public static SpanScope Consumer(
        FlowTrace trace, SimSpan producer, ServiceInstance instance, string queue, DateTimeOffset start, Action<SpanScope> body)
    {
        var dep = Dependencies.Queue;
        var rng = trace.Rng;
        var route = $"{queue} process";
        var span = new SimSpan
        {
            TraceId = trace.TraceId,
            SpanId = rng.SpanId(),
            Instance = instance,
            Name = route,
            Kind = SimSpanKind.Consumer,
            Start = start,
        };
        span.Links.Add(new SimLink { Target = producer });
        trace.Roots.Add(span);
        span.Attributes.Add("messaging.system", dep.System);
        span.Attributes.Add("messaging.destination.name", queue);
        span.Attributes.Add("messaging.operation.type", "process");
        span.Attributes.Add("messaging.message.id", producer.Attributes.Get("messaging.message.id"));
        span.Attributes.Add("server.address", dep.Host);
        span.Attributes.Add("server.port", dep.Port);

        var effect = trace.Env.Incidents.Effect(instance.Service, route, start);
        var scope = new SpanScope(trace, span, start + SpanScope.Ms(0.2), route, effect);
        body(scope);
        span.End = scope.Now + SpanScope.Ms(0.2);

        var attrs = new Tags
        {
            { "messaging.system", dep.System },
            { "messaging.operation.name", "process" },
            { "messaging.destination.name", queue },
        };
        if (scope.Failed)
        {
            span.Status = SimStatus.Error;
            span.StatusMessage ??= "Message processing failed";
            span.Attributes.Add("error.type", "ProcessingFailed");
            attrs.Add("error.type", "ProcessingFailed");
        }
        span.Measurements.Add(new SimMeasurement
        {
            Instrument = Instruments.MessagingProcessDuration,
            Value = (span.End - span.Start).TotalSeconds,
            Attributes = attrs,
        });
        return scope;
    }
}
