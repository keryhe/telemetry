# Keryhe.Telemetry.ClickHouse

ClickHouse provider for [Keryhe Telemetry](https://github.com/keryhe/telemetry) — a
ClickHouse.Client/Dapper implementation for columnar/OLAP storage. Requires **ClickHouse 25.8** (the version the
schema, the tests and the measurements use). Fresh install only: there is no migration from an earlier layout.

## What it provides

- `AddClickHouseCollectorServices(configuration)` — registers `ITelemetryBulkWriter` (batched
  `ClickHouseBulkCopy` inserts) and replaces the shared ingestion worker with a ClickHouse one that batches per day,
  connecting via `ConnectionStrings:Collector`.
- `AddClickHouseApiServices(configuration)` — registers the trace, log, metric, resource and rollup read
  repositories and `IRetentionSweeper`, connecting via `ConnectionStrings:Api`.

**ClickHouse holds telemetry data only.** Tenants, API keys, alert rules and retention settings (the control
plane) live in PostgreSQL, SQL Server or MySQL: also reference that provider's package and register its
`Add<Provider>ControlPlaneCollectorServices` / `Add<Provider>ControlPlaneApiServices`
(`ControlPlane:Provider`, `ConnectionStrings:ControlPlane`).

Install this package alongside `Keryhe.Telemetry.Collector` (write side) and/or
`Keryhe.Telemetry.Api` (read side), and set `Database:Provider` to `ClickHouse`.

## Usage

```csharp
// Collector host
builder.Services.AddClickHouseCollectorServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneCollectorServices(builder.Configuration); // or SqlServer / MySql

// API host
builder.Services.AddClickHouseApiServices(builder.Configuration);
builder.Services.AddPostgreSqlControlPlaneApiServices(builder.Configuration);
```

Configuration:

- `Database:Provider` — must be `ClickHouse`.
- `ConnectionStrings:Collector` / `ConnectionStrings:Api` — HTTP-interface connection strings, e.g.
  `Host=localhost;Port=8123;Username=default;Password=<password>;Database=telemetry`.

- `ControlPlane:Provider` — `PostgreSQL`, `SqlServer` or `MySql`; `ConnectionStrings:ControlPlane` — that database
  (both required).

Apply `schema/ClickHouse-Telemetry.sql` (or `schema/apply-schema.sh telemetry clickhouse`) to ClickHouse and the
control-plane script of your control-plane provider to its database, and create a tenant and key with the Admin tool.

## How it stores data

- **One row carries everything.** There are no reference tables: the resource and scope attributes are copied onto
  every span, log record and metric point. Attributes are `Map(LowCardinality(String), String)`, so **a value's original
  type is not kept** (an int attribute `42` is read back as the text `42`).
- **Tables** (all partitioned by day): `spans`, `log_records`, `gauge_points`, `sum_points`, `histogram_points`,
  `exp_histogram_points`, `summary_points`, plus the derived `trace_index`, `request_rollup_minute`,
  `log_rollup_minute`, `metric_catalog` and `metric_series`. Trace ids are `UUID` and span ids `UInt64`, converted to
  hex only at the API edge.
- **Timestamps are stored at 100 ns**, the driver's `DateTime64` resolution; span durations are exact.
- **Writes** are plain `MergeTree` appends. Each insert carries an `insert_deduplication_token` that is reused on a
  retry, so a retried flush is not stored twice; a client re-sending the same export is stored twice and reads tolerate it.
- **Derived data is written by the collector** after the raw insert succeeds (no materialized views). If a derived
  insert fails the raw rows stay and the cards, charts and metric catalog **under-count** that slice
  (`derived_rows_dropped`).
- **Metric "instances" are per service**, not per resource: the catalog row is one service's metric.
- **Large flushes are split** into concurrent inserts (`ParallelFlushMinRows`, `InsertPieceRows`, `MaxParallelInserts`), each
  with its own dedup token; below the threshold a flush is one insert.
- **Late data** is accepted: records are buffered per UTC day and an earlier day is flushed less often
  (`Telemetry:ClickHouse:Ingestion`). Records older than the retention window are dropped at ingest.
- **Search.** A free-text term of only letters and digits is a whole-word match through a token index
  (`pay` does not find `payments`); anything else is a substring scan. Search is limited to
  `Telemetry:Query:RawSearchWindowHours`.
- **Retention** drops whole expired day partitions (the granularity is the day); the metric catalog and series rows
  nothing has written to within the metrics window are deleted.

Because ClickHouse has no control plane here, nothing in this package needs a relational database except the
`ControlPlane:Provider` you register beside it.

## Documentation

See the [project README](https://github.com/keryhe/telemetry) and
[CLAUDE.md](https://github.com/keryhe/telemetry/blob/main/CLAUDE.md) for the full architecture
and setup guide.

## License

[MIT](https://github.com/keryhe/telemetry/blob/main/LICENSE)
