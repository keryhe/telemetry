# Keryhe.Telemetry.MySql

MySQL provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry): MySqlConnector and Dapper, MySQL 8.0.19 or later. It can serve the telemetry data, the control
plane (tenants, API keys, alert rules, retention settings), or both.

## Usage

```csharp
// Collector host
builder.Services.AddMySqlCollectorServices(builder.Configuration);
builder.Services.AddMySqlControlPlaneCollectorServices(builder.Configuration);

// API host
builder.Services.AddMySqlApiServices(builder.Configuration);
builder.Services.AddMySqlControlPlaneApiServices(builder.Configuration);
```

Apply the schema before first use:

```bash
schema/apply-schema.sh controlplane mysql
schema/apply-schema.sh telemetry mysql
```

## Configuration

| Key | Meaning |
|---|---|
| `Telemetry:{Collector|Api}:Provider:Database` / `…:Provider:ControlPlane` | `MySql` for the side(s) this package serves |
| `ConnectionStrings:Collector` / `:Api` / `:ControlPlane` | e.g. `Server=localhost;Port=3306;Database=telemetry;User ID=root;Password=<password>`; `ControlPlane` is required and normally names the same database |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
