-- Migration: schema 2.11.0 -> 2.12.0 (ClickHouse)
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-2.11.0-to-2.12.0.sql
--
-- ClickHouse DDL has no multi-statement transactions; the only statement below is already
-- idempotent, so a re-run after a partial failure is safe.
--
-- What changed, and why (see CLAUDE.md's "spans index set" notes):
--
--   2.12.0 is a no-op bump for ClickHouse alone, like 2.8.0 before it: the other four providers
--   add idx_spans_error (a status_code-filtered index on start_time_unix_nano) for mode=errors
--   trace queries, but the spans table's daily partition plus its own ORDER BY (trace_id, span_id)
--   already lets a status_code='ERROR' scan skip whole partitions/granules outside the query's
--   time bounds without a dedicated index -- see the comment on that table in
--   ClickHouse-Schema.sql.

INSERT INTO schema_version (version) VALUES ('2.12.0');
