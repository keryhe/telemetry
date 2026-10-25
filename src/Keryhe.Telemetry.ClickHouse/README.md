# Keryhe.Telemetry.ClickHouse

ClickHouse telemetry provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry), for columnar storage at high volume.
Requires **ClickHouse 25.8**. It holds telemetry only: the control plane runs on PostgreSQL, SQL Server, MySQL or a SQLite file.

## Usage

```csharp
// Collector host
builder.Services.AddClickHouseCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration);   // your control-plane provider

// API host
builder.Services.AddClickHouseApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Apply `schema/apply-schema.sh telemetry clickhouse` to ClickHouse, and the control-plane script to the control-plane database.

## Configuration

| Key | Meaning |
|---|---|
| `Database:Provider` | `ClickHouse` |
| `ConnectionStrings:Collector` / `:Api` | HTTP interface, e.g. `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry` |
| `ControlPlane:Provider` / `ConnectionStrings:ControlPlane` | `PostgreSQL`, `SqlServer`, `MySql` or `Sqlite`, and its connection string |
| `Telemetry:ClickHouse:Ingestion:*` | Day buffers, linger and parallel inserts; see [CONFIGURATION.md](../../docs/CONFIGURATION.md#clickhouse-ingestion-telemetryclickhouseingestion) |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
