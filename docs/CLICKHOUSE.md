# ClickHouse provider

How the ClickHouse provider ([`Keryhe.Telemetry.ClickHouse`](../src/Keryhe.Telemetry.ClickHouse/README.md)) stores telemetry. Its ingestion settings are in [CONFIGURATION.md](CONFIGURATION.md#clickhouse-ingestion-telemetryclickhouseingestion).

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
  (`Telemetry:ClickHouse:Ingestion`). Records older than their tenant's retention window (the built-in 30 days for a tenant with no control-plane row) are dropped at ingest; after a window is lengthened, records between the old and new cutoff are still dropped for up to the refresh interval.
- **Search.** A free-text term of only letters and digits is a whole-word match through a token index
  (`pay` does not find `payments`); anything else is a substring scan. Search is limited to
  `Telemetry:Query:RawSearchWindowHours`.
- **Retention** is per tenant, in whole days. Whole day partitions past the longest window in effect are dropped; for each shorter
  window one lightweight `DELETE` removes that window's tenants' rows from the days between the two cutoffs (skipped when already
  done for those days), so queries skip masked rows on the affected parts until merges remove them. The metric catalog and series
  rows nothing has written to within a tenant's metrics window are deleted.

Because ClickHouse has no control plane here, nothing in this package needs a relational database except the
`Telemetry:{Collector|Api}:Provider:ControlPlane` you register beside it.
