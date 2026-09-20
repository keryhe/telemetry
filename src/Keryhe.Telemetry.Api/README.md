# Keryhe.Telemetry.Api

REST read API for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — controllers,
multi-tenant middleware, and provider-agnostic wiring for querying ingested OpenTelemetry
traces, metrics, logs, and alert rules.

## What it provides

- `AddKeryheTelemetryApi(configuration)` — registers the telemetry controllers (as an MVC
  application part, since they live in this library) and the scoped tenant context
  (`ApiTenantContext` / `ITenantContext`). It does not register a database provider — the host
  does that separately (see Usage below).
- `UseKeryheTelemetryApi()` — adds the tenant-resolution middleware to the pipeline.

The host application still owns CORS, Swagger/OpenAPI, HTTPS redirection, and calling
`MapControllers()`.

## Usage

```csharp
builder.Services.AddKeryheTelemetryApi(builder.Configuration);

// Install the NuGet package matching your chosen provider alongside this one, e.g.
// Keryhe.Telemetry.PostgreSQL, and call its own Add<Provider>ApiServices:
builder.Services.AddPostgreSqlApiServices(builder.Configuration);

var app = builder.Build();
app.UseKeryheTelemetryApi();
app.MapControllers();
app.Run();
```

Configuration:

- `ConnectionStrings:Api` — connection string for the api (read by the provider's own
  `Add<Provider>ApiServices` call, not by `AddKeryheTelemetryApi`).

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture,
multi-tenancy model, and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
