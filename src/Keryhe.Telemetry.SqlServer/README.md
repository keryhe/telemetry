# Keryhe.Telemetry.SqlServer

SQL Server provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry): Microsoft.Data.SqlClient and Dapper, SQL Server 2022. It can serve the telemetry data, the control
plane (tenants, API keys, alert rules, retention settings), or both.

## Usage

```csharp
// Collector host
builder.Services.AddSqlServerCollectorServices(builder.Configuration);
builder.Services.AddSqlServerControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddSqlServerApiServices(builder.Configuration);
builder.Services.AddSqlServerControlPlaneApiServices(builder.Configuration);
```

Apply the schema before first use:

```bash
schema/apply-schema.sh controlplane sqlserver
schema/apply-schema.sh telemetry sqlserver
```

## Configuration

| Key | Meaning |
|---|---|
| `Telemetry:{Collector|Api}:Provider:Database` / `…:Provider:ControlPlane` | `SqlServer` for the side(s) this package serves |
| `ConnectionStrings:Collector` / `:Api` / `:ControlPlane` | e.g. `Server=localhost;Database=telemetry;User Id=sa;Password=<password>;TrustServerCertificate=true`; `ControlPlane` is required and normally names the same database |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
