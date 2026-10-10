using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Keryhe.Telemetry.Collector;

/// <summary>
/// Makes long-lived connections end. An OTLP exporter keeps one HTTP/2 connection open for as long as it runs, so a layer-4 load balancer pins each
/// client to one collector instance for good and a new instance gets no traffic until clients restart. With
/// <see cref="TelemetryCollectorOptions.MaxConnectionAgeSeconds"/> set, a connection older than that (plus up to 10% jitter, so a fleet's clients do not
/// all reconnect at the same moment) is asked to close after the request in hand: Kestrel sends an HTTP/2 <c>GOAWAY</c> (or <c>Connection: close</c> on
/// HTTP/1.1), the in-flight requests finish, and the client reconnects, through the balancer, to whichever instance it picks.
/// </summary>
public sealed class ConnectionAgeMiddleware(RequestDelegate next, IOptions<TelemetryCollectorOptions> options)
{
    private const string AgeKey = "keryhe.telemetry.connection";

    private sealed class ConnectionAge(long startedAt, TimeSpan limit)
    {
        public readonly long StartedAt = startedAt;
        public readonly TimeSpan Limit = limit;
        public bool CloseRequested;
    }

    private readonly TimeSpan _maxAge = TimeSpan.FromSeconds(Math.Max(0, options.Value.MaxConnectionAgeSeconds));

    public Task InvokeAsync(HttpContext context)
    {
        if (_maxAge > TimeSpan.Zero
            && context.Features.Get<IConnectionItemsFeature>()?.Items is { } items
            && context.Features.Get<IConnectionLifetimeNotificationFeature>() is { } lifetime)
        {
            if (!items.TryGetValue(AgeKey, out var existing))
                items[AgeKey] = existing = new ConnectionAge(Stopwatch.GetTimestamp(), _maxAge * (1 + Random.Shared.NextDouble() * 0.1));

            var age = (ConnectionAge)existing!;
            if (!age.CloseRequested && Stopwatch.GetElapsedTime(age.StartedAt) >= age.Limit)
            {
                age.CloseRequested = true;
                lifetime.RequestClose();
            }
        }
        return next(context);
    }
}

/// <summary>Adds <see cref="ConnectionAgeMiddleware"/> at the start of the pipeline, so a host needs no extra <c>Use</c> call.</summary>
internal sealed class ConnectionAgeStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ConnectionAgeMiddleware>();
        next(app);
    };
}
