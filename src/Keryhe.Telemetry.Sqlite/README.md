# Keryhe.Telemetry.Sqlite

A SQLite control plane for [Keryhe Telemetry](https://github.com/keryhe/telemetry): tenants, API keys, alert rules and retention
settings in one file. With the Parquet provider it means no database server at all. Control plane only (never a
`Database:Provider`), and every host must run on the machine that holds the file.

## Usage

```csharp
services.AddSqliteApiKeyLookup(configuration);           // collector host: checks the file, never creates it
services.AddSqliteControlPlaneManagement(configuration); // API host: creates and migrates the file at startup
```

Start the API host first; the collector and the Admin tool fail until the file exists at the build's schema version.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ControlPlane:Provider` | (required) | `Sqlite` |
| `ConnectionStrings:ControlPlane` | (required) | `Data Source=<absolute path>`, the same path in every host and in the Admin tool's `ConnectionStrings:Admin`; the directory must exist and be writable |
| `Telemetry:ControlPlane:Sqlite:ApplySchema` | `true` | API host only: `false` makes it check the file instead of creating or migrating it |

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
