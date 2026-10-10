using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Grpc.Net.Client;
using Keryhe.Telemetry.Collector.Authentication;
using Keryhe.Telemetry.Core;
using Keryhe.Telemetry.Core.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keryhe.Telemetry.IntegrationTests.CollectorAuth;

/// <summary>
/// An in-process host (TestServer) running the real <c>AddKeryheTelemetryCollector</c> / <c>MapKeryheTelemetryCollector</c>
/// registration over a fake <see cref="IApiKeyLookup"/> and a controllable clock (collector-authentication plan,
/// decision 15). No database, no Docker. The database-touching workers are not started, so the ingestion channel can be
/// read directly: what is in it is what got past authentication into a service.
/// </summary>
public sealed class CollectorHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<(string Signal, string Reason), long> _authFailures = new();

    public FakeLookup Lookup { get; }
    public TestClock Clock { get; }
    public ConcurrentQueue<string> LogLines { get; }
    public TelemetryIngestionChannel Ingestion => _app.Services.GetRequiredService<TelemetryIngestionChannel>();
    public GrpcChannel Channel { get; }
    public HttpClient Http { get; }

    public IReadOnlyDictionary<(string Signal, string Reason), long> AuthFailures => _authFailures;
    public long AuthFailureCount(string signal, string reason) => _authFailures.GetValueOrDefault((signal, reason));
    public long TotalAuthFailures => _authFailures.Values.Sum();

    private CollectorHost(WebApplication app, FakeLookup lookup, TestClock clock, ConcurrentQueue<string> lines)
    {
        _app = app;
        Lookup = lookup;
        Clock = clock;
        LogLines = lines;
        var server = app.GetTestServer();
        Http = server.CreateClient();
        Channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = server.CreateHandler() });

        // Only this host's own meter: other hosts running in parallel in the same process publish an instrument of the same name.
        var ownMeter = app.Services.GetRequiredService<IngestionMetrics>().Meter;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, ownMeter) && instrument.Name.EndsWith("auth_failures"))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string signal = "", reason = "";
            foreach (var t in tags)
            {
                if (t.Key == "signal") signal = (string)t.Value!;
                if (t.Key == "reason") reason = (string)t.Value!;
            }
            _authFailures.AddOrUpdate((signal, reason), value, (_, v) => v + value);
        });
        _listener.Start();
    }

    public static async Task<CollectorHost> StartAsync(Dictionary<string, string?>? config = null, Action<WebApplication>? map = null, bool fakeClientAddresses = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config ?? new());

        var lines = new ConcurrentQueue<string>();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new Capture(lines));

        builder.Services.AddKeryheTelemetryCollector(builder.Configuration);

        // Nothing here touches a database: drop the workers (they need bulk writers and touch stores).
        foreach (var d in builder.Services.Where(d => d.ServiceType == typeof(IHostedService)
                     && d.ImplementationType?.Namespace?.StartsWith("Keryhe.Telemetry") == true).ToList())
            builder.Services.Remove(d);

        var lookup = new FakeLookup();
        var clock = new TestClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        builder.Services.AddSingleton<IApiKeyLookup>(lookup);
        builder.Services.AddSingleton<TimeProvider>(clock);

        var app = builder.Build();
        // TestServer has no client address. With this on, a request's X-Test-IP header becomes its remote address, so the per-address
        // failure limit can be exercised for several clients.
        if (fakeClientAddresses)
            app.Use((ctx, next) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-IP", out var ip) && System.Net.IPAddress.TryParse(ip.ToString(), out var address))
                    ctx.Connection.RemoteIpAddress = address;
                return next();
            });
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapKeryheTelemetryCollector();
        map?.Invoke(app);
        await app.StartAsync();

        return new CollectorHost(app, lookup, clock, lines);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        Channel.Dispose();
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed class FakeLookup : IApiKeyLookup
    {
        private readonly ConcurrentDictionary<string, ApiKeyLookupResult> _rows = new();
        public bool Throw { get; set; }
        public TimeSpan Delay { get; set; }
        public int Calls;
        public int MaxConcurrent;
        private int _running;

        public void Add(string plainKey, long tenantId, long apiKeyId = 1, DateTimeOffset? expiresAt = null) =>
            _rows[ApiKeyAuthenticationHandler.ComputeKeyHash(plainKey)] = new ApiKeyLookupResult(tenantId, apiKeyId, expiresAt);

        public void Remove(string plainKey) => _rows.TryRemove(ApiKeyAuthenticationHandler.ComputeKeyHash(plainKey), out _);

        public async Task<ApiKeyLookupResult?> LookupAsync(string keyHash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var running = Interlocked.Increment(ref _running);
            try
            {
                int seen;
                while (running > (seen = Volatile.Read(ref MaxConcurrent))) if (Interlocked.CompareExchange(ref MaxConcurrent, running, seen) == seen) break;
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, CancellationToken.None);
                if (Throw) throw new InvalidOperationException("database unreachable");
                return _rows.TryGetValue(keyHash, out var r) ? r : null;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    public sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        // The monotonic timestamp follows the same controllable time, so token buckets (the failed-attempt limiter) do not refill on real time.
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Capture(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new L(categoryName, lines);
        public void Dispose() { }
        private sealed class L(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
