# Keryhe.Telemetry.Sqlite

A SQLite control plane for [Keryhe Telemetry](https://github.com/keryhe/telemetry): tenants, API keys, alert rules and retention
settings in one file. With the Parquet provider it means no database server at all. Control plane only (never a
`Database:Provider`), and every host must run on the machine that holds the file.

## Usage

```csharp
services.AddSqliteApiKeyLookup(configuration, fixedTenantId: null);           // collector host
services.AddSqliteControlPlaneManagement(configuration, fixedTenantId: null); // API host
```

Both create the file (and its directory) and migrate it forward at startup, so either can start first; a `fixedTenantId` is also created,
named `Default`, when it has no row. The Admin tool only checks, and fails until a host has created the file.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ControlPlane:Provider` | (required) | `Sqlite` |
| `ConnectionStrings:ControlPlane` | (required) | `Data Source=<absolute path>`, the same path in every host and in the Admin tool's `ConnectionStrings:Admin`; its directory (created when missing) must be writable |
| `Telemetry:ControlPlane:Sqlite:ApplySchema` | `true` | Both hosts: `false` makes the host check the file instead of creating or migrating it (and create no tenant) |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
