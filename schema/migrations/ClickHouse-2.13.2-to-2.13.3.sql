-- Migration: schema 2.13.2 -> 2.13.3 (ClickHouse)
--
-- Usage:
--   clickhouse-client --database telemetry --multiquery < ClickHouse-2.13.2-to-2.13.3.sql
--
-- ClickHouse DDL has no multi-statement transactions; every statement below is independently
-- idempotent (IF NOT EXISTS / ON CLUSTER-free plain ALTERs), so a re-run after a partial
-- failure is safe.
--
-- What changed, and why (see plans/list-pages-server-side.md's Phase 7 section, decision
-- 5/6/8/39):
--
--   resources.service_name: a real column, backfilled below from attributes_json's
--   "service.name" key via JSONExtractString. ResourceServiceNameExpr() is now just
--   "{alias}.service_name" on every provider. Because resources is a ReplacingMergeTree, the
--   backfill UPDATE is a mutation (ALTER TABLE ... UPDATE), matching this provider's existing
--   "control-plane is best-effort" mutation-based pattern (CLAUDE.md).
--
--   ngrambf_v1 skip indexes on spans.name / spans.status_message / log_records.body_value, and
--   tokenbf_v1 skip indexes on spans.attributes_json / log_records.attributes_json, added via
--   ALTER TABLE ... ADD INDEX + MATERIALIZE INDEX so they apply to existing parts, not just new
--   ones. The predicates themselves (FreeTextPredicate / AttributePredicate) are unchanged --
--   these only let ClickHouse skip granules that provably cannot match before evaluating the
--   real predicate.

ALTER TABLE resources ADD COLUMN IF NOT EXISTS service_name Nullable(String);

ALTER TABLE resources UPDATE service_name = JSONExtractString(assumeNotNull(attributes_json), 'service.name')
WHERE service_name IS NULL AND attributes_json IS NOT NULL;

ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_name_ngram name TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_status_message_ngram assumeNotNull(status_message) TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE spans ADD INDEX IF NOT EXISTS idx_spans_attributes_tokenbf assumeNotNull(attributes_json) TYPE tokenbf_v1(8192, 3, 0) GRANULARITY 4;
ALTER TABLE spans MATERIALIZE INDEX idx_spans_name_ngram;
ALTER TABLE spans MATERIALIZE INDEX idx_spans_status_message_ngram;
ALTER TABLE spans MATERIALIZE INDEX idx_spans_attributes_tokenbf;

ALTER TABLE log_records ADD INDEX IF NOT EXISTS idx_log_body_ngram assumeNotNull(body_value) TYPE ngrambf_v1(3, 4096, 2, 0) GRANULARITY 4;
ALTER TABLE log_records ADD INDEX IF NOT EXISTS idx_log_attributes_tokenbf assumeNotNull(attributes_json) TYPE tokenbf_v1(8192, 3, 0) GRANULARITY 4;
ALTER TABLE log_records MATERIALIZE INDEX idx_log_body_ngram;
ALTER TABLE log_records MATERIALIZE INDEX idx_log_attributes_tokenbf;

INSERT INTO schema_version (version) VALUES ('2.13.3');
