# Keryhe.Telemetry.PostgreSQL

PostgreSQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry): Npgsql and Dapper. It can serve the telemetry data, the control
plane (tenants, API keys, alert rules, retention settings), or both.

## Usage

```csharp
// Collector host
builder.Services.AddPostgreSqlCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddPostgreSqlApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Apply the schema before first use:

```bash
schema/apply-schema.sh controlplane postgresql
schema/apply-schema.sh telemetry postgresql
```

## Configuration

| Key | Meaning |
|---|---|
| `Database:Provider` / `ControlPlane:Provider` | `PostgreSQL` for the side(s) this package serves |
| `ConnectionStrings:Collector` / `:Api` / `:ControlPlane` | e.g. `Host=localhost;Port=5432;Database=telemetry;Username=postgres;Password=<password>`; `ControlPlane` is required and normally names the same database |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
